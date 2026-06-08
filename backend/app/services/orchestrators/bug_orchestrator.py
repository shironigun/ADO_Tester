"""
Find-Bugs pipeline. Ports the .NET BugOrchestrator.

Lifecycle:
  start          → runs Analyst, parks at AwaitingActual until user provides observed actual
  run_bug_writer → Bug Writer runs against expected (Analyst) vs actual (user-supplied)
  push_to_ado    → creates Bug work items + Related-links them to the story
  cancel         → signals via asyncio.Task registry
"""
from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime

from sqlalchemy import delete, select

from app.db import SessionLocal, session_scope
from app.models import (
    BugDraft,
    BugDraftStatus,
    BugSession,
    BugSessionStatus,
    BugSeverity,
)
from app.services.ado_client import get_ado_client
from app.services.agents.analyst import AnalystAgent, AnalystOutput
from app.services.agents.bug_writer import BugWriterAgent
from app.services.knowledgebase import get_knowledgebase_service
from app.services.llm import get_llm_client

log = logging.getLogger("qa.bug-orchestrator")


class BugOrchestrator:
    def __init__(self) -> None:
        self._tasks: dict[int, asyncio.Task] = {}

    def cancel(self, session_id: int) -> bool:
        task = self._tasks.get(session_id)
        if task is None or task.done():
            return False
        task.cancel()
        return True

    async def start(self, project_id: int, work_item_id: int) -> int:
        ado = get_ado_client()
        wi = await ado.get_work_item(work_item_id)

        async with SessionLocal() as s:
            session = BugSession(
                project_id=project_id,
                work_item_id=work_item_id,
                work_item_title=wi.title,
                status=BugSessionStatus.Analyzing,
                current_activity="Fetched ticket from Azure DevOps",
                created_at=datetime.utcnow(),
                updated_at=datetime.utcnow(),
            )
            s.add(session)
            await s.commit()
            await s.refresh(session)
            sid = session.id

        task = asyncio.create_task(self._run_analyst(sid, project_id, work_item_id))
        self._tasks[sid] = task
        task.add_done_callback(lambda _t, k=sid: self._tasks.pop(k, None))
        return sid

    async def run_bug_writer(
        self, session_id: int, actual_behavior: str, code_context: str | None,
    ) -> None:
        task = asyncio.create_task(self._run_bug_writer(session_id, actual_behavior, code_context))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    async def push_to_ado(self, session_id: int) -> None:
        task = asyncio.create_task(self._push(session_id))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    # ── pipelines ───────────────────────────────────────────────────────────

    async def _run_analyst(self, session_id: int, project_id: int, work_item_id: int) -> None:
        ado = get_ado_client()
        kb = get_knowledgebase_service()
        llm = await get_llm_client()
        log.info("Bug session %d using LLM provider: %s", session_id, llm.provider_name)

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return

            try:
                await self._update(s, session, "Re-fetching ticket from Azure DevOps")
                wi = await ado.get_work_item(work_item_id)

                await self._update(s, session, "Retrieving relevant KB chunks")
                query = f"{wi.title} {wi.acceptance_criteria or ''}".strip()
                context = await kb.search(s, project_id, query, top_k=8)

                await self._update(s, session, f"Analyst — analyzing story (using {len(context)} KB chunks)")
                analysis = await AnalystAgent(llm).analyze(wi, context)

                session.analysis_json = analysis.to_json()
                session.status = BugSessionStatus.AwaitingActual
                session.current_activity = "Analyst done — paste observed actual behavior to continue"
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Analyst (bug pipeline) failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _run_bug_writer(
        self, session_id: int, actual_behavior: str, code_context: str | None,
    ) -> None:
        ado = get_ado_client()
        llm = await get_llm_client()

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return
            try:
                if not session.analysis_json:
                    raise RuntimeError("Analyst output missing — Analyst must complete first.")
                analysis = AnalystOutput.from_dict(json.loads(session.analysis_json))

                session.actual_behavior = actual_behavior
                session.code_context = code_context
                session.status = BugSessionStatus.Drafting
                session.current_activity = "Bug Writer — comparing expected vs actual"
                session.updated_at = datetime.utcnow()
                await s.commit()

                wi = await ado.get_work_item(session.work_item_id)
                generated = await BugWriterAgent(llm).generate(
                    analysis, wi, actual_behavior, code_context
                )

                # Reset prior drafts on re-run.
                await s.execute(delete(BugDraft).where(BugDraft.session_id == session_id))
                for i, g in enumerate(generated):
                    s.add(BugDraft(
                        session_id=session_id,
                        title=g.title,
                        severity=_parse_severity(g.severity),
                        preconditions=g.preconditions,
                        repro_steps_json=json.dumps(g.repro_steps),
                        expected_behavior=g.expected,
                        actual_behavior=g.actual,
                        suggested_fix=g.suggested_fix,
                        sort_order=i,
                    ))

                session.status = BugSessionStatus.Drafted
                session.current_activity = f"Drafted {len(generated)} bug(s) — awaiting review"
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Bug Writer failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _push(self, session_id: int) -> None:
        ado = get_ado_client()

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return

            session.status = BugSessionStatus.Pushing
            session.current_activity = "Push starting…"
            session.updated_at = datetime.utcnow()
            await s.commit()

            drafts = (await s.execute(
                select(BugDraft)
                .where(BugDraft.session_id == session_id)
                .where(BugDraft.status == BugDraftStatus.Approved)
                .order_by(BugDraft.sort_order)
            )).scalars().all()

            if not drafts:
                session.status = BugSessionStatus.Failed
                session.error_message = "No approved bug drafts to push."
                session.current_activity = "Push failed: no approved bug drafts"
                session.updated_at = datetime.utcnow()
                await s.commit()
                return

            try:
                pushed = 0
                for i, draft in enumerate(drafts, start=1):
                    session.current_activity = f"Pushing {i}/{len(drafts)}: creating Bug"
                    session.updated_at = datetime.utcnow()
                    await s.commit()

                    repro_steps = json.loads(draft.repro_steps_json) if draft.repro_steps_json else []
                    bug_id = await ado.create_bug(
                        title=draft.title,
                        severity=draft.severity.value if hasattr(draft.severity, "value") else str(draft.severity),
                        preconditions=draft.preconditions,
                        repro_steps=[str(x) for x in repro_steps],
                        expected=draft.expected_behavior,
                        actual=draft.actual_behavior,
                        suggested_fix=draft.suggested_fix,
                    )

                    session.current_activity = f"Pushing {i}/{len(drafts)}: linking Bug #{bug_id} to story"
                    await s.commit()
                    await ado.link_related(bug_id, session.work_item_id)

                    draft.ado_bug_id = bug_id
                    draft.status = BugDraftStatus.Pushed
                    pushed += 1
                    await s.commit()

                session.status = BugSessionStatus.Done
                session.current_activity = f"Pushed {pushed} bug(s) to ADO"
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = (
                    f"Push cancelled — {sum(1 for d in drafts if d.status == BugDraftStatus.Pushed)} "
                    f"of {len(drafts)} pushed"
                )
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Bug push failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Push failed: {e}"
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    @staticmethod
    async def _update(s, session: BugSession, activity: str) -> None:
        session.current_activity = activity
        session.updated_at = datetime.utcnow()
        await s.commit()


def _parse_severity(s: str) -> BugSeverity:
    low = (s or "").strip().lower()
    if "crit" in low or low == "1":
        return BugSeverity.Critical
    if "high" in low or low == "2":
        return BugSeverity.High
    if "low" in low or low == "4":
        return BugSeverity.Low
    return BugSeverity.Medium


async def _safe_commit(s) -> None:
    try:
        await s.commit()
    except Exception:  # noqa: BLE001
        pass


_singleton: BugOrchestrator | None = None


def get_bug_orchestrator() -> BugOrchestrator:
    global _singleton
    if _singleton is None:
        _singleton = BugOrchestrator()
    return _singleton
