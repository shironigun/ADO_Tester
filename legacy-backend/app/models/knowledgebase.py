from __future__ import annotations

from datetime import datetime

from sqlalchemy import DateTime, ForeignKey, Integer, String
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base


class KnowledgebaseDocument(Base):
    __tablename__ = "Documents"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    project_id: Mapped[int] = mapped_column(
        "ProjectId", Integer, ForeignKey("Projects.Id", ondelete="CASCADE"), index=True, nullable=False)
    file_name: Mapped[str] = mapped_column("FileName", String, nullable=False)
    stored_path: Mapped[str] = mapped_column("StoredPath", String, nullable=False, default="")
    chunk_count: Mapped[int] = mapped_column("ChunkCount", Integer, nullable=False, default=0)
    size_bytes: Mapped[int] = mapped_column("SizeBytes", Integer, nullable=False, default=0)
    uploaded_at: Mapped[datetime] = mapped_column("UploadedAt", DateTime, nullable=False)
