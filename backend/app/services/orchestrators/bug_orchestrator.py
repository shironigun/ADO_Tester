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
import re
from datetime import datetime

_STOPWORDS = {"via", "with", "and", "the", "for", "a", "an", "of", "to", "in", "on", "by", "file", "guided"}


def _search_query(*sources: str) -> str:
    """Build a SHORT Code Search query — ADO ANDs all terms, so a long title returns nothing.
    Take the first ~3 significant words from the feature/title/module."""
    text = next((s for s in sources if s and s.strip()), "")
    words = [w for w in re.findall(r"[A-Za-z][A-Za-z]+", text) if w.lower() not in _STOPWORDS]
    return " ".join(words[:3])

from sqlalchemy import delete, select

from app.db import SessionLocal, session_scope
from app.models import (
    BugDraft,
    BugDraftStatus,
    BugSession,
    BugSessionStatus,
    BugSeverity,
    TestSession,
)
from app.services.ado_client import AdoApiError, get_ado_client
from app.services.agents.analyst import AnalystAgent, AnalystOutput
from app.services.agents.bug_writer import BugWriterAgent
from app.services.agents.code_verifier import CodeVerifierAgent, TestCaseInput
from app.services.knowledgebase import get_knowledgebase_service
from app.services.llm import get_llm_client
from app.services.event_bus import get_event_bus
from app.services.events import ConsoleEvent
from app.services.orchestrators.agent_orchestrator import _emit_closure

log = logging.getLogger("qa.bug-orchestrator")


def _pub(key: str, kind: str, phase: str | None = None, text: str = "",
         level: str = "info", data: dict | None = None) -> None:
    get_event_bus().publish(key, ConsoleEvent(kind=kind, phase=phase, text=text, level=level, data=data))


def _persist_log(session: "BugSession", key: str) -> None:
    try:
        session.event_log_json = json.dumps(get_event_bus().narrative(key))
    except Exception:  # noqa: BLE001
        pass


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
        verify_code: bool = False, commit_ids: list[str] | None = None,
        pr_ids: list[str] | None = None,
    ) -> None:
        task = asyncio.create_task(self._run_bug_writer(
            session_id, actual_behavior, code_context,
            verify_code, commit_ids or [], pr_ids or [],
        ))
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
        key = f"bug:{session_id}"
        loop = asyncio.get_running_loop()

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return

            try:
                _pub(key, "system", text=f"Pipeline started · provider {llm.provider_name}")
                await self._update(s, session, "Re-fetching ticket from Azure DevOps")
                wi = await ado.get_work_item(work_item_id)
                _pub(key, "phase", "fetch", f"Fetched ticket #{wi.id} — {wi.title}", level="success",
                     data={"workItemId": wi.id, "type": wi.work_item_type})

                await self._update(s, session, "Retrieving relevant KB chunks")
                query = f"{wi.title} {wi.acceptance_criteria or ''}".strip()
                context = await kb.search(s, project_id, query, top_k=8)
                _pub(key, "result", "retrieve", f"Retrieved {len(context)} knowledgebase chunk(s)",
                     level="success", data={"chunks": len(context), "files": [h.file_name for h in context]})

                await self._update(s, session, f"Analyst — analyzing story (using {len(context)} KB chunks)")
                _pub(key, "phase", "analyst", f"Analyst started ({len(context)} chunks)")
                analysis = await AnalystAgent(llm).analyze(wi, context, emit=_emit_closure(loop, key, "analyst"))
                _pub(key, "result", "analyst",
                     f"Analyst produced: {analysis.module} / {analysis.feature} · "
                     f"{len(analysis.acceptance_criteria)} ACs · {len(analysis.edge_cases)} edge",
                     level="success")

                session.analysis_json = analysis.to_json()
                session.status = BugSessionStatus.AwaitingActual
                session.current_activity = "Analyst done — paste observed actual behavior to continue"
                _pub(key, "system", "analyst", "Analyst done — awaiting observed actual behavior",
                     data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                _pub(key, "system", text="Cancelled by user", level="warn", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Analyst (bug pipeline) failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                _pub(key, "error", text=f"Failed: {e}", level="error", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    async def _run_bug_writer(
        self, session_id: int, actual_behavior: str, code_context: str | None,
        verify_code: bool = False, commit_ids: list[str] | None = None,
        pr_ids: list[str] | None = None,
    ) -> None:
        ado = get_ado_client()
        llm = await get_llm_client()
        key = f"bug:{session_id}"
        loop = asyncio.get_running_loop()

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return
            try:
                if not session.analysis_json:
                    raise RuntimeError("Analyst output missing — Analyst must complete first.")
                analysis = AnalystOutput.from_dict(json.loads(session.analysis_json))
                wi = await ado.get_work_item(session.work_item_id)

                session.actual_behavior = actual_behavior
                session.code_context = code_context

                # ── Code-analysis phase (optional) ──────────────────────────
                verify_summary = ""
                if verify_code:
                    session.status = BugSessionStatus.Verifying
                    session.current_activity = "Reading repository code…"
                    session.updated_at = datetime.utcnow()
                    await s.commit()

                    _pub(key, "phase", "discover", "Locating implementing code (linked PRs/commits + search)")
                    paths = await self._discover_code_paths(
                        ado, session.work_item_id, wi, analysis, commit_ids or [], pr_ids or [], key)
                    repo_code = await self._fetch_code_context(ado, paths, key)

                    test_cases = await self._load_test_cases(s, session.work_item_id)
                    _pub(key, "phase", "code-verify",
                         f"Verifying {len(test_cases)} test case(s) against {len(paths)} file(s)")
                    verdicts = await CodeVerifierAgent(llm).verify(
                        wi, analysis, test_cases, repo_code,
                        manual_description=(actual_behavior or None),
                        emit=_emit_closure(loop, key, "code-verify"),
                    )
                    session.code_verification_json = json.dumps([v.to_dict() for v in verdicts])
                    for v in verdicts:
                        lvl = "error" if v.status == "fail" else "success" if v.status == "pass" else "warn"
                        _pub(key, "result", "code-verify", f"{v.status.upper()}: {v.test_case} — {v.evidence}", level=lvl)

                    fails = [v for v in verdicts if v.status == "fail"]
                    if fails:
                        verify_summary = "Code-verification failures:\n" + "\n".join(
                            f"- {v.test_case}: {v.evidence} ({v.reasoning})" for v in fails)
                    # Feed retrieved code into BugWriter's code context too (bounded).
                    if repo_code:
                        code_context = ((code_context or "") + "\n\n" + repo_code)[:120_000]
                        session.code_context = code_context

                # Combined "actual behavior" signal for the Bug Writer.
                combined_actual = "\n\n".join(p for p in [
                    (actual_behavior or "").strip(),
                    verify_summary,
                ] if p).strip()
                if not combined_actual:
                    combined_actual = "No discrepancies detected by static code analysis."

                session.status = BugSessionStatus.Drafting
                session.current_activity = "Bug Writer — comparing expected vs actual"
                session.updated_at = datetime.utcnow()
                await s.commit()
                _pub(key, "phase", "bugwriter", "Bug Writer started — comparing expected vs actual")

                generated = await BugWriterAgent(llm).generate(
                    analysis, wi, combined_actual, code_context,
                    emit=_emit_closure(loop, key, "bugwriter"),
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
                    _pub(key, "result", "bugwriter",
                         f"Bug {i + 1}/{len(generated)} [{g.severity}]: {g.title}")

                session.status = BugSessionStatus.Drafted
                session.current_activity = f"Drafted {len(generated)} bug(s) — awaiting review"
                _pub(key, "system", "bugwriter", f"Drafted {len(generated)} bug(s) — awaiting review",
                     level="success", data={"terminal": True, "count": len(generated)})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = "Cancelled by user"
                _pub(key, "system", text="Cancelled by user", level="warn", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Bug Writer failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Failed: {e}"
                _pub(key, "error", text=f"Failed: {e}", level="error", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)

    # ── code-analysis helpers ─────────────────────────────────────────────────

    _MAX_FILES = 20
    _MAX_BYTES_PER_FILE = 30_000
    _MAX_TOTAL_BYTES = 250_000

    async def _discover_code_paths(
        self, ado, work_item_id: int, wi, analysis: AnalystOutput,
        commit_ids: list[str], pr_ids: list[str], key: str,
    ) -> list[str]:
        """Collect candidate file paths: story-linked PRs/commits + manual ids (exact) first,
        then ADO Code Search (supplemental). Deduped, capped."""
        try:
            repo_id = await ado.resolve_repository_id()
        except AdoApiError as e:
            _pub(key, "system", "discover", f"Repo resolve failed: {e}", level="warn")
            return []

        ordered: list[str] = []

        def add(paths: list[str]) -> None:
            for p in paths:
                if p not in ordered:
                    ordered.append(p)

        # 1. Story-linked PRs/commits (auto) + manual ids.
        auto_pr, auto_commit = [], []
        try:
            refs = await ado.get_work_item_git_refs(work_item_id)
            auto_pr, auto_commit = refs.get("pull_request_ids", []), refs.get("commit_shas", [])
        except AdoApiError:
            pass
        for pr in list(dict.fromkeys([*auto_pr, *pr_ids])):
            try:
                add(await ado.get_pull_request_changed_files(pr, repo_id))
                _pub(key, "system", "discover", f"PR #{pr}: {len(ordered)} file(s) so far")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"PR #{pr} skipped: {e}", level="warn")
        for sha in list(dict.fromkeys([*auto_commit, *commit_ids])):
            try:
                add(await ado.get_commit_changed_files(sha, repo_id))
                _pub(key, "system", "discover", f"commit {sha[:8]}: {len(ordered)} file(s) so far")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"commit {sha[:8]} skipped: {e}", level="warn")

        # 2. Code Search supplement (best-effort; extension may be unavailable).
        if len(ordered) < self._MAX_FILES:
            query = _search_query(analysis.feature, wi.title, analysis.module)
            try:
                hits = await ado.search_code(query, top=self._MAX_FILES) if query else []
                add(hits)
                _pub(key, "result", "discover", f"Code Search '{query}' → {len(hits)} hit(s)")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"Code Search unavailable: {e}", level="warn")

        capped = ordered[: self._MAX_FILES]
        _pub(key, "result", "discover", f"Selected {len(capped)} file(s) to read",
             level="success", data={"files": capped})
        return capped

    async def _fetch_code_context(self, ado, paths: list[str], key: str) -> str:
        """Fetch file contents (capped per-file + total) into a single prompt block."""
        blocks: list[str] = []
        total = 0
        for p in paths:
            if total >= self._MAX_TOTAL_BYTES:
                break
            try:
                content = await ado.get_file_content(p)
            except AdoApiError:
                continue
            if not content:
                continue
            snippet = content[: self._MAX_BYTES_PER_FILE]
            blocks.append(f"=== FILE: {p} ===\n{snippet}")
            total += len(snippet)
        _pub(key, "system", "discover", f"Read {len(blocks)} file(s) · {total:,} chars")
        return "\n\n".join(blocks)

    @staticmethod
    async def _load_test_cases(s, work_item_id: int) -> list[TestCaseInput]:
        """Pull the generated test cases for this story from the latest Author-Tests session."""
        ts = (await s.execute(
            select(TestSession)
            .where(TestSession.work_item_id == work_item_id)
            .order_by(TestSession.created_at.desc())
            .limit(1)
        )).scalar_one_or_none()
        if ts is None:
            return []
        out: list[TestCaseInput] = []
        for d in sorted(ts.drafts, key=lambda x: x.sort_order):
            try:
                steps = json.loads(d.steps_json) if d.steps_json else []
            except (ValueError, TypeError):
                steps = []
            out.append(TestCaseInput(
                title=d.title, preconditions=d.preconditions,
                steps=steps if isinstance(steps, list) else [],
            ))
        return out

    async def _push(self, session_id: int) -> None:
        ado = get_ado_client()
        key = f"bug:{session_id}"

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return

            session.status = BugSessionStatus.Pushing
            session.current_activity = "Push starting…"
            session.updated_at = datetime.utcnow()
            await s.commit()
            _pub(key, "phase", "push", "Push to ADO started")

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
                _pub(key, "error", "push", "Push failed: no approved bug drafts",
                     level="error", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
                return

            try:
                pushed = 0
                for i, draft in enumerate(drafts, start=1):
                    session.current_activity = f"Pushing {i}/{len(drafts)}: creating Bug"
                    session.updated_at = datetime.utcnow()
                    await s.commit()
                    _pub(key, "push", "push", f"{i}/{len(drafts)}: creating Bug — {draft.title}")

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
                    _pub(key, "result", "push", f"Bug #{bug_id} pushed → linked to story",
                         level="success", data={"bugId": bug_id})

                session.status = BugSessionStatus.Done
                session.current_activity = f"Pushed {pushed} bug(s) to ADO"
                _pub(key, "system", "push", f"Pushed {pushed} bug(s) to ADO",
                     level="success", data={"terminal": True, "count": pushed})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await s.commit()
            except asyncio.CancelledError:
                session.status = BugSessionStatus.Cancelled
                session.current_activity = (
                    f"Push cancelled — {sum(1 for d in drafts if d.status == BugDraftStatus.Pushed)} "
                    f"of {len(drafts)} pushed"
                )
                _pub(key, "system", "push", session.current_activity, level="warn", data={"terminal": True})
                _persist_log(session, key)
                session.updated_at = datetime.utcnow()
                await _safe_commit(s)
                raise
            except Exception as e:  # noqa: BLE001
                log.exception("Bug push failed for session %d", session_id)
                session.status = BugSessionStatus.Failed
                session.error_message = str(e)
                session.current_activity = f"Push failed: {e}"
                _pub(key, "error", "push", f"Push failed: {e}", level="error", data={"terminal": True})
                _persist_log(session, key)
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
