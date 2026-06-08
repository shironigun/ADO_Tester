"""
Author-tests router — mirrors the .NET AuthorTestsController.
  GET  /api/author-tests/search?q
  GET  /api/author-tests/plans
  GET  /api/author-tests/plans/{planId}/suites
  POST /api/author-tests/{projectId}/start
  POST /api/author-tests/sessions/{id}/run-tester
  POST /api/author-tests/sessions/{id}/cancel
  GET  /api/author-tests/sessions/{projectId}
  GET  /api/author-tests/sessions/detail/{id}
  PATCH /api/author-tests/drafts/{id}
  POST /api/author-tests/sessions/{id}/approve-all
  POST /api/author-tests/sessions/{id}/push
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.db import get_session
from app.services.sse import SSE_HEADERS, event_stream, parse_last_event_id
from app.models import DraftStatus, SessionStatus, TestCaseDraft, TestSession
from app.schemas import (
    AcceptedResponse,
    AdoWorkItemSchema,
    ApproveAllResponse,
    CancelResponse,
    PushRequest,
    StartRequest,
    StartResponse,
    TestCaseDraftSchema,
    TestPlanSchema,
    TestSessionDetailSchema,
    TestSessionList,
    TestSessionSummary,
    TestSuiteSchema,
    UpdateDraftRequest,
)
from app.services.ado_client import AdoApiError, get_ado_client
from app.services.orchestrators import get_agent_orchestrator

router = APIRouter(prefix="/api/author-tests", tags=["author-tests"])
log = logging.getLogger("qa.author-tests")


@router.get("/sessions/{session_id}/stream")
async def stream_session(session_id: int, request: Request) -> StreamingResponse:
    """Live SSE console for a session's pipeline (phase + token/thinking deltas)."""
    last = parse_last_event_id(
        request.headers.get("last-event-id"), request.query_params.get("lastEventId"))
    return StreamingResponse(
        event_stream(f"session:{session_id}", last),
        media_type="text/event-stream", headers=SSE_HEADERS,
    )


# ── Work item search + plans / suites ─────────────────────────────────────


@router.get("/search", response_model=list[AdoWorkItemSchema])
async def search(q: str = "") -> list[AdoWorkItemSchema]:
    if not q or not q.strip():
        return []
    try:
        items = await get_ado_client().search_work_items(q, top=20)
    except Exception as e:  # noqa: BLE001
        log.exception("WI search failed")
        raise HTTPException(status_code=502, detail=str(e)) from None
    return [AdoWorkItemSchema.model_validate(i) for i in items]


@router.get("/plans", response_model=list[TestPlanSchema])
async def get_plans() -> list[TestPlanSchema]:
    try:
        plans = await get_ado_client().get_test_plans()
    except Exception as e:  # noqa: BLE001
        log.exception("Get plans failed")
        raise HTTPException(status_code=502, detail=str(e)) from None
    return [TestPlanSchema.model_validate(p) for p in plans]


@router.get("/plans/{plan_id}/suites", response_model=list[TestSuiteSchema])
async def get_suites(plan_id: int) -> list[TestSuiteSchema]:
    try:
        suites = await get_ado_client().get_test_suites(plan_id)
    except Exception as e:  # noqa: BLE001
        log.exception("Get suites failed for plan %d", plan_id)
        raise HTTPException(status_code=502, detail=str(e)) from None
    return [TestSuiteSchema.model_validate(s) for s in suites]


# ── Pipeline ──────────────────────────────────────────────────────────────


@router.post("/{project_id}/start", response_model=StartResponse)
async def start(project_id: int, req: StartRequest) -> StartResponse:
    if req.work_item_id <= 0:
        raise HTTPException(status_code=400, detail="Invalid work item ID.")

    ado = get_ado_client()
    try:
        await ado.get_work_item(req.work_item_id)
    except AdoApiError as e:
        if e.status == 404:
            raise HTTPException(status_code=404, detail=f"Work item #{req.work_item_id} not found.") from None
        raise HTTPException(status_code=502, detail=str(e)) from None

    try:
        session_id = await get_agent_orchestrator().start(
            project_id, req.work_item_id, auto_handoff=req.auto_handoff
        )
        return StartResponse(session_id=session_id)
    except Exception as e:  # noqa: BLE001
        log.exception("Session start failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.post("/sessions/{session_id}/run-tester", response_model=AcceptedResponse, status_code=202)
async def run_tester(session_id: int) -> AcceptedResponse:
    await get_agent_orchestrator().run_tester(session_id)
    return AcceptedResponse(started=True)


@router.post("/sessions/{session_id}/cancel", response_model=CancelResponse)
async def cancel(session_id: int) -> CancelResponse:
    return CancelResponse(cancelled=get_agent_orchestrator().cancel(session_id))


# ── Session queries ───────────────────────────────────────────────────────


@router.get("/sessions/{project_id}", response_model=TestSessionList)
async def list_sessions(
    project_id: int,
    skip: int = 0,
    take: int = 20,
    session: AsyncSession = Depends(get_session),
) -> TestSessionList:
    take = max(1, min(take, 100))
    skip = max(0, skip)
    try:
        total = (await session.execute(
            select(func.count()).select_from(TestSession).where(TestSession.project_id == project_id)
        )).scalar_one()
        rows = (await session.execute(
            select(TestSession)
            .where(TestSession.project_id == project_id)
            .order_by(TestSession.created_at.desc())
            .offset(skip)
            .limit(take)
        )).scalars().all()
        return TestSessionList(
            total=int(total),
            items=[TestSessionSummary.model_validate(r) for r in rows],
        )
    except Exception as e:  # noqa: BLE001
        log.exception("ListSessions failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/sessions/detail/{session_id}", response_model=TestSessionDetailSchema)
async def get_session_detail(
    session_id: int,
    session: AsyncSession = Depends(get_session),
) -> TestSessionDetailSchema:
    try:
        row = await session.get(TestSession, session_id)
        if row is None:
            raise HTTPException(status_code=404, detail="Session not found.")
        return TestSessionDetailSchema.model_validate(row)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("GetSession failed for %d", session_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


# ── Draft editing ─────────────────────────────────────────────────────────


@router.patch("/drafts/{draft_id}", response_model=TestCaseDraftSchema)
async def update_draft(
    draft_id: int,
    req: UpdateDraftRequest,
    session: AsyncSession = Depends(get_session),
) -> TestCaseDraftSchema:
    try:
        d = await session.get(TestCaseDraft, draft_id)
        if d is None:
            raise HTTPException(status_code=404, detail="Draft not found.")
        if req.title is not None: d.title = req.title
        if req.preconditions is not None: d.preconditions = req.preconditions
        if req.steps_json is not None: d.steps_json = req.steps_json
        if req.status is not None: d.status = req.status
        await session.commit()
        await session.refresh(d)
        return TestCaseDraftSchema.model_validate(d)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("UpdateDraft failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.post("/sessions/{session_id}/approve-all", response_model=ApproveAllResponse)
async def approve_all(
    session_id: int,
    session: AsyncSession = Depends(get_session),
) -> ApproveAllResponse:
    try:
        drafts = (await session.execute(
            select(TestCaseDraft)
            .where(TestCaseDraft.session_id == session_id)
            .where(TestCaseDraft.status == DraftStatus.Draft)
        )).scalars().all()
        for d in drafts:
            d.status = DraftStatus.Approved
        await session.commit()
        return ApproveAllResponse(approved_count=len(drafts))
    except Exception as e:  # noqa: BLE001
        log.exception("ApproveAll failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


# ── Push to ADO ───────────────────────────────────────────────────────────


@router.post("/sessions/{session_id}/push", response_model=AcceptedResponse, status_code=202)
async def push(session_id: int, req: PushRequest) -> AcceptedResponse:
    # Fire-and-forget: orchestrator records progress to session.current_activity
    await get_agent_orchestrator().push_to_ado(session_id, req.test_plan_id, req.test_suite_id)
    return AcceptedResponse(started=True)
