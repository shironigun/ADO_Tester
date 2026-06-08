from __future__ import annotations

from datetime import datetime

from app.schemas.base import CamelModel


class KnowledgebaseDocumentSchema(CamelModel):
    id: int
    project_id: int
    file_name: str
    stored_path: str
    chunk_count: int
    size_bytes: int
    uploaded_at: datetime


class KnowledgebaseHitSchema(CamelModel):
    document_id: int
    file_name: str
    content: str
    score: float
