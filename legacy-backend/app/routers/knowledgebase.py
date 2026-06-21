"""
Knowledgebase router — mirrors the .NET KnowledgebaseController.
  GET    /api/knowledgebase/{projectId}              → list docs
  POST   /api/knowledgebase/{projectId}              → upload one or more .md (multipart)
  DELETE /api/knowledgebase/doc/{documentId}
  GET    /api/knowledgebase/{projectId}/search?q&k   → BM25 retrieval
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, Depends, File, HTTPException, UploadFile
from sqlalchemy.ext.asyncio import AsyncSession

from app.db import get_session
from app.schemas import KnowledgebaseDocumentSchema, KnowledgebaseHitSchema
from app.services.knowledgebase import KnowledgebaseService, get_knowledgebase_service

MAX_FILE_BYTES = 10_000_000  # 10 MB per file — matches the .NET cap
MAX_K = 50

router = APIRouter(prefix="/api/knowledgebase", tags=["knowledgebase"])
log = logging.getLogger("qa.kb")


def _service() -> KnowledgebaseService:
    return get_knowledgebase_service()


@router.get("/{project_id}", response_model=list[KnowledgebaseDocumentSchema])
async def list_docs(
    project_id: int,
    session: AsyncSession = Depends(get_session),
    svc: KnowledgebaseService = Depends(_service),
) -> list[KnowledgebaseDocumentSchema]:
    try:
        docs = await svc.list_(session, project_id)
        return [KnowledgebaseDocumentSchema.model_validate(d) for d in docs]
    except Exception as e:  # noqa: BLE001
        log.exception("KB list failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.post("/{project_id}", response_model=list[KnowledgebaseDocumentSchema])
async def upload(
    project_id: int,
    files: list[UploadFile] = File(...),
    session: AsyncSession = Depends(get_session),
    svc: KnowledgebaseService = Depends(_service),
) -> list[KnowledgebaseDocumentSchema]:
    if not files:
        raise HTTPException(status_code=400, detail="No files uploaded.")

    results = []
    try:
        for file in files:
            name = file.filename or ""
            if not name.lower().endswith(".md"):
                raise HTTPException(
                    status_code=400, detail=f"Only .md files are accepted ('{name}').")

            raw = await file.read()
            if len(raw) > MAX_FILE_BYTES:
                raise HTTPException(
                    status_code=400,
                    detail=f"'{name}' is {len(raw)//1024} KB — max per file is {MAX_FILE_BYTES//1024} KB.")

            markdown = raw.decode("utf-8", errors="replace")
            doc = await svc.upload(session, project_id, name, markdown)
            results.append(KnowledgebaseDocumentSchema.model_validate(doc))
        return results
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("KB upload failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.delete("/doc/{document_id}", status_code=204)
async def delete_doc(
    document_id: int,
    session: AsyncSession = Depends(get_session),
    svc: KnowledgebaseService = Depends(_service),
) -> None:
    try:
        await svc.delete(session, document_id)
    except Exception as e:  # noqa: BLE001
        log.exception("KB delete failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/{project_id}/search", response_model=list[KnowledgebaseHitSchema])
async def search(
    project_id: int,
    q: str,
    k: int = 8,
    session: AsyncSession = Depends(get_session),
    svc: KnowledgebaseService = Depends(_service),
) -> list[KnowledgebaseHitSchema]:
    if not q or len(q.strip()) < 2:
        raise HTTPException(status_code=400, detail="Query must be at least 2 characters.")
    k = max(1, min(k, MAX_K))
    try:
        hits = await svc.search(session, project_id, q, k)
        return [KnowledgebaseHitSchema.model_validate(h) for h in hits]
    except Exception as e:  # noqa: BLE001
        log.exception("KB search failed")
        raise HTTPException(status_code=500, detail=str(e)) from None
