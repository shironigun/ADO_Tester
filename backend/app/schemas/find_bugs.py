from __future__ import annotations

from datetime import datetime

from app.models import BugDraftStatus, BugSessionStatus, BugSeverity
from app.schemas.base import CamelModel


class BugStartRequest(CamelModel):
    work_item_id: int


class BugStartResponse(CamelModel):
    session_id: int


class BugRunRequest(CamelModel):
    actual_behavior: str
    code_context: str | None = None


class BugDraftSchema(CamelModel):
    id: int
    session_id: int
    title: str
    severity: BugSeverity
    preconditions: str
    repro_steps_json: str
    expected_behavior: str
    actual_behavior: str
    suggested_fix: str
    status: BugDraftStatus
    ado_bug_id: int | None
    sort_order: int


class BugSessionDetailSchema(CamelModel):
    id: int
    project_id: int
    work_item_id: int
    work_item_title: str
    status: BugSessionStatus
    current_activity: str | None
    analysis_json: str | None
    actual_behavior: str | None
    code_context: str | None
    error_message: str | None
    created_at: datetime
    updated_at: datetime
    drafts: list[BugDraftSchema] = []


class BugSessionSummary(CamelModel):
    id: int
    work_item_id: int
    work_item_title: str
    status: BugSessionStatus
    created_at: datetime
    error_message: str | None


class BugSessionList(CamelModel):
    total: int
    items: list[BugSessionSummary]


class UpdateBugDraftRequest(CamelModel):
    title: str | None = None
    severity: BugSeverity | None = None
    preconditions: str | None = None
    repro_steps_json: str | None = None
    expected_behavior: str | None = None
    actual_behavior: str | None = None
    suggested_fix: str | None = None
    status: BugDraftStatus | None = None
