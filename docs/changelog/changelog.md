# Changelog

Newest entries first.

---

## 2026-08-01

### feat(qa-loop): FRS Knowledge Base–grounded redesign + UI Phase v2
QA Loop now retrieves an uploaded FRS KB (folder-preserving upload), runs a shared atlas-seeded
code scan, and grounds the API analyst in module/RULES context. After API push (`ApiDone`), an
optional UI Phase runs a dedicated UI Analyst → case author → Playwright scripts → inline
execution → explain/heal path (agents separate from Author Tests).
- New session columns: `FrsSlug`, `KbContextJson`, `CodeScanJson`, `UiAnalysisJson`, `UiTestSuiteId`,
  `LastUiRunId`; statuses `RetrievingKb` / `Scanning` / `ApiDone` and UI `AnalyzingUi` /
  `AwaitingUiAnalysisReview` / `RunningUiScripts`.
- Endpoints: `frs-modules`, `ui-suite`, `rerun-ui-analysis`, `run-ui-scripts`, `explain-failure`;
  heal accepts `userContext`; author/generate guards tightened.
- Frontend: KB folder tree upload; UiPhasePanel state machine (analysis review, Run Scripts,
  ExplainFailureCard).
- Verified with full backend test suite and a clean frontend production build. Live ADO verification
  pending (expired PAT). See
  [`docs/features/qa-loop-frs-grounded.md`](../features/qa-loop-frs-grounded.md).

---

## 2026-07-30

### feat(qa-loop): discover nested module sources from the selected branch
QA Loop no longer relies solely on a configured `{FrontendPath}/apis` folder or a top-level module
directory. When those paths produce no source, it lists the selected repository branch, selects bounded
frontend candidates, extracts common HTTP calls, ranks backend controller candidates, and feeds the
evidence into the existing analyzer.
- Repository/branch selections now drive module dropdown discovery and code search resolution.
- Sessions persist discovery evidence and expose an optional Source Dirs override in the scan form.
- The UI Phase selection now persists at scan start; generated Playwright prompts include the selected
  module route and validate that Playwright MCP is initialized.
- Verified with 187 backend tests, a clean API build, and a successful frontend production build. See
  [`docs/features/qa-loop-smart-discovery.md`](../features/qa-loop-smart-discovery.md).

### fix(qa-loop): inherit repository and branch from Azure DevOps Settings
QA Loop scans that omitted the optional repository/branch form fields previously persisted both values
as null, even when Azure DevOps Settings had a configured repository and default branch. The pipeline
then skipped all code-aware source fetches and generated text-only, empty coverage analysis.
- The scan-start form now uses `ado.repository` / `ado.default_branch` when its more-specific
  `qaloop.default_repository` / `qaloop.default_branch` values are unset.
- The backend now applies the same fallback while creating `ApiTestSession`, so direct API callers and
  stale frontend state cannot create a source-less session.
- Verified with 187 backend tests, clean isolated API build, and a successful frontend production build.

### feat(llm): add a separate native Ollama provider
Ollama is now an independent provider rather than an OpenAI-compatible configuration:
- **Native API:** `OllamaLlmClient` calls native `POST /api/chat` for local Ollama and Ollama Cloud,
  sending a bearer token only when one is configured.
- **Settings:** `OLLAMA_BASE_URL`, `OLLAMA_API_KEY`, and `OLLAMA_MODEL` map to the independent
  `ollama.*` runtime settings and a new Settings-page card. Existing `OPENAI_*` and
  `OpenAICompatible` behavior is unchanged.
- **Verification:** 187 backend tests pass; the configured Ollama Cloud endpoint returned `OK` from
  a native API chat request. See [`docs/features/ollama-provider.md`](../features/ollama-provider.md).

### fix(qa-loop): surface silent source-fetch degradation in the Agent Console
A QA Loop scan against a live module (`orders`) completed with **0 routes / 0 surfaces / 0 drafts** and
no visible error — `FetchSourceAsync` had silently degraded to its bare `"Module: {name}"` placeholder
(no code fetched at all) while every failure reason (missing Repository/Branch, a moved/missing frontend
`apis/` folder, an ADO Git 401/403, zero files under the configured paths) was only ever logged
server-side (`_log.LogDebug`/`LogWarning`) and never published to the session's event bus, so the
console showed a plain "Fetched source (14 chars)" **Success** line with no clue why.
- `QaLoopOrchestrator.FetchSourceAsync`/`FetchFrontendApisAsync` now take the session's event key and
  publish a `Warn`-level `System` event at every point they degrade or hit a partial failure: missing
  Repository/Branch; the frontend `apis/` folder not found (with a hint to check the module's Frontend
  Path); per-directory/module-name ADO Git read failures (rolled up, with a hint to check the PAT's
  `Code (Read)` scope); and the final zero-files-found case.
- The `RunPipelineInnerAsync` "Fetched source (N chars)" line itself now renders as **Warn** (not
  Success) when the result is exactly the bare-placeholder text, instead of an all-green checkmark that
  hides a completely code-context-free run.
- No behavior change to a *successful* fetch — this is pure observability; 182/182 tests pass.
- **Root cause still open** for the reported `orders` run specifically — the fix makes the *next* run
  self-diagnosing (the console will now say exactly which of the above happened) but this instance
  couldn't be re-verified live in this session (see `docs/progress/RalphLoop/qa-loop-v2/progress.md` for
  the investigation notes and what to check).

## 2026-07-29

### feat(qa-runs): Cancel + delete for QA Loop run entries
Each run entry on the QA Runs board can now be cancelled while in-flight and deleted once terminal.
- **Backend:** `POST /api/qaloop/runs/{runId}/cancel` — signals a run-scoped `CancellationToken` (new `RunCancellationRegistry` singleton, keyed by run id so it can't collide with session ids) that kills the Playwright child process; `RunExecutionService` then marks the run `Cancelled`. Orphaned runs (no live token) are flipped to `Cancelled` directly. Rejects a terminal run with 409.
- **Backend:** `DELETE /api/qaloop/runs/{runId}` — removes the run row + its `TestFailure` records and best-effort deletes the on-disk artifact directory (`IPlaywrightRunner.DeleteRunArtifacts`). Refuses an in-flight run with 409 (cancel first).
- The `Start` background task now runs under the registered token (previously uncancellable). Existing cancel plumbing (`PlaywrightRunner` process-tree kill, `ExecuteAsync` `OperationCanceledException` handler) was already in place — only the token wiring + endpoints were missing.
- **Frontend:** QA Runs table row shows a **Cancel** button (in-flight) or **Delete** button (terminal); a Cancel-run button also appears in the run drawer; delete goes through a confirmation dialog. Verified end-to-end against PostgreSQL (start → terminal → cancel-guard 409 → delete 204 → gone) plus the browser delete flow.

### feat(qlv2): QA Loop v2 Slab 2 — UI Testing Phase (COMPLETE)
All 8 tasks (QLV2-201..208) VERIFIED. See [`docs/features/qa-loop-ui-phase.md`](features/qa-loop-ui-phase.md) for details.
- **DB:** 4 new columns — `ApiTestSessions.HasUiPhase/UiStatus`; `ApiTestDrafts.DraftKind/Bucket`. `ApiDraftKind` + `ApiUiStatus` enums. SQLite + Npgsql migrations applied.
- **Settings:** `QaLoopOptions` += `AppUrl/AppUsername/AppPassword/PlaywrightRoot`; runtime-settings registered; `scripts/fixtures/auth.setup.ts` Playwright login scaffold.
- **Backend:** `UiCaseAuthorAgent` + embedded `ui_case_author_system.txt`; `IPlaywrightAgentService` / `PlaywrightAgentService` (generate + heal via Claude CLI); orchestrator `AuthorUiCasesAsync`, `GenerateUiScriptsAsync`, `HealSpecsAsync`, `EnsureUiBucketSuitesAsync`; `PlaywrightRunner.EnableCapture`; three new controller endpoints.
- **Frontend:** `hasUiPhase` toggle on scan form; `UiPhasePanel` (author/review/scripts sub-panels); "UI Phase" tab in session detail.
- **Tests:** 3 new orchestrator tests (AuthorUiCases / GenerateUiScripts / HealSpecs paths); 163/163 pass.

### feat(qlv2): QA Loop v2 Slab 1 — Free-text context + merge-refine (COMPLETE)
All 13 tasks (QLV2-101..113) VERIFIED. See [`docs/features/qa-loop-free-text.md`](features/qa-loop-free-text.md) for details.
- **DB:** 3 new columns on `ApiTestSessions` (`AdditionalContext`, `GenerateContext`, `LastFeedback`); SQLite + Npgsql migrations applied.
- **Backend:** `ModuleAnalyzerAgent.RefineInventoryAsync` + `additionalContext` injection; generator `RefineAsync` overloads + `extraContext`; `QaLoopOrchestrator.RerunAnalyzeAsync`/`RefineDraftsAsync` + route/workflow matchers; `QaLoopController` three new endpoints; contracts.
- **Frontend:** `qaLoop.ts` API additions; `qaLoop.tsx` — start form additional-context field, AwaitingReview refine-analysis panel, DraftsByLevel per-draft feedback + regenerate.
- **Tests:** 2 new tests (`RefineDrafts_updates_targeted_removes_flagged_*` + `RerunAnalyze_returns_*`); 158/158 pass.

### fix(pgm): PGM-014 — NoClobber DotNetEnv + Postgres Npgsql migration verified
- `DotNetEnv.Env.TraversePath().NoClobber().Load()` — shell env vars win over `.env` (12-factor).
- `AddQaLoopFreeTextContext` Npgsql migration generated + applied.
- Docker Postgres round-trip verified with story 177540 (Author Tests haiku test run to AwaitingTester).


- Merged `origin/master` (168 remote commits) into local `master` via `--allow-unrelated-histories`.
- Fixed `GeneratedTestCase` CS0104 naming collision introduced by the merge: renamed the lightweight dedup record in `IExistingTestsIndexService` → `DedupTestCase` (`AgentOrchestrator` and `ExistingTestsIndexService` updated).
- Fixed `QaAutomation.Migrations.Npgsql.csproj` XML comment containing illegal `--` sequences.
- Added `ITextSearchProvider` (SqliteFts5Search) registration to all 5 test harnesses so `DatabaseInitializer` DI resolves correctly.
- Added `NullCodeContextService` / `NullExistingTestsIndexService` stubs to `AgentOrchestratorTests` and `BugOrchestratorTests` to match new orchestrator constructor signatures.
- **156/156 tests pass.**

### feat(ate): ATE-034 — EF migration `AuthorTestsContextAndIndex`
- Adds 6 nullable context/reference-plan columns to `TestSessions`.
- Adds `TestPlanScans` and `ExistingTestCaseIndexEntries` tables.
- Migration file: `20260729012836_AuthorTestsContextAndIndex`.

---

## 2026-07-28

### feat(ate): Author Tests Enhancements — Phase 1, 2, 3
Full implementation of free-text context, code discovery, refine loops, and existing-test dedup.
See [`docs/features/author-tests-enhancements.md`](features/author-tests-enhancements.md) for details.
- **38 tasks** (ATE-001 through ATE-039) implemented and pushed in branch `claude/chat-session-flzhjh`.
- Backend: new `TestSession` columns, `AuthorRunContext`, extended contracts, `ICodeContextService` / `CodeContextService`, `IExistingTestsIndexService` / `ExistingTestsIndexService`, `TestPlanScan` + `ExistingTestCaseIndexEntry` entities, Analyst/Tester refine overloads, `AgentOrchestrator` refine methods + `ReconcileDrafts`, 4 new controller endpoints, DI singletons.
- Frontend: pipeline step insert (Discover = step 2), Analyst refine panel, Tester refine UI (per-test + overall), reference-plan pickers (start form + Analyst gate), dedup result card, code-context card, API methods for all new endpoints.

### feat(pgm): PostgreSQL Migration — Phase 1, 2, 3 + migrations-assembly scaffold
Full dual-provider plumbing + FTS abstraction.
See [`docs/progress/RalphLoop/postgresql-migration/progress.md`](progress/RalphLoop/postgresql-migration/progress.md) for details.
- `ITextSearchProvider` abstraction (`SqliteFts5Search` + `PostgresFtsSearch`).
- `DatabaseOptions` + `DATABASE_PROVIDER` env-var boot-time provider selection.
- `KnowledgebaseService` and `ExistingTestsIndexService` delegate all FTS to the provider.
- `QaAutomation.Migrations.Npgsql` project scaffolded; Npgsql Initial migration pending.
