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


# import/require specifiers in TS/JS source (relative ones only — they resolve to repo files)
_IMPORT_RE = re.compile(r"""(?:from|import|require\()\s*['"]([^'"]+)['"]""")


def _extract_relative_imports(content: str) -> list[str]:
    return [m for m in _IMPORT_RE.findall(content or "") if m.startswith(".")]


def _resolve_import(spec: str, from_path: str) -> list[str]:
    """Resolve a relative import specifier to candidate repo paths (extension/index guesses)."""
    import posixpath
    base_dir = from_path.rsplit("/", 1)[0] if "/" in from_path else ""
    target = posixpath.normpath(posixpath.join(base_dir, spec))
    last = target.rsplit("/", 1)[-1]
    exts = [".ts", ".tsx", ".js", ".jsx", ".cs"]
    if "." in last:  # already has an extension
        return [target]
    return [target + e for e in exts] + [f"{target}/index{e}" for e in exts[:4]]

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
from app.services.ado_client import AdoApiError, ChangedFile, get_ado_client
from app.services.agents.analyst import AnalystAgent, AnalystOutput
from app.services.agents.bug_writer import BugWriterAgent
from app.services.agents.code_verifier import CodeVerifierAgent, TestCaseInput
from app.services.agents.json_utils import strip_fences
from app.services.app_settings import (
    get_app_settings_service,
    K_BUG_FROM_CASE_MODE,
    K_VERIFY_DETAIL,
    K_VERIFY_STEP_SCOPE,
)
from app.services.knowledgebase import get_knowledgebase_service
from app.services.llm import get_llm_client
from app.services.event_bus import get_event_bus
from app.services.events import ConsoleEvent
from app.services.orchestrators.agent_orchestrator import _emit_closure
from app.services.orchestrators.concurrency import PIPELINE_SEM, mark_cancelled, mark_queued

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

    async def start(
        self, project_id: int, work_item_id: int, auto: bool = False, batch_id: int | None = None,
    ) -> int:
        ado = get_ado_client()
        wi = await ado.get_work_item(work_item_id)

        async with SessionLocal() as s:
            session = BugSession(
                project_id=project_id,
                work_item_id=work_item_id,
                work_item_title=wi.title,
                status=BugSessionStatus.Analyzing,
                current_activity="Fetched ticket from Azure DevOps",
                batch_id=batch_id,
                created_at=datetime.utcnow(),
                updated_at=datetime.utcnow(),
            )
            s.add(session)
            await s.commit()
            await s.refresh(session)
            sid = session.id

        task = asyncio.create_task(self._run_analyst(sid, project_id, work_item_id, auto))
        self._tasks[sid] = task
        task.add_done_callback(lambda _t, k=sid: self._tasks.pop(k, None))
        return sid

    async def run_bug_writer(
        self, session_id: int, actual_behavior: str, code_context: str | None,
        verify_code: bool = False, commit_ids: list[str] | None = None,
        pr_ids: list[str] | None = None, selected_test_cases: list[str] | None = None,
    ) -> None:
        task = asyncio.create_task(self._run_bug_writer(
            session_id, actual_behavior, code_context,
            verify_code, commit_ids or [], pr_ids or [], selected_test_cases or [],
        ))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    async def push_to_ado(self, session_id: int) -> None:
        task = asyncio.create_task(self._push(session_id))
        self._tasks[session_id] = task
        task.add_done_callback(lambda _t, k=session_id: self._tasks.pop(k, None))

    # ── pipelines ───────────────────────────────────────────────────────────

    async def _run_analyst(
        self, session_id: int, project_id: int, work_item_id: int, auto: bool = False,
    ) -> None:
        """Queue behind the global concurrency cap, then run the Analyst (and, in auto mode, the
        Code-Verify + Bug-Writer chain). A child cancelled while still queued is marked Cancelled."""
        try:
            await mark_queued(BugSession, session_id)
            async with PIPELINE_SEM:
                await self._run_analyst_inner(session_id, project_id, work_item_id, auto)
        except asyncio.CancelledError:
            await mark_cancelled(BugSession, session_id, BugSessionStatus.Cancelled)
            raise

    async def _run_analyst_inner(
        self, session_id: int, project_id: int, work_item_id: int, auto: bool = False,
    ) -> None:
        ado = get_ado_client()
        kb = get_knowledgebase_service()
        llm = await get_llm_client()
        log.info("Bug session %d using LLM provider: %s", session_id, llm.provider_name)
        key = f"bug:{session_id}"
        loop = asyncio.get_running_loop()
        auto_continue = False

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
                if auto:
                    # Epic-run AUTO: keep going straight into Code-Verify + Bug-Writer (below, once
                    # this session is committed/closed) — don't park for manual observed behavior.
                    session.current_activity = "Analyst done — verifying against the code…"
                    session.updated_at = datetime.utcnow()
                    await s.commit()
                    auto_continue = True
                else:
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

        # AUTO mode (Epic run): the analyst session is committed+closed; run the rest of the pipeline
        # (anchor → code-verify → bug-writer) in this same task/slot. _run_bug_writer owns its own
        # session + cancel/fail handling, so a cancel here aborts whichever phase is live.
        if auto_continue:
            await self._run_bug_writer(session_id, "", None, verify_code=True)

    async def _run_bug_writer(
        self, session_id: int, actual_behavior: str, code_context: str | None,
        verify_code: bool = False, commit_ids: list[str] | None = None,
        pr_ids: list[str] | None = None, selected_test_cases: list[str] | None = None,
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

                # ── Code-analysis phase (always on) — anchored on the change ──
                verify_summary = ""
                session.status = BugSessionStatus.Verifying
                session.current_activity = "Anchoring on the change (linked PRs/commits)…"
                session.updated_at = datetime.utcnow()
                await s.commit()

                _pub(key, "phase", "discover",
                     "Anchoring on the change — linked PRs/commits, then only relevant code")
                diff_summary, repo_code = await self._discover_anchored_context(
                    ado, session.work_item_id, wi, analysis, commit_ids or [], pr_ids or [], llm, key)
                verify_context = (
                    f"## CHANGE UNDER TEST\n{diff_summary}\n\n## CODE (anchor + relevant dependencies/callers)\n{repo_code}"
                    if diff_summary else repo_code
                )

                sourced_tcs = await self._load_test_cases(ado, s, session.work_item_id)
                # User-chosen subset: keep only the selected cases (by stable key). An empty or
                # stale selection (nothing matched) falls back to verifying everything.
                if selected_test_cases:
                    sel = set(selected_test_cases)
                    chosen = [t for t in sourced_tcs if t[3] in sel]
                    if chosen:
                        if len(chosen) < len(sourced_tcs):
                            _pub(key, "system", "code-verify",
                                 f"Verifying {len(chosen)} of {len(sourced_tcs)} test case(s) you selected")
                        sourced_tcs = chosen
                test_cases = [t[0] for t in sourced_tcs]
                # title → (source, adoTestCaseId, steps) for tagging verdicts with origin + repro steps
                tc_meta = {
                    t[0].title.strip().lower(): (t[1], t[2], t[0].steps)
                    for t in sourced_tcs
                }
                n_plan = sum(1 for t in sourced_tcs if t[1] == "plan")
                n_draft = len(sourced_tcs) - n_plan
                _pub(key, "phase", "code-verify",
                     f"Verifying {len(test_cases)} test case(s) "
                     f"({n_plan} from Test Plan, {n_draft} draft) against the change")
                verdicts = await CodeVerifierAgent(llm).verify(
                    wi, analysis, test_cases, verify_context,
                    manual_description=(actual_behavior or None),
                    emit=_emit_closure(loop, key, "code-verify"),
                )
                # Tag each verdict with the source of its test case (plan vs draft) + repro steps
                # so the UI can show origin, link to the ADO Test Case, and let the tester try it.
                verdict_dicts = []
                for v in verdicts:
                    vd = v.to_dict()
                    meta = tc_meta.get(v.test_case.strip().lower())
                    if meta:
                        vd["source"] = meta[0]
                        if meta[1]:
                            vd["adoTestCaseId"] = meta[1]
                        if meta[2]:
                            vd["steps"] = meta[2]
                    verdict_dicts.append(vd)
                session.code_verification_json = json.dumps(verdict_dicts)
                for v in verdicts:
                    lvl = "error" if v.status == "fail" else "success" if v.status == "pass" else "warn"
                    _pub(key, "result", "code-verify", f"{v.status.upper()}: {v.test_case} — {v.evidence}", level=lvl)

                # ── Pass 2 (optional, settings-driven): per-STEP verdicts ──
                # `verify_detail` = case (skip) | step (tick/✗ per step) | step_full (+ per-step "actual").
                # `verify_step_scope` = fails_only (drill fails+inconclusive) | all (every case).
                detail = (await get_app_settings_service().get(K_VERIFY_DETAIL)).strip().lower() or "step"
                scope = (await get_app_settings_service().get(K_VERIFY_STEP_SCOPE)).strip().lower() or "fails_only"
                if detail in ("step", "step_full"):
                    tc_by_title = {t[0].title.strip().lower(): t[0] for t in sourced_tcs}
                    targets = [
                        (idx, case)
                        for idx, vd in enumerate(verdict_dicts)
                        if (scope == "all" or str(vd.get("status")) in ("fail", "inconclusive"))
                        and (case := tc_by_title.get(str(vd.get("testCase", "")).strip().lower())) is not None
                        and case.steps
                    ]
                    if targets:
                        full = detail == "step_full"
                        BATCH = 6
                        total = len(targets)
                        done = 0
                        _pub(key, "phase", "code-verify",
                             f"Step-level verification of {total} case(s) ({detail}, {scope})")
                        for start in range(0, total, BATCH):
                            chunk = targets[start:start + BATCH]
                            session.current_activity = f"Verifying steps — {done}/{total} case(s)"
                            session.updated_at = datetime.utcnow()
                            await s.commit()
                            step_lists = await CodeVerifierAgent(llm).verify_steps(
                                wi, analysis, [c for _, c in chunk], verify_context,
                                full=full, emit=_emit_closure(loop, key, "code-verify"),
                            )
                            for (vd_index, _case), svs in zip(chunk, step_lists):
                                verdict_dicts[vd_index]["steps"] = [sv.to_dict() for sv in svs]
                                verdict_dicts[vd_index]["failedStepIndex"] = next(
                                    (j for j, sv in enumerate(svs) if sv.status == "fail"), None)
                                # Reconcile the case-level status with the per-step verdict so the
                                # chip, the step ticks, and the "Create bug" affordance agree.
                                statuses = [sv.status for sv in svs]
                                if any(st == "fail" for st in statuses):
                                    verdict_dicts[vd_index]["status"] = "fail"
                                elif any(st == "inconclusive" for st in statuses):
                                    verdict_dicts[vd_index]["status"] = "inconclusive"
                                elif statuses:
                                    verdict_dicts[vd_index]["status"] = "pass"
                            done += len(chunk)
                            session.code_verification_json = json.dumps(verdict_dicts)
                            session.updated_at = datetime.utcnow()
                            await s.commit()
                        _pub(key, "result", "code-verify",
                             f"Step-level verification complete ({total} case(s))", level="success")

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

    async def _discover_anchored_context(
        self, ado, work_item_id: int, wi, analysis: AnalystOutput,
        commit_ids: list[str], pr_ids: list[str], llm, key: str,
    ) -> tuple[str, str]:
        """Anchor on the change (linked PRs/commits → diff), then expand ONLY along relevant
        dependencies (imports) and callers (symbol search), focused by one LLM triage pass.
        Returns (diff_summary, role-tagged code block). Never raises — degrades to whatever it
        could gather so verification always proceeds."""
        try:
            repo_id = await ado.resolve_repository_id()
        except AdoApiError as e:
            _pub(key, "system", "discover", f"Repo resolve failed: {e}", level="warn")
            return ("", "")

        changed: list[ChangedFile] = []
        have = lambda p: any(c.path == p for c in changed)  # noqa: E731

        def add_changed(items: list[ChangedFile]) -> None:
            for c in items:
                if not have(c.path):
                    changed.append(c)

        # 1. Anchor: changed files from story-linked + manual PRs/commits.
        auto_pr, auto_commit = [], []
        try:
            refs = await ado.get_work_item_git_refs(work_item_id)
            auto_pr, auto_commit = refs.get("pull_request_ids", []), refs.get("commit_shas", [])
        except AdoApiError:
            pass
        for pr in list(dict.fromkeys([*auto_pr, *pr_ids])):
            try:
                add_changed(await ado.get_pull_request_diff(pr, repo_id))
                _pub(key, "system", "discover", f"PR #{pr}: {len(changed)} changed file(s)")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"PR #{pr} skipped: {e}", level="warn")
        for sha in list(dict.fromkeys([*auto_commit, *commit_ids])):
            try:
                add_changed(await ado.get_commit_diff(sha, repo_id))
                _pub(key, "system", "discover", f"commit {sha[:8]}: {len(changed)} changed file(s)")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"commit {sha[:8]} skipped: {e}", level="warn")

        # 2. No linked refs → find the closest starter via search + recent commits.
        if not changed:
            terms = _search_query(analysis.feature, wi.title, analysis.module)
            _pub(key, "phase", "discover", f"No linked PRs/commits — searching for '{terms}'")
            seeds: list[str] = []
            try:
                seeds = await ado.search_code(terms, top=8) if terms else []
                _pub(key, "result", "discover", f"Code Search → {len(seeds)} seed file(s)")
            except AdoApiError as e:
                _pub(key, "system", "discover", f"Code Search unavailable: {e}", level="warn")
            for seed in seeds[:2]:
                try:
                    shas = await ado.get_commits_for_path(seed, top=1, repo_id=repo_id)
                    if shas:
                        add_changed(await ado.get_commit_diff(shas[0], repo_id))
                        _pub(key, "system", "discover", f"recent commit {shas[0][:8]} on {seed}")
                except AdoApiError:
                    pass
            for sp in seeds:
                if not have(sp):
                    changed.append(ChangedFile(path=sp, change_type="seed"))

        changed = changed[: self._MAX_FILES]
        if not changed:
            _pub(key, "system", "discover", "No code anchor found — verifying against expected behavior only", level="warn")
            return ("", "")

        diff_summary = "Files changed (anchor):\n" + "\n".join(f"- [{c.change_type or 'edit'}] {c.path}" for c in changed)

        # 3. Read changed files (skip deletes); parse their imports as dependency candidates.
        contents: dict[str, str] = {}
        import_cands: list[str] = []
        for c in changed:
            if c.change_type == "delete":
                continue
            try:
                txt = await ado.get_file_content(c.path)
            except AdoApiError:
                continue
            if not txt:
                continue
            contents[c.path] = txt[: self._MAX_BYTES_PER_FILE]
            for spec in _extract_relative_imports(txt):
                for cand in _resolve_import(spec, c.path):
                    if cand not in import_cands:
                        import_cands.append(cand)

        # 4. Callers (regression surface): symbol search on each changed file's base name.
        caller_cands: list[str] = []
        for path in list(contents.keys())[:5]:
            symbol = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
            try:
                for h in await ado.search_code(symbol, top=6):
                    if h not in contents and h not in caller_cands:
                        caller_cands.append(h)
            except AdoApiError:
                break  # Code Search unavailable — stop trying

        # 5. Triage: one LLM pass picks the few extra files actually needed.
        candidate_lines = (
            [f"[dependency] {p}" for p in import_cands[:30]]
            + [f"[caller] {p}" for p in caller_cands[:30]]
        )
        role_of: dict[str, str] = {p: "dependency" for p in import_cands}
        role_of.update({p: "caller" for p in caller_cands})
        selected_extra = (
            await self._triage_files(llm, diff_summary, analysis, candidate_lines, key)
            if candidate_lines else []
        )

        # 6. Assemble role-tagged block within byte budget.
        blocks = [f"=== [CHANGED] {p} ===\n{txt}" for p, txt in contents.items()]
        total = sum(len(t) for t in contents.values())
        fetched = set(contents.keys())
        for path in selected_extra:
            if total >= self._MAX_TOTAL_BYTES or path in fetched:
                continue
            try:
                txt = await ado.get_file_content(path)
            except AdoApiError:
                continue
            if not txt:
                continue
            snippet = txt[: self._MAX_BYTES_PER_FILE]
            blocks.append(f"=== [{role_of.get(path, 'related').upper()}] {path} ===\n{snippet}")
            total += len(snippet)
            fetched.add(path)

        _pub(key, "result", "discover",
             f"Anchored on {len(contents)} changed file(s); expanded to {len(fetched)} total · {total:,} chars",
             level="success", data={"files": sorted(fetched)})
        return (diff_summary, "\n\n".join(blocks))

    async def _triage_files(
        self, llm, diff_summary: str, analysis: AnalystOutput, candidate_lines: list[str], key: str,
    ) -> list[str]:
        """One cheap LLM pass: from the candidate dependencies/callers, pick only the files
        needed to judge regression. Falls back to deterministic top picks on any error."""
        system = (
            "You are a senior engineer scoping a REGRESSION review. Given the change under test "
            "and candidate files (dependencies the change imports, and callers that reference it), "
            "pick ONLY the files needed to judge whether the change breaks the expected behavior. "
            "Prefer callers and direct dependencies; ignore clearly unrelated files. "
            "Return ONLY a JSON array of file path strings (max 10), no prose."
        )
        user = (
            f"## Change under test\n{diff_summary}\n\n"
            f"## Feature\n{analysis.module} / {analysis.feature}\n\n"
            f"## Candidate files (role-tagged)\n" + "\n".join(candidate_lines)
            + "\n\nReturn the JSON array of paths to read."
        )
        try:
            raw = await llm.complete(system, user)
            data = json.loads(strip_fences(raw, opener="["))
            if isinstance(data, list):
                picked = [str(x) for x in data if isinstance(x, str)][:10]
                _pub(key, "result", "triage", f"Triage selected {len(picked)} extra file(s) to read")
                return picked
        except Exception as e:  # noqa: BLE001
            _pub(key, "system", "triage", f"Triage skipped ({e}) — using deterministic picks", level="warn")
        return [ln.split("] ", 1)[-1] for ln in candidate_lines][:8]

    async def _load_test_cases(
        self, ado, s, work_item_id: int,
    ) -> list[tuple[TestCaseInput, str, int | None, str]]:
        """Collect the test cases to verify for this story, tagged with their source:
          - "plan": real ADO Test Case work items linked to the story (source of truth)
          - "draft": local Author-Tests drafts not (yet) linked in ADO
        Returns (TestCaseInput, source, adoTestCaseId | None, key) tuples, where `key` is a STABLE,
        UNIQUE selection id (plan#<adoId> / draft#<localDraftId>) — distinct even when two drafts
        share a title. ADO-linked cases win; local drafts already represented in ADO are skipped."""
        sourced: list[tuple[TestCaseInput, str, int | None, str]] = []
        seen: set[str] = set()

        # 1. ADO-linked Test Cases (preferred).
        try:
            linked = await ado.get_linked_test_cases(work_item_id)
        except AdoApiError as e:
            log.info("Linked test cases unavailable for #%d: %s", work_item_id, e)
            linked = []
        for tc in linked:
            sourced.append((
                TestCaseInput(
                    title=tc.title, preconditions=tc.preconditions,
                    steps=[{"action": st.action, "expected": st.expected} for st in tc.steps],
                ),
                "plan", tc.id, f"plan#{tc.id}",
            ))
            seen.add(tc.title.strip().lower())

        # 2. Latest Author-Tests session drafts not already linked in ADO.
        ts = (await s.execute(
            select(TestSession)
            .where(TestSession.work_item_id == work_item_id)
            .order_by(TestSession.created_at.desc())
            .limit(1)
        )).scalar_one_or_none()
        if ts is not None:
            for d in sorted(ts.drafts, key=lambda x: x.sort_order):
                if (d.title or "").strip().lower() in seen:
                    continue
                try:
                    steps = json.loads(d.steps_json) if d.steps_json else []
                except (ValueError, TypeError):
                    steps = []
                sourced.append((
                    TestCaseInput(
                        title=d.title, preconditions=d.preconditions,
                        steps=steps if isinstance(steps, list) else [],
                    ),
                    "draft", getattr(d, "ado_test_case_id", None), f"draft#{d.id}",
                ))
        return sourced

    async def list_test_cases(self, session_id: int) -> list[dict]:
        """Public: the attached test cases for a session's story (ADO-linked + Author-Tests drafts),
        serialized for the API. No LLM — powers the pre-verification report list."""
        ado = get_ado_client()
        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                return []
            sourced = await self._load_test_cases(ado, s, session.work_item_id)
        out: list[dict] = []
        for tc, source, ado_id, ckey in sourced:
            out.append({
                "key": ckey,
                "title": tc.title,
                "preconditions": tc.preconditions,
                "source": source,
                "adoTestCaseId": ado_id,
                "steps": [
                    {"action": str(st.get("action") or st.get("Action") or ""),
                     "expected": str(st.get("expected") or st.get("Expected") or "")}
                    for st in tc.steps
                ],
            })
        return out

    async def create_bug_from_verdict(self, session_id: int, v: dict) -> int:
        """Create ONE bug draft from a single verified test case verdict; return the new draft id.
        Mode (`instant` | `agent`) comes from v['mode'] or the bug_from_case_mode setting:
          - instant: build the draft locally from the verdict (no LLM)
          - agent:   run the Bug Writer on just this case (falls back to instant on any error)."""
        mode = (v.get("mode") or "").strip().lower()
        if mode not in ("instant", "agent"):
            mode = (await get_app_settings_service().get(K_BUG_FROM_CASE_MODE)).strip().lower() or "instant"

        title = (str(v.get("test_case") or "")).strip() or "Untitled test case"
        steps = [st for st in (v.get("steps") or []) if isinstance(st, dict)]
        fidx = v.get("failed_step_index")
        if not isinstance(fidx, int) or fidx < 0 or fidx >= len(steps):
            # No explicit failing step → anchor on the first failing step, else the first
            # inconclusive one, else leave None (the repro then covers all steps).
            fidx = next((i for i, st in enumerate(steps) if str(st.get("status")).lower() == "fail"), None)
            if fidx is None:
                fidx = next((i for i, st in enumerate(steps) if str(st.get("status")).lower() == "inconclusive"), None)
        evidence = str(v.get("evidence") or "")
        reasoning = str(v.get("reasoning") or "")

        async with session_scope() as s:
            session = await s.get(BugSession, session_id)
            if session is None:
                raise ValueError("Session not found.")
            if session.status in (
                BugSessionStatus.Analyzing, BugSessionStatus.Verifying,
                BugSessionStatus.Drafting, BugSessionStatus.Pushing,
            ):
                # The Bug Writer run resets ALL drafts; adding one mid-run would be wiped.
                raise ValueError("The pipeline is still running — wait for it to finish before adding a bug.")

            # Local pre-fill from the verdict — used directly for `instant`, and as the `agent` fallback.
            cut = (fidx + 1) if isinstance(fidx, int) else len(steps)
            repro = [
                (str(st.get("action") or st.get("expected") or "")).strip()
                for st in steps[:cut] if (st.get("action") or st.get("expected"))
            ]
            fail_step = steps[fidx] if isinstance(fidx, int) else (steps[-1] if steps else {})
            expected = str(fail_step.get("expected") or "")
            actual = str(fail_step.get("actual") or evidence or reasoning or "")
            fields = dict(
                title=title,
                severity=_parse_severity(str(v.get("severity") or "Medium")),
                preconditions="",
                repro_steps_json=json.dumps(repro),
                expected_behavior=expected,
                actual_behavior=actual,
                suggested_fix="",
            )

            if mode == "agent":
                try:
                    llm = await get_llm_client()
                    wi = await get_ado_client().get_work_item(session.work_item_id)
                    analysis = (AnalystOutput.from_dict(json.loads(session.analysis_json))
                                if session.analysis_json else AnalystOutput())
                    summary = _verdict_actual_summary(title, repro, expected, actual)
                    generated = await BugWriterAgent(llm).generate(analysis, wi, summary, session.code_context)
                    if generated:
                        g = generated[0]
                        fields = dict(
                            title=g.title or title,
                            severity=_parse_severity(g.severity),
                            preconditions=g.preconditions,
                            repro_steps_json=json.dumps(g.repro_steps),
                            expected_behavior=g.expected or expected,
                            actual_behavior=g.actual or actual,
                            suggested_fix=g.suggested_fix,
                        )
                except Exception:  # noqa: BLE001 — degrade to the instant pre-fill
                    log.exception("Bug-from-verdict agent mode failed for %d; using instant pre-fill", session_id)

            existing = (await s.execute(
                select(BugDraft.sort_order).where(BugDraft.session_id == session_id)
            )).scalars().all()
            next_order = (max(existing) + 1) if existing else 0

            draft = BugDraft(session_id=session_id, sort_order=next_order,
                             status=BugDraftStatus.Draft, **fields)
            s.add(draft)
            # Make the new draft reviewable/pushable when the session is past the in-flight stages.
            if session.status in (
                BugSessionStatus.Drafted, BugSessionStatus.Done, BugSessionStatus.AwaitingActual,
                BugSessionStatus.Failed, BugSessionStatus.Cancelled,
            ):
                session.status = BugSessionStatus.Drafted
                session.current_activity = "Added a bug from a failed test case — awaiting review"
            session.updated_at = datetime.utcnow()
            await s.commit()
            await s.refresh(draft)
            return draft.id

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


def _verdict_actual_summary(title: str, repro: list[str], expected: str, actual: str) -> str:
    """Compose the 'observed actual behavior' signal handed to the Bug Writer for a single case."""
    parts = [f"A verified test case failed: {title}."]
    if repro:
        parts.append("Steps:\n" + "\n".join(f"- {r}" for r in repro))
    if expected:
        parts.append(f"Expected: {expected}")
    if actual:
        parts.append(f"Observed (from static code verification): {actual}")
    return "\n\n".join(parts)


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
