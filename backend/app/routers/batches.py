"""Epic/Feature runs (batches) — fan out one session per story, grouped under a Batch.

  GET  /api/batches/children-expand?parentWorkItemId=&recursive=   expand an Epic/Feature
  POST /api/batches/{module}/{projectId}/start                     create batch + N child sessions
  GET  /api/batches/{projectId}                                    list batches (derived rollup)
  GET  /api/batches/detail/{batchId}                               batch detail (per-story rollup)
  POST /api/batches/{batchId}/cancel                               cancel in-flight + queued children
"""
from __future__ import annotations

import asyncio
import logging
from datetime import datetime

from fastapi import APIRouter, Depends, HTTPException, Query
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.db import get_session
from app.models import Batch, BugDraft, BugSession, TestCaseDraft, TestSession
from app.schemas import (
    BatchChild,
    BatchChildSummary,
    BatchDetail,
    BatchList,
    BatchStartRequest,
    BatchStartResponse,
    BatchSummary,
    CancelResponse,
    ChildrenExpandResponse,
)
from app.services.ado_client import AdoApiError, get_ado_client
from app.services.orchestrators import get_agent_orchestrator, get_bug_orchestrator

router = APIRouter(prefix="/api/batches", tags=["batches"])
log = logging.getLogger("qa.batches")

# module → (session model, draft model, orchestrator getter)
_MODULES = {
    "author-tests": (TestSession, TestCaseDraft, get_agent_orchestrator),
    "find-bugs": (BugSession, BugDraft, get_bug_orchestrator),
}

_DONE = {"Done", "Drafted"}
# User-wait states: the pipeline finished its automatic work and is parked for hands-on input
# (Find Bugs Manual → AwaitingActual; Author Tests Manual → AwaitingTester). These are NOT
# in-flight work, so a batch with all children parked must settle, not poll forever.
_AWAITING = {"AwaitingActual", "AwaitingTester"}
_TERMINAL = {"Done", "Drafted", "Failed", "Cancelled"}


def _classify(statuses: list[str]) -> dict:
    """Derive a batch's rollup from its children's statuses."""
    total = len(statuses)
    done = sum(1 for s in statuses if s in _DONE)
    failed = sum(1 for s in statuses if s == "Failed")
    cancelled = sum(1 for s in statuses if s == "Cancelled")
    awaiting = sum(1 for s in statuses if s in _AWAITING)
    running = total - done - failed - cancelled - awaiting
    if total == 0:
        derived = "Empty"
    elif running > 0:
        derived = "Running"
    elif awaiting > 0:
        derived = "AwaitingInput"  # all automatic work done; parked for manual verification
    elif failed > 0:
        derived = "CompletedWithFailures"
    elif done == 0 and cancelled > 0:
        derived = "Cancelled"
    else:
        derived = "Completed"
    return {"child_count": total, "done_count": done, "failed_count": failed,
            "running_count": running, "derived_status": derived}


@router.get("/children-expand", response_model=ChildrenExpandResponse)
async def children_expand(
    parent_work_item_id: int = Query(alias="parentWorkItemId"),
    recursive: bool = True,
) -> ChildrenExpandResponse:
    """Expand an Epic/Feature to its candidate child stories (BFS through Epic/Feature, collecting
    User Story/Bug/Issue leaves when recursive)."""
    ado = get_ado_client()
    try:
        parent = await ado.get_work_item(parent_work_item_id)
        children, truncated = await ado.get_child_work_items(parent_work_item_id, recursive=recursive)
    except AdoApiError as e:
        if e.status == 404:
            raise HTTPException(status_code=404, detail=f"Work item #{parent_work_item_id} not found.") from None
        raise HTTPException(status_code=502, detail=str(e)) from None
    return ChildrenExpandResponse(
        parent_work_item_id=parent_work_item_id,
        parent_title=parent.title,
        children=[BatchChild(id=c.id, title=c.title, work_item_type=c.work_item_type, state=c.state)
                  for c in children],
        truncated=truncated,
    )


@router.post("/{module}/{project_id}/start", response_model=BatchStartResponse)
async def start_batch(
    module: str, project_id: int, req: BatchStartRequest,
    session: AsyncSession = Depends(get_session),
) -> BatchStartResponse:
    if module not in _MODULES:
        raise HTTPException(status_code=404, detail="Unknown module.")
    ids = list(dict.fromkeys(i for i in req.work_item_ids if i and i > 0))  # de-dupe, drop invalid
    if not ids:
        raise HTTPException(status_code=400, detail="No work items selected.")
    mode = (req.mode or "").strip().lower()
    mode = mode if mode in ("auto", "manual") else None

    # Batch title from the parent (best-effort) or a story count.
    title = ""
    if req.parent_work_item_id:
        try:
            title = (await get_ado_client().get_work_item(req.parent_work_item_id)).title
        except AdoApiError:
            title = f"Work item #{req.parent_work_item_id}"
    if not title:
        title = f"{len(ids)} stor{'y' if len(ids) == 1 else 'ies'}"

    batch = Batch(
        project_id=project_id, module=module, parent_work_item_id=req.parent_work_item_id,
        title=title, mode=(mode if module == "find-bugs" else None),
        created_at=datetime.utcnow(), updated_at=datetime.utcnow(),
    )
    session.add(batch)
    await session.commit()
    await session.refresh(batch)
    bid = batch.id

    orch = get_agent_orchestrator() if module == "author-tests" else get_bug_orchestrator()

    async def _start_one(wid: int) -> int:
        if module == "author-tests":
            return await orch.start(project_id, wid, auto_handoff=True, batch_id=bid)
        return await orch.start(project_id, wid, auto=(mode == "auto"), batch_id=bid)

    # Fan out concurrently: each orch.start does a synchronous ADO get_work_item before creating its
    # task, so a serial loop would block the POST on N round-trips (an 80-story Epic could exceed the
    # client/proxy timeout → retry → duplicate batch). gather() collapses that to ~one round-trip.
    results = await asyncio.gather(*(_start_one(wid) for wid in ids), return_exceptions=True)
    session_ids: list[int] = []
    skipped: list[int] = []
    for wid, res in zip(ids, results):
        if isinstance(res, BaseException):
            log.error("Batch child start failed for work item #%d", wid, exc_info=res)
            skipped.append(wid)
        else:
            session_ids.append(res)
    return BatchStartResponse(batch_id=bid, session_ids=session_ids, skipped=skipped)


@router.get("/detail/{batch_id}", response_model=BatchDetail)
async def batch_detail(batch_id: int, session: AsyncSession = Depends(get_session)) -> BatchDetail:
    batch = await session.get(Batch, batch_id)
    if batch is None:
        raise HTTPException(status_code=404, detail="Batch not found.")
    model, draft, _ = _MODULES[str(batch.module)]
    sessions = (await session.execute(
        select(model).where(model.batch_id == batch_id).order_by(model.created_at)
    )).scalars().all()
    sids = [x.id for x in sessions]
    counts: dict[int, int] = {}
    if sids:
        counts = dict((await session.execute(
            select(draft.session_id, func.count()).where(draft.session_id.in_(sids)).group_by(draft.session_id)
        )).all())
    children = [
        BatchChildSummary(
            session_id=x.id, work_item_id=x.work_item_id, work_item_title=x.work_item_title,
            status=str(x.status), current_activity=x.current_activity, error_message=x.error_message,
            count=counts.get(x.id, 0),
        )
        for x in sessions
    ]
    cls = _classify([str(x.status) for x in sessions])
    return BatchDetail(
        id=batch.id, module=str(batch.module), parent_work_item_id=batch.parent_work_item_id,
        title=batch.title, mode=(str(batch.mode) if batch.mode else None),
        created_at=batch.created_at, updated_at=batch.updated_at,
        derived_status=cls["derived_status"], children=children,
    )


@router.post("/{batch_id}/cancel", response_model=CancelResponse)
async def cancel_batch(batch_id: int, session: AsyncSession = Depends(get_session)) -> CancelResponse:
    batch = await session.get(Batch, batch_id)
    if batch is None:
        raise HTTPException(status_code=404, detail="Batch not found.")
    model, _, get_orch = _MODULES[str(batch.module)]
    rows = (await session.execute(
        select(model).where(model.batch_id == batch_id)
    )).scalars().all()
    orch = get_orch()
    signalled = 0
    forced = 0
    now = datetime.utcnow()
    for x in rows:
        # Live task (in-flight or semaphore-queued): signal it — its own handler flips it to Cancelled.
        if orch.cancel(x.id):
            signalled += 1
            continue
        # No live task but still non-terminal — chiefly Manual children parked at AwaitingActual/
        # AwaitingTester (their task already returned and was popped from the registry). Nothing to
        # signal, so transition them here. Without this, "Cancel all" on a parked Manual batch is a
        # no-op and the batch never leaves its non-terminal rollup.
        if str(x.status) not in _TERMINAL:
            x.status = "Cancelled"
            x.current_activity = "Cancelled (epic run)"
            x.updated_at = now
            forced += 1
    if forced:
        await session.commit()
    return CancelResponse(cancelled=(signalled + forced) > 0)


@router.get("/{project_id}", response_model=BatchList)
async def list_batches(
    project_id: int, skip: int = 0, take: int = 30,
    session: AsyncSession = Depends(get_session),
) -> BatchList:
    take = max(1, min(take, 100))
    skip = max(0, skip)
    total = (await session.execute(
        select(func.count()).select_from(Batch).where(Batch.project_id == project_id)
    )).scalar_one()
    rows = (await session.execute(
        select(Batch).where(Batch.project_id == project_id)
        .order_by(Batch.created_at.desc()).offset(skip).limit(take)
    )).scalars().all()

    # Gather child statuses for the listed batches in one query per module (no N+1).
    status_map: dict[int, list[str]] = {b.id: [] for b in rows}
    for module, (model, _draft, _orch) in _MODULES.items():
        mids = [b.id for b in rows if str(b.module) == module]
        if not mids:
            continue
        res = (await session.execute(
            select(model.batch_id, model.status).where(model.batch_id.in_(mids))
        )).all()
        for bid, st in res:
            status_map.setdefault(bid, []).append(str(st))

    items = [
        BatchSummary(
            id=b.id, module=str(b.module), parent_work_item_id=b.parent_work_item_id,
            title=b.title, mode=(str(b.mode) if b.mode else None), created_at=b.created_at,
            **_classify(status_map.get(b.id, [])),
        )
        for b in rows
    ]
    return BatchList(total=int(total), items=items)
