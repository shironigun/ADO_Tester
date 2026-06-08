from __future__ import annotations

from datetime import datetime
from enum import Enum

from sqlalchemy import DateTime, Integer, String
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base


class ReportStatus(str, Enum):
    Generating = "Generating"
    Done = "Done"
    Failed = "Failed"


class ReportTheme(str, Enum):
    """Visual identity applied to the rendered report — picked per generation."""
    Executive = "Executive"      # light, minimal, big numbers, serif headlines
    Standup   = "Standup"        # dark slate, neon accents, tight layout
    Branded   = "Branded"        # YourOrg corporate palette, logo lockup


class Report(Base):
    __tablename__ = "Reports"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    project_id: Mapped[int] = mapped_column("ProjectId", Integer, nullable=False)
    from_utc: Mapped[datetime] = mapped_column("FromUtc", DateTime, nullable=False)
    to_utc: Mapped[datetime] = mapped_column("ToUtc", DateTime, nullable=False)
    boards: Mapped[str] = mapped_column("Boards", String, nullable=False, default="")
    ticket_count: Mapped[int] = mapped_column("TicketCount", Integer, nullable=False, default=0)
    status: Mapped[ReportStatus] = mapped_column("Status", String, nullable=False, default=ReportStatus.Generating)
    html_path: Mapped[str | None] = mapped_column("HtmlPath", String, nullable=True)
    error_message: Mapped[str | None] = mapped_column("ErrorMessage", String, nullable=True)
    created_at: Mapped[datetime] = mapped_column("CreatedAt", DateTime, nullable=False)
    title: Mapped[str] = mapped_column("Title", String, nullable=False, default="")
    trigger: Mapped[str] = mapped_column("Trigger", String, nullable=False, default="Manual")
    theme: Mapped[ReportTheme] = mapped_column(
        "Theme", String, nullable=False, default=ReportTheme.Executive
    )
    # Structured AI insights (TL;DR bullets, theme clusters, speaker script) as JSON text.
    summary_json: Mapped[str | None] = mapped_column("SummaryJson", String, nullable=True)
