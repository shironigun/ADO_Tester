"""
Knowledgebase service — ports the .NET KnowledgebaseService verbatim.

Stores uploaded .md on disk, chunks (~1000 char paragraphs) into the SQLite FTS5 `doc_chunks`
virtual table, and runs lexical (BM25) retrieval against it.
"""
from __future__ import annotations

import os
import re
import uuid
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from sqlalchemy import delete, select, text
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import settings
from app.models import KnowledgebaseDocument

TARGET_CHUNK_CHARS = 1000
_TOKEN_RE = re.compile(r"[A-Za-z0-9_]+")


@dataclass(frozen=True)
class KnowledgebaseHit:
    document_id: int
    file_name: str
    content: str
    score: float  # lower bm25 = better


class KnowledgebaseService:
    def __init__(self) -> None:
        self._kb_root: Path = settings.data_path / settings.knowledgebase_folder_name
        self._kb_root.mkdir(parents=True, exist_ok=True)

    # ── chunking ────────────────────────────────────────────────────────────

    @staticmethod
    def _chunk_markdown(markdown: str) -> list[str]:
        paragraphs = [p for p in markdown.replace("\r\n", "\n").split("\n\n") if p]
        if not paragraphs:
            return [markdown.strip()]
        chunks: list[str] = []
        buf = ""
        for para in paragraphs:
            if buf and len(buf) + len(para) > TARGET_CHUNK_CHARS:
                chunks.append(buf.strip())
                buf = ""
            buf += para + "\n\n"
        if buf:
            chunks.append(buf.strip())
        return chunks or [markdown.strip()]

    @staticmethod
    def _match_expression(query: str) -> str | None:
        tokens = [m.group(0) for m in _TOKEN_RE.finditer(query) if len(m.group(0)) > 1]
        # de-dup preserving order, cap 20
        seen: set[str] = set()
        unique: list[str] = []
        for t in tokens:
            if t not in seen:
                seen.add(t)
                unique.append(t)
            if len(unique) >= 20:
                break
        if not unique:
            return None
        return " OR ".join(f'"{t}"' for t in unique)

    # ── ops ─────────────────────────────────────────────────────────────────

    async def upload(
        self,
        session: AsyncSession,
        project_id: int,
        file_name: str,
        markdown: str,
    ) -> KnowledgebaseDocument:
        project_dir = self._kb_root / str(project_id)
        project_dir.mkdir(parents=True, exist_ok=True)

        safe_name = os.path.basename(file_name)
        stored_path = project_dir / f"{uuid.uuid4().hex}-{safe_name}"
        stored_path.write_text(markdown, encoding="utf-8")

        chunks = self._chunk_markdown(markdown)

        doc = KnowledgebaseDocument(
            project_id=project_id,
            file_name=safe_name,
            stored_path=str(stored_path),
            chunk_count=len(chunks),
            size_bytes=len(markdown.encode("utf-8")),
            uploaded_at=datetime.utcnow(),
        )
        session.add(doc)
        await session.flush()  # populate doc.id without ending the transaction

        for chunk in chunks:
            await session.execute(
                text("INSERT INTO doc_chunks (content, project_id, doc_id) VALUES (:c, :p, :d)"),
                {"c": chunk, "p": project_id, "d": doc.id},
            )
        await session.commit()
        await session.refresh(doc)
        return doc

    async def list_(
        self, session: AsyncSession, project_id: int
    ) -> list[KnowledgebaseDocument]:
        result = await session.execute(
            select(KnowledgebaseDocument)
            .where(KnowledgebaseDocument.project_id == project_id)
            .order_by(KnowledgebaseDocument.uploaded_at.desc())
        )
        return list(result.scalars().all())

    async def delete(self, session: AsyncSession, document_id: int) -> None:
        doc = await session.get(KnowledgebaseDocument, document_id)
        if doc is None:
            return

        await session.execute(
            text("DELETE FROM doc_chunks WHERE doc_id = :d"),
            {"d": document_id},
        )

        try:
            if doc.stored_path and os.path.exists(doc.stored_path):
                os.remove(doc.stored_path)
        except OSError:
            pass  # file gone is fine; keep DB delete idempotent

        await session.execute(
            delete(KnowledgebaseDocument).where(KnowledgebaseDocument.id == document_id)
        )
        await session.commit()

    async def search(
        self,
        session: AsyncSession,
        project_id: int,
        query: str,
        top_k: int = 8,
    ) -> list[KnowledgebaseHit]:
        match_expr = self._match_expression(query)
        if match_expr is None:
            return []

        # Pull doc IDs → filenames once so we don't N+1 the result mapping.
        rows = (await session.execute(
            select(KnowledgebaseDocument.id, KnowledgebaseDocument.file_name)
            .where(KnowledgebaseDocument.project_id == project_id)
        )).all()
        file_names: dict[int, str] = {r[0]: r[1] for r in rows}

        sql = text(
            """
            SELECT doc_id, content, bm25(doc_chunks) AS score
            FROM doc_chunks
            WHERE doc_chunks MATCH :q AND project_id = :p
            ORDER BY score
            LIMIT :k
            """
        )
        result = await session.execute(sql, {"q": match_expr, "p": project_id, "k": top_k})
        hits: list[KnowledgebaseHit] = []
        for doc_id, content, score in result.all():
            hits.append(KnowledgebaseHit(
                document_id=int(doc_id),
                file_name=file_names.get(int(doc_id), "(unknown)"),
                content=str(content),
                score=float(score),
            ))
        return hits


_singleton: KnowledgebaseService | None = None


def get_knowledgebase_service() -> KnowledgebaseService:
    global _singleton
    if _singleton is None:
        _singleton = KnowledgebaseService()
    return _singleton
