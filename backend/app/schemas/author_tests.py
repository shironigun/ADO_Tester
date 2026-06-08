from __future__ import annotations

from datetime import datetime

from app.models import DraftStatus, SessionStatus
from app.schemas.base import CamelModel


# ── Test Plan / Suite (ADO) ─────────────────────────────────────────────────


class TestPlanSchema(CamelModel):
    id: int
    name: str


class TestSuiteSchema(CamelModel):
    id: int
    name: str


# ── Pipeline ────────────────────────────────────────────────────────────────


class StartRequest(CamelModel):
    work_item_id: int
    auto_handoff: bool = True


class StartResponse(CamelModel):
    session_id: int


class CancelResponse(CamelModel):
    cancelled: bool


# ── Sessions / Drafts ───────────────────────────────────────────────────────


class TestCaseDraftSchema(CamelModel):
    id: int
    session_id: int
    title: str
    preconditions: str
    steps_json: str
    status: DraftStatus
    ado_test_case_id: int | None
    sort_order: int


class TestSessionDetailSchema(CamelModel):
    id: int
    project_id: int
    work_item_id: int
    work_item_title: str
    status: SessionStatus
    current_activity: str | None
    analysis_json: str | None
    error_message: str | None
    event_log_json: str | None = None
    created_at: datetime
    updated_at: datetime
    drafts: list[TestCaseDraftSchema] = []


class TestSessionSummary(CamelModel):
    id: int
    work_item_id: int
    work_item_title: str
    status: SessionStatus
    created_at: datetime
    error_message: str | None


class TestSessionList(CamelModel):
    total: int
    items: list[TestSessionSummary]


# ── Draft editing ───────────────────────────────────────────────────────────


class UpdateDraftRequest(CamelModel):
    title: str | None = None
    preconditions: str | None = None
    steps_json: str | None = None
    status: DraftStatus | None = None


class ApproveAllResponse(CamelModel):
    approved_count: int


# ── Push to ADO ─────────────────────────────────────────────────────────────


class PushRequest(CamelModel):
    test_plan_id: int
    test_suite_id: int


class AcceptedResponse(CamelModel):
    started: bool
