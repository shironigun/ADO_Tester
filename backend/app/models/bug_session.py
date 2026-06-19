from __future__ import annotations

from datetime import datetime
from enum import Enum

from sqlalchemy import DateTime, ForeignKey, Integer, String
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db import Base


class BugSessionStatus(str, Enum):
    Analyzing = "Analyzing"
    AwaitingActual = "AwaitingActual"
    Verifying = "Verifying"          # static code-analysis against generated test cases
    Drafting = "Drafting"
    Drafted = "Drafted"
    Pushing = "Pushing"
    Done = "Done"
    Failed = "Failed"
    Cancelled = "Cancelled"


class BugDraftStatus(str, Enum):
    Draft = "Draft"
    Approved = "Approved"
    Rejected = "Rejected"
    Pushed = "Pushed"


class BugSeverity(str, Enum):
    Critical = "Critical"
    High = "High"
    Medium = "Medium"
    Low = "Low"


class BugSession(Base):
    __tablename__ = "BugSessions"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    project_id: Mapped[int] = mapped_column("ProjectId", Integer, nullable=False)
    work_item_id: Mapped[int] = mapped_column("WorkItemId", Integer, nullable=False)
    work_item_title: Mapped[str] = mapped_column("WorkItemTitle", String, nullable=False, default="")
    status: Mapped[BugSessionStatus] = mapped_column("Status", String, nullable=False, default=BugSessionStatus.Analyzing)
    current_activity: Mapped[str | None] = mapped_column("CurrentActivity", String, nullable=True)
    analysis_json: Mapped[str | None] = mapped_column("AnalysisJson", String, nullable=True)
    actual_behavior: Mapped[str | None] = mapped_column("ActualBehavior", String, nullable=True)
    code_context: Mapped[str | None] = mapped_column("CodeContext", String, nullable=True)
    error_message: Mapped[str | None] = mapped_column("ErrorMessage", String, nullable=True)
    event_log_json: Mapped[str | None] = mapped_column("EventLogJson", String, nullable=True)
    # Per-test-case static code-verification verdicts as a JSON array.
    code_verification_json: Mapped[str | None] = mapped_column("CodeVerificationJson", String, nullable=True)
    # Set when this session is part of an Epic/Feature run (a Batch). NULL for single runs.
    batch_id: Mapped[int | None] = mapped_column(
        "BatchId", Integer, ForeignKey("Batches.Id", ondelete="SET NULL"), index=True, nullable=True)
    created_at: Mapped[datetime] = mapped_column("CreatedAt", DateTime, nullable=False)
    updated_at: Mapped[datetime] = mapped_column("UpdatedAt", DateTime, nullable=False)

    drafts: Mapped[list["BugDraft"]] = relationship(
        back_populates="session", cascade="all, delete-orphan", lazy="selectin")


class BugDraft(Base):
    __tablename__ = "BugDrafts"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    session_id: Mapped[int] = mapped_column(
        "SessionId", Integer, ForeignKey("BugSessions.Id", ondelete="CASCADE"), index=True, nullable=False)
    title: Mapped[str] = mapped_column("Title", String, nullable=False, default="")
    severity: Mapped[BugSeverity] = mapped_column("Severity", String, nullable=False, default=BugSeverity.Medium)
    preconditions: Mapped[str] = mapped_column("Preconditions", String, nullable=False, default="")
    repro_steps_json: Mapped[str] = mapped_column("ReproStepsJson", String, nullable=False, default="[]")
    expected_behavior: Mapped[str] = mapped_column("ExpectedBehavior", String, nullable=False, default="")
    actual_behavior: Mapped[str] = mapped_column("ActualBehavior", String, nullable=False, default="")
    suggested_fix: Mapped[str] = mapped_column("SuggestedFix", String, nullable=False, default="")
    status: Mapped[BugDraftStatus] = mapped_column("Status", String, nullable=False, default=BugDraftStatus.Draft)
    ado_bug_id: Mapped[int | None] = mapped_column("AdoBugId", Integer, nullable=True)
    sort_order: Mapped[int] = mapped_column("SortOrder", Integer, nullable=False, default=0)

    session: Mapped[BugSession] = relationship(back_populates="drafts")
