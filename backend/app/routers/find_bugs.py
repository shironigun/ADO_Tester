"""
Find-Bugs router — mirrors the .NET FindBugsController.
  POST  /api/find-bugs/{projectId}/start
  POST  /api/find-bugs/sessions/{id}/run
  POST  /api/find-bugs/sessions/{id}/cancel
  POST  /api/find-bugs/sessions/{id}/push
  POST  /api/find-bugs/sessions/{id}/approve-all
  GET   /api/find-bugs/sessions/{projectId}
  GET   /api/find-bugs/sessions/detail/{id}
  PATCH /api/find-bugs/drafts/{id}
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.db import get_session
from app.services.sse import SSE_HEADERS, event_stream, parse_last_event_id
from app.models import BugDraft, BugDraftStatus, BugSession
from app.schemas import (
    AcceptedResponse,
    ApproveAllResponse,
    BugDraftSchema,
    BugLinkedRefs,
    BugRunRequest,
    BugSessionDetailSchema,
    BugSessionList,
    BugSessionSummary,
    BugStartRequest,
    BugStartResponse,
    CancelResponse,
    UpdateBugDraftRequest,
)
from app.services.ado_client import AdoApiError, get_ado_client
from app.services.orchestrators import get_bug_orchestrator

router = APIRouter(prefix="/api/find-bugs", tags=["find-bugs"])
log = logging.getLogger("qa.find-bugs")


@router.get("/sessions/{session_id}/stream")
async def stream_session(session_id: int, request: Request) -> StreamingResponse:
    """Live SSE console for a bug session's pipeline (phase + token/thinking deltas)."""
    last = parse_last_event_id(
        request.headers.get("last-event-id"), request.query_params.get("lastEventId"))
    return StreamingResponse(
        event_stream(f"bug:{session_id}", last),
        media_type="text/event-stream", headers=SSE_HEADERS,
    )


@router.post("/{project_id}/start", response_model=BugStartResponse)
async def start(project_id: int, req: BugStartRequest) -> BugStartResponse:
    if req.work_item_id <= 0:
        raise HTTPException(status_code=400, detail="Invalid work item ID.")
    try:
        await get_ado_client().get_work_item(req.work_item_id)
    except AdoApiError as e:
        if e.status == 404:
            raise HTTPException(status_code=404, detail=f"Work item #{req.work_item_id} not found.") from None
        raise HTTPException(status_code=502, detail=str(e)) from None

    try:
        sid = await get_bug_orchestrator().start(project_id, req.work_item_id)
        return BugStartResponse(session_id=sid)
    except Exception as e:  # noqa: BLE001
        log.exception("Bug session start failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.post("/sessions/{session_id}/run", response_model=AcceptedResponse, status_code=202)
async def run_bug_writer(session_id: int, req: BugRunRequest) -> AcceptedResponse:
    # Need at least one signal: a manual description OR the verify-against-code path.
    has_manual = bool(req.actual_behavior and req.actual_behavior.strip())
    if not has_manual and not req.verify_code:
        raise HTTPException(
            status_code=400,
            detail="Provide observed actual behavior, or enable 'Verify against code'.",
        )
    await get_bug_orchestrator().run_bug_writer(
        session_id, req.actual_behavior or "", req.code_context,
        verify_code=req.verify_code, commit_ids=req.commit_ids, pr_ids=req.pr_ids,
    )
    return AcceptedResponse(started=True)


@router.get("/sessions/{session_id}/linked-refs", response_model=BugLinkedRefs)
async def linked_refs(session_id: int, session: AsyncSession = Depends(get_session)) -> BugLinkedRefs:
    """Commits/PRs linked to the session's story — to prefill the code-verify UI."""
    bug = await session.get(BugSession, session_id)
    if bug is None:
        raise HTTPException(status_code=404, detail="Session not found.")
    try:
        refs = await get_ado_client().get_work_item_git_refs(bug.work_item_id)
        return BugLinkedRefs(
            pull_request_ids=refs.get("pull_request_ids", []),
            commit_shas=refs.get("commit_shas", []),
        )
    except AdoApiError:
        return BugLinkedRefs()


@router.post("/sessions/{session_id}/cancel", response_model=CancelResponse)
async def cancel(session_id: int) -> CancelResponse:
    return CancelResponse(cancelled=get_bug_orchestrator().cancel(session_id))


@router.post("/sessions/{session_id}/push", response_model=AcceptedResponse, status_code=202)
async def push(session_id: int) -> AcceptedResponse:
    await get_bug_orchestrator().push_to_ado(session_id)
    return AcceptedResponse(started=True)


@router.post("/sessions/{session_id}/approve-all", response_model=ApproveAllResponse)
async def approve_all(
    session_id: int, session: AsyncSession = Depends(get_session),
) -> ApproveAllResponse:
    try:
        drafts = (await session.execute(
            select(BugDraft)
            .where(BugDraft.session_id == session_id)
            .where(BugDraft.status == BugDraftStatus.Draft)
        )).scalars().all()
        for d in drafts:
            d.status = BugDraftStatus.Approved
        await session.commit()
        return ApproveAllResponse(approved_count=len(drafts))
    except Exception as e:  # noqa: BLE001
        log.exception("BugApproveAll failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/sessions/{project_id}", response_model=BugSessionList)
async def list_sessions(
    project_id: int,
    skip: int = 0,
    take: int = 30,
    session: AsyncSession = Depends(get_session),
) -> BugSessionList:
    take = max(1, min(take, 100))
    skip = max(0, skip)
    try:
        total = (await session.execute(
            select(func.count()).select_from(BugSession).where(BugSession.project_id == project_id)
        )).scalar_one()
        rows = (await session.execute(
            select(BugSession)
            .where(BugSession.project_id == project_id)
            .order_by(BugSession.created_at.desc())
            .offset(skip).limit(take)
        )).scalars().all()
        return BugSessionList(
            total=int(total),
            items=[BugSessionSummary.model_validate(r) for r in rows],
        )
    except Exception as e:  # noqa: BLE001
        log.exception("ListBugSessions failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/sessions/detail/{session_id}", response_model=BugSessionDetailSchema)
async def get_session_detail(
    session_id: int, session: AsyncSession = Depends(get_session),
) -> BugSessionDetailSchema:
    try:
        row = await session.get(BugSession, session_id)
        if row is None:
            raise HTTPException(status_code=404, detail="Session not found.")
        return BugSessionDetailSchema.model_validate(row)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("GetBugSession failed for %d", session_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.patch("/drafts/{draft_id}", response_model=BugDraftSchema)
async def update_draft(
    draft_id: int, req: UpdateBugDraftRequest,
    session: AsyncSession = Depends(get_session),
) -> BugDraftSchema:
    try:
        d = await session.get(BugDraft, draft_id)
        if d is None:
            raise HTTPException(status_code=404, detail="Draft not found.")
        if req.title is not None: d.title = req.title
        if req.severity is not None: d.severity = req.severity
        if req.preconditions is not None: d.preconditions = req.preconditions
        if req.repro_steps_json is not None: d.repro_steps_json = req.repro_steps_json
        if req.expected_behavior is not None: d.expected_behavior = req.expected_behavior
        if req.actual_behavior is not None: d.actual_behavior = req.actual_behavior
        if req.suggested_fix is not None: d.suggested_fix = req.suggested_fix
        if req.status is not None: d.status = req.status
        await session.commit()
        await session.refresh(d)
        return BugDraftSchema.model_validate(d)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("UpdateBugDraft failed")
        raise HTTPException(status_code=500, detail=str(e)) from None
