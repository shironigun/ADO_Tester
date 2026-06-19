"""Schemas for Epic/Feature runs (batches) — camelCase via CamelModel."""
from __future__ import annotations

from datetime import datetime

from app.schemas.base import CamelModel


class BatchChild(CamelModel):
    """A work item returned by children-expand (a candidate story to run)."""
    id: int
    title: str
    work_item_type: str = ""
    state: str | None = None


class ChildrenExpandResponse(CamelModel):
    parent_work_item_id: int
    parent_title: str = ""
    children: list[BatchChild] = []
    truncated: bool = False


class BatchStartRequest(CamelModel):
    parent_work_item_id: int | None = None       # the Epic/Feature it was launched from (display)
    work_item_ids: list[int] = []                # explicit story ids to run
    mode: str | None = None                      # "auto" | "manual" — find-bugs only


class BatchStartResponse(CamelModel):
    batch_id: int
    session_ids: list[int] = []
    skipped: list[int] = []                       # ids that produced no session (dupes/invalid)


class BatchChildSummary(CamelModel):
    session_id: int
    work_item_id: int
    work_item_title: str
    status: str
    current_activity: str | None = None
    error_message: str | None = None
    count: int = 0                                # tests generated (author) / bugs found (find-bugs)


class BatchSummary(CamelModel):
    id: int
    module: str
    parent_work_item_id: int | None = None
    title: str
    mode: str | None = None
    created_at: datetime
    child_count: int = 0
    done_count: int = 0
    failed_count: int = 0
    running_count: int = 0
    derived_status: str = ""


class BatchList(CamelModel):
    total: int
    items: list[BatchSummary]


class BatchDetail(CamelModel):
    id: int
    module: str
    parent_work_item_id: int | None = None
    title: str
    mode: str | None = None
    created_at: datetime
    updated_at: datetime
    derived_status: str = ""
    children: list[BatchChildSummary] = []
