# Changelog

Newest entries first.

---

## 2026-08-02

### feat(qa-loop): close rail / push / framework gaps
- **Rail rebased:** Retrieve KB → Plan Intake → API Analyst (+ coverage) → API Tests → Push (API) → UI Analyst → UI Tester → Push (UI). Code Scan is internal to Analyst (no circle). Top Coverage/Drafts/UI/Push tabs removed.
- **Start Scan** works with automation-unit picker; `autoHandoff` defaults **false** (Analyst gate).
- **Push lanes:** API vs UI (`?lane=` / body); Push (UI) works after `ApiDone`; per-draft ADO stream retry; partial push stays re-pushable.
- **Framework Push:** single Slug, registry merge (no clobber), generators target `common/fixtures/api.fixture` + SpecNormalizer product mode.
- **New Scan MD attach:** scan-scoped `.md` file/folder (not permanent KB); injected into analyzer + generators.
- **Explain → Heal | Draft Bug** (Draft Bug links Find Bugs; Heal calls healSpecs).

### feat(qa-loop): automation-unit picker + CPQ+ E2E (Deals sibling)
- New Scan picks **automation units** (not only side-menu routes): e.g. **CPQ+ · under Deals**.
- CPQ admin (Settings CPQ tab / `/cpq/admin*`) stays under unit `cpq-plus`.
- Data Review / Feedback deferred (no FRS docs yet); aliases remain for Plan mapping only.
- Impact-map seeds `@cpq-plus`; live CPQ session wrote `automation/modules/cpq-plus/` + registry.
- Cancel marks parked sessions Cancelled; demo seeder opt-in via `QALOOP_SEED_DEMO=1`.

### feat(bridge): Phase 4 impact + automation CI + Push git hint (INT-401..403)
- Seed/simulate product `impact-map` from read-only `build-qa.yml` path includes (Scripts Board).
- Product `automation/pipelines/` Execute runs API + UI projects for impacted tags (no `*-QA` edits).
- QA Loop Push tab shows repo-lane git status under `automation/` (Push ≠ CI).
- Ralph: INT-401..404.

### feat(qa-loop): UX slab — Activity rail, clickable steps, UI cards (INT-UX-01..04)
- Session details: left step body + always-on Activity; clickable unlocked pipeline steps.
- UI script gen shows cards with Automated/Remaining; refine cancels in-flight gen (L14).
- Push dual lanes labeled Repo | ADO.

### feat(bridge): onboard messaging automation unit (INT-501)
- Impact-map seed + path sim for `@messaging` from `messaging/build/build-qa.yml` + Messenger FE.
- Pipeline path triggers include messaging; no empty `modules/messaging` scaffold.

### feat(bridge): polish product mode — deep-link + retire private inventory (INT-901)
- Settings `qaloop.automation_pipeline_url` deep-links from Push when set.
- With `AUTOMATION_ROOT` set, Scripts Board inventory ignores legacy `backend/data/scripts`.
- Notify owner = existing single Teams webhook; product pipeline already publishes JUnit (no new ingest).

### feat(bridge): Phase 3 structured writes under product automation/ (INT-301..304)
- With `AUTOMATION_ROOT` set, QA Loop Generate/Push writes API schemas + specs into
  `modules/{unit}/api/...`, UI specs into `modules/{unit}/ui/tests/{bucket}/`, and heals in place.
- First UI write can seed framework fixtures/auth under `automation/`; registry updated.
- Empty root keeps legacy `backend/data/scripts` behavior. Ralph: INT-301..305.

### feat(bridge): Phase 2 Plan intake — import / improve / patch Push (INT-201..205)
- Required Test Plan on QA Loop start; session columns `SuiteUnitMapJson` / `PlanImportMetaJson`.
- Suite→unit map (`POST .../plan-map`) + unit-filtered import (`POST .../plan-import`) with
  thin/rich/gap drafts and AdoTestCaseId dedup.
- Orchestrator Plan Intake after Retrieve KB (`ImportingPlan` / `plan-intake`); analyzer,
  generators, and UI author consume imported drafts (L1 suite injection).
- Push patches existing TCs via `UpdateTestCaseAsync`; creates only gap drafts.
- Ralph: `qa-loop-automation-integrate` Phase 2.

### feat(scripts-board): run framework specs with AUTOMATION_ROOT cwd (INT-103)
- When selected specs live under `qaloop.automation_root`, Scripts Board runs Playwright with
  cwd = that folder and an overlay config (reporters still land in ADO_Tester run dirs).
- Inventory reads `AUTOMATION_ROOT` via `IOptionsMonitor` so Settings changes apply live.

### feat(scripts-board): inventory product AUTOMATION_ROOT (INT-102)
- Scripts Board inventories `{AUTOMATION_ROOT}/modules/{unit}/api|ui/**/*.spec.ts` when
  `qaloop.automation_root` is set (legacy private scripts still scanned when present).
- Live: CRM deals specs show as orphan_disk until linked drafts exist.

### feat(bridge): AUTOMATION_ROOT setting (INT-101)
- `qaloop.automation_root` / env `AUTOMATION_ROOT` — absolute path to product `{repo}/automation/`.
- Empty keeps derived FrontendRoot sibling + legacy Playwright root. Settings → QA Loop panel.
- Ralph: `qa-loop-automation-integrate` INT-101.

### docs(bridge): lock ADO_Tester ↔ product `/automation` contract
- Feature page: [`docs/features/automation-framework-bridge.md`](../features/automation-framework-bridge.md)
  — write-only `automation/`, automation units, Plan AI map + thin→improve, structured Push
  (disk + ADO + git), parallel CI, impact API+UI, end-state QA Loop flow.
- Ralph loop: `docs/progress/RalphLoop/qa-loop-automation-integrate/` (`INT-001`).

### fix(runs): never mark infra/setup crashes as Passed
- Scripts Board + QA Runs used `Failed > 0 ? Failed : Passed`, so Playwright setup crashes
  (`ExitCode ≠ 0`, junit `0/0/0` — e.g. missing `@playwright/test`) showed **Passed**.
- Both now use `PlaywrightRunResult.ResolveStatus()`: failures → Failed; non-zero exit or
  zero tests executed → **Error** (+ `ErrorMessage`); otherwise Passed.

### chore(scripts-board): drop trends/flakiness
- Removed Launch-panel trends/flakiness UI, `GET /trends` + `/flakiness`, `ScriptTrendsService`, and client helpers.

### feat(scripts-board): Power Console redesign
- Scripts Board is the sole Playwright **launch** surface (dashboard layout: modules · specs ·
  Launch · live terminal · runs rail). QA Runs is **logs/history only** (Start form removed).
- Run `Profile` on Scripts Board runs (+ Npgsql migration); report serve; spec-content preview;
  fullscreen isolated popup (`/scripts-board/fullscreen`); All-labeled filter selects.
- Ralph: `SB-401..407` · design: `docs/progress/RalphLoop/scripts-board/power-console.md`.

### feat(scripts-board): inventory · runs · live terminal · trends
- Completes Ralph loop SB-301..308: `@qa-meta`, inventory reconcile, execution API (negative RunId),
  raw-ANSI `ScriptsBoardRunner` + `term` SSE, trends/flakiness from Allure `history.jsonl`, and SPA
  page with xterm TermConsole + trends/flakiness panel.
- Nav: **Scripts Board** → `/scripts-board`. Feature doc: `docs/features/scripts-board.md`.

## 2026-08-01

### fix(qa-loop): UI cases always start end-to-end from login
- Every UI case (smoke / regression / e2e) now requires Login → dismiss email-registration
  popup → open module in side menu, then feature steps.
- Enforced in `qa_loop_ui_case_author_system.txt` + hard prepend/dedupe in
  `QaLoopUiCaseAuthorAgent.EnsureBootstrapSteps` (also used by legacy inventory author).
- Playwright script gen skips Login/popup (storageState) and starts at module navigation.
- **Childlike granularity** (same bar as Author Tests Tester): every click/nav/field is its own
  step with its own expected — written so Playwright agents can follow the list blindly.

### feat(qa-loop): Tester-depth UI case review
- QA Loop UI Phase `AwaitingReview` now mirrors Author Tests' Tester step: StatTiles → review
  dialog with full case cards (preconditions, steps table, edit/approve/reject, per-case feedback,
  regenerate). Independent copy in `UiCaseReviewPanel.tsx`.
- New `POST /api/qaloop/sessions/{id}/refine-ui-cases` re-authors pending UiCase drafts with
  feedback while keeping Approved cases.

### feat(qa-loop): tabbed editable UI Analysis (Author Tests–style)
- UI Phase analysis is now a horizontal-tab panel (Overview / Features / Flows / Screens /
  Must test / Must not / Questions) with Edit+Save and **per-tab refine feedback**.
- New `PATCH /api/qaloop/sessions/{id}/ui-analysis` persists reviewer edits as camelCase JSON.

### fix(qa-loop): UI Analysis card empty on AwaitingUiAnalysisReview
- Root cause: `UiAnalysisJson` was serialized with System.Text.Json PascalCase (`Features`, …)
  while the SPA read camelCase (`features`, …) — card title rendered, body stayed blank.
- Orchestrator now persists/reads with `JsonSerializerDefaults.Web`; SPA parser accepts both casings
  so existing sessions display without re-running UI Analyst.

### feat(llm): Cursor CLI featured haiku / composer / grok 4.5
- Cursor catalog now pins **Haiku**, **Composer 2.5**, **Cursor Grok 4.5** with effort + context
  (Default / 200k / 1M); collapses effort-suffixed list-models duplicates.
- `--model` composition normalizes aliases (`grok-4.5`, `composer`, grok `*-low|medium|high` ids)
  into parameterized forms like `haiku[effort=low,context=200k]`.

### feat(llm): AppBar model chips + Cursor CLI provider
- `GET /api/settings/provider` now includes `active` `{ model, effort, context }` for display-only
  AppBar chips (edit remains on Settings → LLM Providers).
- Claude CLI: `claude_cli.effort` / `--effort`; catalog endpoint `GET /api/settings/claude-cli/catalog`.
- Copilot curated catalog expanded; context chip labels map `default`→`200k`, `long_context`→`1M`.
- New **CursorCli** provider (`agent` / `cursor-agent`, scratch workspace, live `--list-models` catalog).
  Setup: [`docs/setup/cursor-cli.md`](../setup/cursor-cli.md).

### fix(ado): force IPv4 for Azure DevOps HttpClient
- Root cause: on this Windows network, .NET's default dual-stack connect to `dev.azure.com` hangs
  on IPv6 until the 30s HttpClient timeout; curl/IPv4 succeeds in ~1–2s.
- ADO named client now uses `Ipv4SocketsHttpHandler` (`ConnectCallback` → IPv4 only).
- Verified: `GET /api/diagnostics/workitem/100002` ~1.3s; Author Tests session starts on Haiku.

### chore(database): complete PostgreSQL-only cutover
- Removed the retired SQLite/provider-selection guidance and unused storage option.
- PostgreSQL connection configuration is now required; Npgsql migrations and `tsvector`/GIN search
  are the only documented database path.

### fix(qa-loop): shared API auth + UI storageState + auto-heal on run
- Renamed `LEAD_API_BASE_URL` → `AUTOMATION_API_BASE_URL` (legacy fallback kept).
- API Playwright runs always use the shared host + minted bearer (no run profile host/static token);
  child env also sets `BASE_URL`.
- UI runs (`IsUiRun`): app `baseURL`, `scripts/fixtures/auth.setup.ts` globalSetup/`storageState`
  (Not Now dismiss, 30m reuse), 90s timeout.
- Run Scripts auto-heals failed UI specs (keep `Approved`, max 3 re-runs); Heal button removed from UI.
- See [`docs/features/qa-loop-ui-phase.md`](../features/qa-loop-ui-phase.md).

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
- Docker Postgres round-trip verified with story 100002 (Author Tests haiku test run to AwaitingTester).


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
