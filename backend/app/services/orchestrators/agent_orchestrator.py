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
from app.services.llm.base import StreamProgress
from app.services.event_bus import get_event_bus
from app.services.events import ConsoleEvent

log = logging.getLogger("qa.orchestrator")


def _emit_closure(loop, key: str, phase: str):
    """Build the thread-safe `emit` callback handed to the streaming LLM client. The CLI
    reader thread calls it with {'kind','text'}; it bridges to the event loop's bus."""
    bus = get_event_bus()

    def _emit(d: dict) -> None:
        bus.publish_threadsafe(loop, key, ConsoleEvent(
            kind=d.get("kind", "token"), text=d.get("text", ""), phase=phase,
        ))
    return _emit


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

        key = f"session:{session_id}"
        loop = asyncio.get_running_loop()

        async with session_scope() as s:
            session = await s.get(TestSession, session_id)
            if session is None:
                return

            try:
                # Resolve the LLM client inside the try so any config error
                # (e.g. provider not configured) is recorded on the session.
                llm = await get_llm_client()
                log.info("Session %d using LLM provider: %s", session_id, llm.provider_name)
                self._pub(key, "system", text=f"Pipeline started · provider {llm.provider_name}")

                await self._update_activity(s, session, "Re-fetching work item from Azure DevOps")
                wi = await ado.get_work_item(work_item_id)
                self._pub(key, "phase", "fetch", f"Fetched work item #{wi.id} — {wi.title}",
                          level="success", data={"workItemId": wi.id, "type": wi.work_item_type})

                await self._update_activity(s, session, "Retrieving relevant chunks from the domain knowledgebase")
                query = f"{wi.title} {wi.acceptance_criteria or ''}".strip()
                context = await kb.search(s, project_id, query, top_k=8)
                self._pub(key, "result", "retrieve", f"Retrieved {len(context)} knowledgebase chunk(s)",
                          level="success", data={"chunks": len(context),
                                                  "files": [h.file_name for h in context]})

                await self._update_activity(
                    s, session, f"Analyst — analyzing story (using {len(context)} KB chunks)"
                )
                self._pub(key, "phase", "analyst", f"Analyst started ({len(context)} chunks)")
                analysis = await self._run_streaming(
                    s, session, "Analyst — analyzing story",
                    lambda p: AnalystAgent(llm).analyze(wi, context, progress=p,
                                                        emit=_emit_closure(loop, key, "analyst")),
                    key=key, phase="analyst",
                )
                self._pub(key, "result", "analyst",
                          f"Analyst produced: {analysis.module} / {analysis.feature} · "
                          f"{len(analysis.acceptance_criteria)} ACs · {len(analysis.edge_cases)} edge · "
                          f"{len(analysis.negative_cases)} negative",
                          level="success", data={
                              "acceptanceCriteria": len(analysis.acceptance_criteria),
                              "edgeCases": len(analysis.edge_cases),
                              "negativeCases": len(analysis.negative_cases),
                              "businessRules": len(analysis.business_rules),
                          })

                session.analysis_json = analysis.to_json()
                if auto_handoff:
                    session.status = SessionStatus.Drafting
                    session.current_activity = "Analyst done — handing off to Tester"
                    session.updated_at = datetime.utcnow()
                    await s.commit()
                    self._pub(key, "system", text="Analyst done — handing off to Tester")
                    await self._update_activity(s, session, "Tester — generating test cases in QA style")
                    await self._run_tester_inner(s, session, analysis, wi, key=key, loop=loop)
                else:
                    session.status = SessionStatus.AwaitingTester
                    session.current_activity = "Analyst done — click Run Tester to continue"
                    self._pub(key, "system", "analyst",
                              "Analyst done — awaiting manual Run Tester", data={"terminal": True})
                    self._persist_log(session, key)
                    session.updated_at = datetime.utcnow()
                    await s.commit()
            except asyncio.CancelledError:
                log.info("Pipeline cancelled for session %d", session_id)
                session.status = SessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                self._pub(key, "system", text="Cancelled by user", level="warn", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Pipeline failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                self._pub(key, "error", text=f"Failed: {e}", level="error", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _rerun_tester(self, session_id: int) -> None:
        ado = get_ado_client()
        key = f"session:{session_id}"
        loop = asyncio.get_running_loop()

        async with session_scope() as s:
            session = await s.get(TestSession, session_id)
            if session is None:
                return
            try:
                # Resolve LLM inside the try so a missing provider lands on this session.
                await get_llm_client()

                if not session.analysis_json:
                    raise RuntimeError("Analyst analysis missing — run Analyst first.")
                analysis = AnalystOutput.from_dict(json.loads(session.analysis_json))

                session.status = SessionStatus.Drafting
                session.current_activity = "Tester — generating test cases in QA style"
                session.updated_at = datetime.utcnow()
                await s.commit()

                wi = await ado.get_work_item(session.work_item_id)
                await self._run_tester_inner(s, session, analysis, wi, key=key, loop=loop)
            except asyncio.CancelledError:
                session.status = SessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                self._pub(key, "system", text="Cancelled by user", level="warn", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Manual Tester failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                self._pub(key, "error", text=f"Failed: {e}", level="error", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _run_tester_inner(self, s, session: TestSession, analysis: AnalystOutput, wi,
                                key: str | None = None, loop=None) -> None:
        llm = await get_llm_client()
        if key:
            self._pub(key, "phase", "tester", "Tester started — generating test cases in QA style")
        emit = _emit_closure(loop, key, "tester") if (key and loop) else None
        generated = await self._run_streaming(
            s, session, "Tester — generating test cases",
            lambda p: TesterAgent(llm).generate(analysis, wi, progress=p, emit=emit),
            key=key, phase="tester",
        )

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
            if key:
                self._pub(key, "result", "tester", f"Test case {i + 1}/{len(generated)}: {g.title}")

        session.status = SessionStatus.Drafted
        session.current_activity = f"Drafted {len(generated)} test case(s) — awaiting review"
        if key:
            self._pub(key, "system", "tester", f"Drafted {len(generated)} test case(s) — awaiting review",
                      level="success", data={"terminal": True, "count": len(generated)})
            self._persist_log(session, key)
        session.updated_at = datetime.utcnow()
        await s.commit()

    async def _push(self, session_id: int, test_plan_id: int, test_suite_id: int) -> None:
        ado = get_ado_client()
        key = f"session:{session_id}"

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
            self._pub(key, "phase", "push", "Push to ADO started")

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
                self._pub(key, "error", "push", "Push failed: no approved drafts",
                          level="error", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
                return

            try:
                pushed = 0
                for i, draft in enumerate(drafts, start=1):
                    session.current_activity = f"Pushing {i}/{len(drafts)}: creating Test Case"
                    session.updated_at = datetime.utcnow()
                    await s.commit()
                    self._pub(key, "push", "push", f"{i}/{len(drafts)}: creating Test Case — {draft.title}")

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
                    self._pub(key, "result", "push", f"TC #{tc_id} pushed → suite + linked to story",
                              level="success", data={"testCaseId": tc_id})

                session.status = SessionStatus.Done
                session.current_activity = f"Pushed {pushed} test case(s) to ADO"
                self._pub(key, "system", "push", f"Pushed {pushed} test case(s) to ADO",
                          level="success", data={"terminal": True, "count": pushed})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = SessionStatus.Cancelled
                session.current_activity = (
                    f"Push cancelled — {sum(1 for d in drafts if d.status == DraftStatus.Pushed)} "
                    f"of {len(drafts)} pushed"
                )
                self._pub(key, "system", "push", session.current_activity,
                          level="warn", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Push failed for session %d", session_id)
                session.status = SessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Push failed: {e}"
                self._pub(key, "error", "push", f"Push failed: {e}",
                          level="error", data={"terminal": True})
                self._persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    # ── helpers ─────────────────────────────────────────────────────────────

    async def _run_streaming(self, s, session: TestSession, label: str, factory,
                             key: str | None = None, phase: str | None = None):
        """Run a streaming agent call while pumping live progress into the session's
        current_activity (~every 1.5s) so the UI shows what the agent is doing and that
        it's alive. If `key` is given, also publishes `stat` events to the console bus.
        `factory(progress)` returns the agent coroutine."""
        bus = get_event_bus() if key else None
        progress = StreamProgress()
        task = asyncio.create_task(factory(progress))
        try:
            while True:
                done, _ = await asyncio.wait({task}, timeout=1.5)
                snap = progress.snapshot()
                elapsed = int(snap["elapsed"])
                chars = snap["chars"]
                session.current_activity = (
                    f"{label} — {chars:,} chars · {elapsed}s" if chars
                    else f"{label} — working · {elapsed}s"
                )
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                if bus is not None:
                    bus.publish(key, ConsoleEvent(
                        kind="stat", phase=phase, data={"chars": chars, "elapsed": elapsed},
                    ))
                if task in done:
                    break
            return task.result()
        except asyncio.CancelledError:
            task.cancel()
            raise

    # ── console-event helpers ────────────────────────────────────────────────

    @staticmethod
    def _pub(key: str, kind: str, phase: str | None = None, text: str = "",
             level: str = "info", data: dict | None = None) -> None:
        get_event_bus().publish(key, ConsoleEvent(
            kind=kind, phase=phase, text=text, level=level, data=data,
        ))

    @staticmethod
    def _persist_log(session: TestSession, key: str) -> None:
        """Snapshot the durable narrative into EventLogJson (piggybacks the next commit)."""
        try:
            session.event_log_json = json.dumps(get_event_bus().narrative(key))
        except Exception:  # noqa: BLE001
            pass

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
