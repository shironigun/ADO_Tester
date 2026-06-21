from __future__ import annotations

from datetime import datetime
from enum import Enum

from sqlalchemy import DateTime, Integer, String
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base


class BatchModule(str, Enum):
    AuthorTests = "author-tests"
    FindBugs = "find-bugs"


class BatchMode(str, Enum):
    """Find-Bugs only: Auto runs Analyst→Code-Verify→Bug-Writer end-to-end per story;
    Manual parks each child at AwaitingActual for hands-on verification."""
    Auto = "auto"
    Manual = "manual"


class Batch(Base):
    """An Epic/Feature run — a grouping over N child sessions (one per story). Each child is a
    normal TestSession / BugSession carrying this batch's id. The batch's status is DERIVED from
    its children at read time (not stored), so child status transitions never write this row."""
    __tablename__ = "Batches"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    project_id: Mapped[int] = mapped_column("ProjectId", Integer, nullable=False)
    module: Mapped[BatchModule] = mapped_column("Module", String, nullable=False)
    # The Epic/Feature the run was launched from (provenance/display only; may be None for an
    # ad-hoc multi-select of individual stories).
    parent_work_item_id: Mapped[int | None] = mapped_column("ParentWorkItemId", Integer, nullable=True)
    title: Mapped[str] = mapped_column("Title", String, nullable=False, default="")
    mode: Mapped[BatchMode | None] = mapped_column("Mode", String, nullable=True)  # find-bugs only
    created_at: Mapped[datetime] = mapped_column("CreatedAt", DateTime, nullable=False)
    updated_at: Mapped[datetime] = mapped_column("UpdatedAt", DateTime, nullable=False)
