"""
Test-authoring pipeline. Ports the .NET AgentOrchestrator.

Lifecycle:
  - StartAsync seeds a TestSession synchronously and returns its id; a background asyncio Task
    runs Analyst → Tester (when auto-handoff is on) using a FRESH SessionLocal — never the
    request's session, which closes when the HTTP handler returns (the bug we hit in .NET).
  - RunTester re-runs the Tester half manually.
  - PushToAdo creates Test Cases, places them in the chosen Suite, links Tested-By to the story.
  - Cancel signals via the asyncio.Task registry; pipelines check periodically by awaiting work.
"""
from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime

from sqlalchemy import delete, select

from app.db import SessionLocal, session_scope
from app.models import (
    DraftStatus,
    SessionStatus,
    TestCaseDraft,
    TestSession,
)
from app.services.ado_client import TestCaseStep, get_ado_client
from app.services.agents.analyst import AnalystAgent, AnalystOutput
from app.services.agents.tester import TesterAgent
from app.services.knowledgebase import get_knowledgebase_service
from app.services.llm import get_llm_client

log = logging.getLogger("qa.orchestrator")


class AgentOrchestrator:
    """Single instance per process. Holds the cancellation registry."""

    def __init__(self) -> None:
        self._tasks: dict[int, asyncio.Task] = {}

    # ── cancellation ────────────────────────────────────────────────────────

    def cancel(self, session_id: int) -> bool:
        task = self._tasks.get(session_id)
        if task is None or task.done():
            return False
        task.cancel()
        return True

    # ── start (Analyst, then Tester if auto) ────────────────────────────────

    async def start(self, project_id: int, work_item_id: int, auto_handoff: bool = True) -> int:
        ado = get_ado_client()
        wi = await ado.get_work_item(work_item_id)

        async with SessionLocal() as s:
            session = TestSession(
                project_id=project_id,
                work_item_id=work_item_id,
                work_item_title=wi.title,
                status=SessionStatus.Analyzing,
                current_activity="Fetched work item from Azure DevOps",
                created_at=datetime.utcnow(),
                updated_at=datetime.utcnow(),
            )
            s.add(session)
            await s.commit()
            await s.refresh(session)
            sid = session.id

        task = asyncio.create_task(self._run_pipeline(sid, project_id, work_item_id, auto_handoff))
        self._tasks[sid] = task
        task.add_done_callback(lambda _t, k=sid: self._tasks.pop(k, None))
        return sid

    # ── manual Tester rerun ─────────────────────────────────────────────────

    async def run_tester(self, session_id: int) -> None:
        task = asyncio.create_task(self._rerun_tester(session_id))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    # ── push approved drafts to ADO ─────────────────────────────────────────

    async def push_to_ado(self, session_id: int, test_plan_id: int, test_suite_id: int) -> None:
        task = asyncio.create_task(self._push(session_id, test_plan_id, test_suite_id))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    # ── pipelines ───────────────────────────────────────────────────────────

    async def _run_pipeline(
        self, session_id: int, project_id: int, work_item_id: int, auto_handoff: bool,
    ) -> None:
        ado = get_ado_client()
        kb = get_knowledgebase_service()
        llm = await get_llm_client()
        log.info("Session %d using LLM provider: %s", session_id, llm.provider_name)

        async with session_scope() as s:
            session = await s.get(TestSession, session_id)
            if session is None:
                return

            try:
                await self._update_activity(s, session, "Re-fetching work item from Azure DevOps")
                wi = await ado.get_work_item(work_item_id)

                await self._update_activity(s, session, "Retrieving relevant chunks from the domain knowledgebase")
                query = f"{wi.title} {wi.acceptance_criteria or ''}".strip()
                context = await kb.search(s, project_id, query, top_k=8)

                await self._update_activity(
                    s, session, f"Analyst — analyzing story (using {len(context)} KB chunks)"
                )
                analysis = await AnalystAgent(llm).analyze(wi, context)

                session.analysis_json = analysis.to_json()
                if auto_handoff:
                    session.status = SessionStatus.Drafting
                    session.current_activity = "Analyst done — handing off to Tester"
                else:
                    session.status = SessionStatus.Drafted
                    session.current_activity = "Analyst done — awaiting manual Tester trigger"
                session.updated_at = datetime.utcnow()
                await s.commit()

                if auto_handoff:
                    await self._update_activity(s, session, "Tester — generating test cases in QA style")
                    await self._run_tester_inner(s, session, analysis, wi)
            except asyncio.CancelledError:
                log.info("Pipeline cancelled for session %d", session_id)
                session.status = SessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Pipeline failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _rerun_tester(self, session_id: int) -> None:
        ado = get_ado_client()
        llm = await get_llm_client()

        async with session_scope() as s:
            session = await s.get(TestSession, session_id)
            if session is None:
                return
            try:
                if not session.analysis_json:
                    raise RuntimeError("Analyst analysis missing — run Analyst first.")
                analysis = AnalystOutput.from_dict(json.loads(session.analysis_json))

                session.status = SessionStatus.Drafting
                session.current_activity = "Tester — generating test cases in QA style"
                session.updated_at = datetime.utcnow()
                await s.commit()

                wi = await ado.get_work_item(session.work_item_id)
                await self._run_tester_inner(s, session, analysis, wi)
            except asyncio.CancelledError:
                session.status = SessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Manual Tester failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _run_tester_inner(self, s, session: TestSession, analysis: AnalystOutput, wi) -> None:
        llm = await get_llm_client()
        generated = await TesterAgent(llm).generate(analysis, wi)

        # Clear any prior drafts (re-run case) then insert new ones.
        await s.execute(delete(TestCaseDraft).where(TestCaseDraft.session_id == session.id))
        for i, g in enumerate(generated):
            s.add(TestCaseDraft(
                session_id=session.id,
                title=g.title,
                preconditions=g.preconditions,
                steps_json=json.dumps([{"action": st.action, "expected": st.expected} for st in g.steps]),
                sort_order=i,
            ))

        session.status = SessionStatus.Drafted
        session.current_activity = f"Drafted {len(generated)} test case(s) — awaiting review"
        session.updated_at = datetime.utcnow()
        await s.commit()

    async def _push(self, session_id: int, test_plan_id: int, test_suite_id: int) -> None:
        ado = get_ado_client()

        async with session_scope() as s:
            stmt = (
                select(TestSession)
                .where(TestSession.id == session_id)
            )
            session = (await s.execute(stmt)).scalar_one_or_none()
            if session is None:
                log.error("Push: session %d not found", session_id)
                return

            session.status = SessionStatus.Pushing
            session.current_activity = "Push starting…"
            session.updated_at = datetime.utcnow()
            await s.commit()

            drafts = (await s.execute(
                select(TestCaseDraft)
                .where(TestCaseDraft.session_id == session_id)
                .where(TestCaseDraft.status == DraftStatus.Approved)
                .order_by(TestCaseDraft.sort_order)
            )).scalars().all()

            if not drafts:
                session.status = SessionStatus.Failed
                session.error_message = "No approved drafts to push."
                session.current_activity = "Push failed: no approved drafts"
                session.updated_at = datetime.utcnow()
                await s.commit()
                return

            try:
                pushed = 0
                for i, draft in enumerate(drafts, start=1):
                    session.current_activity = f"Pushing {i}/{len(drafts)}: creating Test Case"
                    session.updated_at = datetime.utcnow()
                    await s.commit()

                    raw_steps = json.loads(draft.steps_json) if draft.steps_json else []
                    steps = [
                        TestCaseStep(
                            action=str(x.get("action") or x.get("Action") or ""),
                            expected=str(x.get("expected") or x.get("Expected") or ""),
                        )
                        for x in raw_steps if isinstance(x, dict)
                    ]
                    tc_id = await ado.create_test_case(draft.title, draft.preconditions, steps)

                    session.current_activity = f"Pushing {i}/{len(drafts)}: adding TC #{tc_id} to suite"
                    await s.commit()
                    await ado.add_test_case_to_suite(test_plan_id, test_suite_id, tc_id)

                    session.current_activity = f"Pushing {i}/{len(drafts)}: linking TC #{tc_id} to story"
                    await s.commit()
                    await ado.link_tested_by(session.work_item_id, tc_id)

                    draft.ado_test_case_id = tc_id
                    draft.status = DraftStatus.Pushed
                    pushed += 1
                    await s.commit()

                session.status = SessionStatus.Done
                session.current_activity = f"Pushed {pushed} test case(s) to ADO"
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = SessionStatus.Cancelled
                session.current_activity = (
                    f"Push cancelled — {sum(1 for d in drafts if d.status == DraftStatus.Pushed)} "
                    f"of {len(drafts)} pushed"
                )
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Push failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Push failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    # ── helpers ─────────────────────────────────────────────────────────────

    @staticmethod
    async def _update_activity(s, session: TestSession, msg: str) -> None:
        session.current_activity = msg
        session.updated_at = datetime.utcnow()
        await s.commit()


async def _safe_commit(s) -> None:
    try:
        await s.commit()
    except Exception:  # noqa: BLE001
        pass


_singleton: AgentOrchestrator | None = None


def get_agent_orchestrator() -> AgentOrchestrator:
    global _singleton
    if _singleton is None:
        _singleton = AgentOrchestrator()
    return _singleton
