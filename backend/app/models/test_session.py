from __future__ import annotations

from datetime import datetime
from enum import Enum

from sqlalchemy import DateTime, ForeignKey, Integer, String
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db import Base


class SessionStatus(str, Enum):
    """JSON serializes as the exact string the React UI keys off."""
    Analyzing = "Analyzing"          # Analyst working
    AwaitingTester = "AwaitingTester"  # Analyst done, manual mode — user clicks Run Tester
    Drafting = "Drafting"            # Tester running
    Drafted = "Drafted"              # Test cases drafted, awaiting review
    Pushing = "Pushing"
    Done = "Done"
    Failed = "Failed"
    Cancelled = "Cancelled"


class DraftStatus(str, Enum):
    Draft = "Draft"
    Approved = "Approved"
    Rejected = "Rejected"
    Pushed = "Pushed"


class TestSession(Base):
    __tablename__ = "TestSessions"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    project_id: Mapped[int] = mapped_column("ProjectId", Integer, nullable=False)
    work_item_id: Mapped[int] = mapped_column("WorkItemId", Integer, nullable=False)
    work_item_title: Mapped[str] = mapped_column("WorkItemTitle", String, nullable=False, default="")
    status: Mapped[SessionStatus] = mapped_column("Status", String, nullable=False, default=SessionStatus.Analyzing)
    current_activity: Mapped[str | None] = mapped_column("CurrentActivity", String, nullable=True)
    analysis_json: Mapped[str | None] = mapped_column("AnalysisJson", String, nullable=True)
    error_message: Mapped[str | None] = mapped_column("ErrorMessage", String, nullable=True)
    # Persisted narrative of the live console (non-token events) as a JSON array — replays a
    # finished run's activity feed. Nullable; never holds raw model tokens.
    event_log_json: Mapped[str | None] = mapped_column("EventLogJson", String, nullable=True)
    created_at: Mapped[datetime] = mapped_column("CreatedAt", DateTime, nullable=False)
    updated_at: Mapped[datetime] = mapped_column("UpdatedAt", DateTime, nullable=False)

    drafts: Mapped[list["TestCaseDraft"]] = relationship(
        back_populates="session", cascade="all, delete-orphan", lazy="selectin")


class TestCaseDraft(Base):
    __tablename__ = "TestCaseDrafts"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    session_id: Mapped[int] = mapped_column(
        "SessionId", Integer, ForeignKey("TestSessions.Id", ondelete="CASCADE"), index=True, nullable=False)
    title: Mapped[str] = mapped_column("Title", String, nullable=False, default="")
    preconditions: Mapped[str] = mapped_column("Preconditions", String, nullable=False, default="")
    steps_json: Mapped[str] = mapped_column("StepsJson", String, nullable=False, default="[]")
    status: Mapped[DraftStatus] = mapped_column("Status", String, nullable=False, default=DraftStatus.Draft)
    ado_test_case_id: Mapped[int | None] = mapped_column("AdoTestCaseId", Integer, nullable=True)
    sort_order: Mapped[int] = mapped_column("SortOrder", Integer, nullable=False, default=0)

    session: Mapped[TestSession] = relationship(back_populates="drafts")
