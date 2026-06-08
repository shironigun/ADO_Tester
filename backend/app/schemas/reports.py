from __future__ import annotations

from datetime import datetime

from app.models import ReportStatus, ReportTheme
from app.schemas.base import CamelModel


class GenerateReportRequest(CamelModel):
    from_utc: datetime
    to_utc: datetime
    boards: list[str] | None = None
    theme: ReportTheme = ReportTheme.Executive


class GenerateReportResponse(CamelModel):
    report_id: int


class ReportSchema(CamelModel):
    id: int
    project_id: int
    from_utc: datetime
    to_utc: datetime
    boards: str
    ticket_count: int
    status: ReportStatus
    html_path: str | None
    error_message: str | None
    created_at: datetime
    title: str
    trigger: str
    theme: ReportTheme
