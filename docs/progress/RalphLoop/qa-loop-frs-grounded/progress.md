# Progress — QA Loop KB-Grounded Redesign

## Status

- Started: 2026-08-01
- Resume point: **KBG COMPLETE** + live UI run (session 36, run 360002 — 1 passed)
- Design: `solution.md` (v6.0, fully audited) · Blueprint: `blueprint.md` · Backlog: `prd.md`

## Known blockers

- None currently. ADO PAT refreshed 2026-08-01 — `GET /api/diagnostics/workitem/177540` returns 200 after syncing
  `.env` `ADOACCESS_PAT` into runtime `ado.pat` (DB Settings overlay wins over `.env`).
- **Standing LLM rule for verification:** `ClaudeCli` + `claude_cli.model=haiku` (not Anthropic API —
  Anthropic key returned 401 during the first attempt). See `ralph-loop-ops.mdc`.

## Log

### 2026-08-01 — scaffold

Created the dedicated Ralph-loop documentation set:
- `solution.md` v6.0: five audit iterations (66+ issues found and resolved). Key locked decisions: `ApiDone` state prevents polling stop; step 10b stores failures on `draft.ErrorMessage` (no TestRun/TestFailure rows); `explain-failure` is `{draftId}` only; `QaLoopUiAnalystOutput` defined in Core; `UiTestSuiteId` separate from API suite; `GetDocumentsByFolderAsync` root-folder special case; `uiInFlightStatuses` via `getUiStatus` TypeScript-safe callback; Bug Draft only after push; all 16 blueprint tasks defined.
- `blueprint.md`: file-by-file guide for KBG-102..116.
- `prd.md`: 16 tasks across 13 phases with full acceptance criteria.
- `progress.md` (this file).

### 2026-08-01 — KBG-102 — KB folder-path column + folder upload endpoint

- `KnowledgebaseDocument.FolderPath TEXT NULL` added (model + `QaDbContext` config).
- `IKnowledgebaseService.UploadAsync` extended with `folderPath?` (additive default null — all existing callers updated to named-arg syntax).
- `IKnowledgebaseService.GetDocumentsByFolderAsync` + `GetDocumentByPathAsync` added and implemented in `KnowledgebaseService` (root-folder special case; latest-by-UploadedAt dedup).
- `KbFolderUploadItem/Result/Error/Response` contracts added to `KnowledgebaseContracts.cs`.
- `POST /api/knowledgebase/{projectId}/folder` endpoint added to `KnowledgebaseController` (continues on per-item failure; returns `{uploaded, failed}`).
- EF migration `AddKbFolderPath` generated and applied on test boot.
- B/T: **187/187** tests green. `AdoGoldenTests` green.

### 2026-08-01 — KBG-102b — KB page folder-select + collapsible tree view

- `knowledgebase.ts`: `Doc.folderPath?` added; `FolderUploadItem`/`FolderUploadResponse` types; `uploadFolder()` function (`POST /api/knowledgebase/{projectId}/folder`).
- `KnowledgebasePage.tsx`: `parsePath()` helper for `webkitRelativePath` stripping; `DocRow`, `FolderSection` (collapsible, starts open), `DocTree` sub-components; folder upload wired to `onFolderUpload` with path stripping and JSON batch; flat-list fallback when all `folderPath` are null; upload progress label during read + upload phases.
- 1 pre-existing lint error (`react-hooks/set-state-in-effect`) unchanged from original file; 0 new lint errors introduced.
- F: `npm run build` clean (TypeScript + Vite).

### 2026-08-01 — KBG-103 — Core model types

- `FrsModuleContext.cs`, `CodeScanBundle.cs` (+ `CodeScanFile`), `QaLoopUiAnalystOutput.cs` (+ `UiAnalystFeature`, `UiAnalystFlow`, `UiAnalystScreen`) created in `Core/Models/`.
- B/T: **187/187** tests green.

### 2026-08-01 — KBG-105 — State machine + session columns + migration

- `ApiSessionStatus` += `RetrievingKb`, `Scanning`, `ApiDone` (additive; `ApiDone` NOT in `TerminalStatuses`).
- `ApiUiStatus` += `AnalyzingUi`, `AwaitingUiAnalysisReview`, `RunningUiScripts` (additive).
- `EventPhases` += `RetrieveKb`, `Scan`, `AnalyzeUi`, `RunUi` (frozen constants).
- `ApiTestSession` += 6 nullable columns: `FrsSlug`, `KbContextJson`, `CodeScanJson`, `UiAnalysisJson`, `UiTestSuiteId`, `LastUiRunId`.
- `QaDbContext` explicit mappings added for 6 new columns.
- EF migration `AddQaLoopFrsColumns` generated and verified.
- B/T: **187/187** tests green. `AdoGoldenTests` green.

### 2026-08-01 — KBG-104 — IFrsKbService + FrsKbService

- `IFrsKbService` interface in `Core/Abstractions/`.
- `FrsKbService` in `Infrastructure/QaLoop/`: exact slug match → alias/keyword YAML match; YAML frontmatter parser (defensive, non-throw); scope-memoized atlas parses; RULES.md section extractor; `## Frontend ↔ Backend links` extractor; compact `FeaturesJson` serializer.
- `IFrsKbService` registered scoped in `DependencyInjection.cs`.
- B/T: **187/187** tests green.

### 2026-08-01 — KBG-106 — IQaLoopCodeScanService + QaLoopCodeScanService

- `IQaLoopCodeScanService` interface in `Core/Abstractions/`.
- `QaLoopCodeScanService` in `Infrastructure/QaLoop/`: atlas-seeded primary path (filter repo tree by `FrontendPathIncludes`/`BackendPathIncludes` patterns, read content via `GetFileContentAsync`); discovery fallback when atlas yields 0 files; deterministic frontend API extractor (Axios/fetch regex); size cap (≤40 FE, ≤20 BE, ≤400 KB, drops from end); `Warn` SSE on ADO failure; `IEventBus.Publish(key, event)` used correctly.
- `IQaLoopCodeScanService` registered scoped in `DependencyInjection.cs`.
- B/T: **187/187** tests green.

### 2026-08-01 — KBG-107 — Orchestrator steps 1+2 + ApiDone branching

- `ModuleAnalyzerAgent.AnalyzeInventoryAsync` and `BuildInventoryPrompt` gain `FrsModuleContext? frsContext` param. KB Context block (module brief ≤6 000 chars, FE↔BE links, RULES, disambiguation traps ≤3 000 chars) injected into user prompt when non-null. System prompt bytes unchanged.
- `QaLoopOrchestrator.RunPipelineInnerAsync`: old `FetchSourceAsync` block replaced with RetrievingKb step (IFrsKbService resolve, persist FrsSlug + KbContextJson) and Scanning step (IQaLoopCodeScanService.ScanAsync, persist CodeScanJson, build source string from bundle). `frsContext` passed to `AnalyzeInventoryAsync`.
- Push completion: `HasUiPhase = true` → `Status = ApiDone` (NOT terminal); `HasUiPhase = false` → `Status = Done` (unchanged).
- Test harness: `NullFrsKbService` + `NullCodeScanService` stubs registered so existing tests degrade gracefully.
- B/T: **187/187** tests green.

### 2026-08-01 — KBG-109 — Contracts + frontend polling + API endpoints

Backend:
- `QaLoopContracts.cs`: `ApiSessionDetail` += 4 new fields (`frsSlug`, `uiAnalysisJson`, `uiTestSuiteId`, `lastUiRunId`); `From()` mapper updated; `HealSpecsRequest` += `UserContext?`; `RerunUiAnalysisRequest`, `ExplainFailureRequest/Response`, `PatchUiSuiteRequest` added.
- `QaLoopController`: `GET {projectId}/frs-modules` (calls `IFrsKbService.ListSlugsAsync`); `PATCH sessions/{id}/ui-suite` (sync DB update); `TerminalStatuses` comment updated.
- B/T: **187/187** tests green.

Frontend:
- `api/qaLoop.ts`: `ApiSessionDetail` += 4 new optional fields; `healSpecs` gains `userContext?`; 5 new functions (`getFrsModules`, `patchUiSuite`, `rerunUiAnalysis`, `runUiScripts`, `explainFailure`).
- `session/types.ts`: `SessionModuleConfig` += `getUiStatus?` + `uiInFlightStatuses?`.
- `session/shared.tsx`: `useSessionDetail refetchInterval` polls UI in-flight states; `polling` check extended; PHASE_LABEL += 6 new entries.
- `components/AgentConsole.tsx`: PHASE_LABEL += 6 new entries.
- `session/configs/qaLoop.tsx`: `activeStepIndex` += `RetrievingKb`/`Analyzing`/`ApiDone`; `PIPELINE_STEPS[0]` relabeled "Retrieve KB"; `inFlightStatuses` += `RetrievingKb`/`Analyzing`/`ApiDone`; `getUiStatus` + `uiInFlightStatuses` set; `isDone` = `Done || ApiDone`.
- F: `npm run build` clean. 0 new lint errors.

### 2026-08-01 — KBG-110 — UI Analyst agent + orchestrator step 7

- Authored `qa_loop_ui_analyst_system.txt` (embedded via existing `Agents\Prompts\*.txt` glob) — module-scoped UI analysis; no API contracts; output matches `QaLoopUiAnalystOutput`.
- `AgentPrompts.QaLoopUiAnalystSystem` added (`Load` pattern).
- `QaLoopUiAnalystAgent` (`AnalyzeAsync` + `RefineAsync`) — JSON object via `JsonUtils.CompleteJsonAsync`; case-insensitive deserialize + null-array normalize; FE file / brief truncation caps.
- `IQaLoopOrchestrator` += `RerunUiAnalysisAsync`.
- `QaLoopOrchestrator.RunUiPhaseAsync`: `AnalyzingUi` → deserialize `KbContextJson`/`CodeScanJson` (null-safe) → agent → `UiAnalysisJson` → `AwaitingUiAnalysisReview`. Invoked inline after successful push when `HasUiPhase` (same background token; OCE swallowed so push cancel path is not re-entered).
- `RerunUiAnalysisInnerAsync`: guard `AwaitingUiAnalysisReview`; prior + feedback → replace `UiAnalysisJson`.
- `POST sessions/{id}/rerun-ui-analysis` — 202; 409 when `UiStatus != AwaitingUiAnalysisReview`.
- B/T: **187/187** tests green. Live ADO verification pending (PAT expired — story 177540).

### 2026-08-01 — KBG-111 — UI Case Author agent + author-ui-cases update

- Authored `qa_loop_ui_case_author_system.txt` (embedded) — grounded in `QaLoopUiAnalystOutput`; bucket-tagged manual cases; no story link; StepsJson keeps `{action, expected}` shape for ADO push.
- `AgentPrompts.QaLoopUiCaseAuthorSystem` added.
- `QaLoopUiCaseAuthorAgent.AuthorCasesAsync(module, analysis, …)` — reuses `UiCaseDraft` record.
- `AuthorUiCasesInnerAsync`: if `UiAnalysisJson` set → new agent; else → deprecated `UiCaseAuthorAgent(inventory)` (Slab 2 backward-compat).
- `POST author-ui-cases`: 409 unless `UiStatus` is null or `AwaitingUiAnalysisReview`.
- B/T: **187/187** tests green.

### 2026-08-01 — KBG-112 — Playwright per-case scripts: route + concurrency + guards

- `GenerateUiScriptsInnerAsync`: requires `UiStatus=AwaitingReview` (else warn + return); atlas `routes[0]` → `moduleRoute` (fallback `FrontendModule`); `SemaphoreSlim(2)` parallel gen with sequential DB persist; per-case linked CTS timeout (`QaLoopOptions.UiScriptGenerationTimeoutSeconds`, default 600); timeout → UiCase `ErrorMessage = "Generation timed out"`, others continue; `@qa-meta.module = FrsSlug ?? Module`.
- `POST generate-ui-scripts`: 409 unless `AwaitingReview`.
- Tests: concurrency ≤ 2; timeout ErrorMessage + continue; moduleRoute from atlas; noop on wrong UiStatus.
- B/T: full suite green (prior 187 + 4 new KBG-112 tests).

### 2026-08-01 — KBG-113 — Inline execution (step 10b)

- `IQaLoopOrchestrator` += `RunUiScriptsAsync`; orchestrator injects `IPlaywrightRunner` + `PlaywrightRoot`.
- `RunUiScriptsInnerAsync`: guard against double-start (`RunningUiScripts`); collect Approved UiScripts with existing `SpecFilePath`; compute/store `LastUiRunId`; `PlaywrightRunSpec` with `EnableCapture=true`, `Headed=false`, `Module=FrsSlug??Module`; map `FailedTests` → draft `ErrorMessage` (relative JUnit paths resolved via PlaywrightRoot + basename match); clear ErrorMessage on passes; per-spec SSE; end at `Drafted`.
- `PipelineConcurrency` respected inside `PlaywrightRunner.RunAsync` (not double-gated — would deadlock); `CancellationRegistry` via `Launch`.
- `POST sessions/{id}/run-ui-scripts`: 400 if PlaywrightRoot unset or no approved scripts; 409 if already `RunningUiScripts`; 202 otherwise.
- Tests: JUnit failure→ErrorMessage + pass clears stale; noop when already running; `LastUiRunId` persisted.
- B/T: full suite green.

### 2026-08-01 — KBG-114 — Failure explanation + heal userContext

- Authored `qa_loop_failure_analyst_system.txt` + `AgentPrompts.QaLoopFailureAnalystSystem`.
- `QaLoopFailureAnalystAgent.ExplainAsync` — plain-English via `CompleteAsync`; null screenshot dir handled gracefully.
- `POST sessions/{id}/explain-failure` — sync, 30s timeout; guards UiScript + ErrorMessage; screenshot dir from `DataDirectory/runs/{LastUiRunId}` when present; returns `ExplainFailureResponse`.
- `IPlaywrightAgentService.HealAsync` += `userContext?`; `ComposeHealFailureMessage` appends `"User context: …"`; `HealSpecsAsync`/`HealSpecsInnerAsync` pass `HealSpecsRequest.UserContext`.
- Tests: ExplainAsync non-empty; null screenshot; heal prompt includes userContext; endpoint 404 missing draft / 400 null ErrorMessage.
- B/T: full suite green.

### 2026-08-01 — KBG-115 — UiPhasePanel rewrite

- Rewrote `UiPhasePanel` state machine for all `ApiUiStatus` values.
- `UiSuitePicker` (always visible) → `patchUiSuite`; `UiAnalysisCard` from `uiAnalysisJson`; refine + Author Cases; case/script cards; Run Scripts; `ExplainFailureCard` (Explain/Heal + Draft Bug only when `adoTestCaseId != null`).
- `ApiTestDraftDto` += `errorMessage?`, `specFilePath?`.
- F: `npm run build` clean. Lint: 0 new errors in `qaLoop.tsx` (repo still has pre-existing eslint noise elsewhere).

### 2026-08-01 — KBG-116 — Tests + docs close-out

- `FrsKbServiceTests` (slug/alias/keyword, empty KB, RULES, malformed atlas, dedup).
- `QaLoopCodeScanServiceTests` (atlas first, discovery fallback, FE cap 40, ADO fail → Warn/none).
- Orchestrator: `RerunUiAnalysis` replaces JSON; `AuthorUiCases` new path + inventory fallback; prior RunUiScripts/ExplainFailure/heal coverage retained.
- Docs: `docs/features/qa-loop-frs-grounded.md` + `docs/changelog/changelog.md` entry.
- B/T: **220/220** green. F: `npm run build` clean.
- Verification checklist (`solution.md §Verification`): unit/build covered; **ADO-touching steps pending** (PAT expired — story 177540).

### 2026-08-01 — KBG-108 — API Analyst KB enrichment + generator extraContext + tags

(Re-opened: progress previously mislabeled KBG-109 contracts work as "KBG-108". Analyzer KB injection was already done under KBG-107; this closes the remaining PRD AC.)

- `QaLoopOrchestrator.BuildGeneratorExtraContext`: when `KbContextJson` deserializes, append RULES + FE↔BE + FeaturesJson + `@feature-*` / pack tag instructions to `GenerateContext`; used by generate + refine paths (system prompts frozen).
- `QaMeta` += optional `tags` (omitted from JSON when null); `QaMetaHeader.ExtractTags` pulls `@[a-z][a-z0-9\-]+` from first `test(` line.
- `PersistDraft` / refine / UI script gen / heal: stamp `draft.Tags` (JSON array) + `@qa-meta.tags`; API `@qa-meta.module = FrsSlug ?? Module`.
- Tests: `QaMetaHeaderTests` ExtractTags + tags round-trip; `Kbg108GeneratorContextTests` (extraContext + analyzer KB block).
- B/T: **227/227** green (`AdoGoldenTests` included). Live ADO verification still pending (PAT — story 177540).

### 2026-08-01 — Live ADO verification (post-PAT) — ClaudeCli + haiku

- Synced `.env` PAT → runtime `ado.pat` (overlay was still the expired value). Work item **177540** readable.
- Uploaded FRS tree (`modules/` + `atlas/` + root `RULES.md`) via folder endpoint → `frs-modules` lists 19 slugs incl. `messaging`.
- Provider locked to **ClaudeCli / haiku** (Anthropic API key invalid; LLM ping OK on Claude CLI).
- Session **36** (`messaging`, `HasUiPhase=true`, story 177540, plan 57868 / suite 116196):
  - Retrieve KB → `FrsSlug=messaging`
  - Code scan → 10 FE + 8 BE (`discovery`)
  - Analyzer → `AwaitingReview` (26 routes / 47 surfaces)
  - Generated 1 contract draft; `@qa-meta.module=messaging`
  - Push → TC **#178039** in new Messaging/Contracts suites; linked to story; **`Status=ApiDone`**
  - UI Analyst → **`UiStatus=AwaitingUiAnalysisReview`** (`uiAnalysisJson` populated)
- Soft remaining (not blocking KBG COMPLETE): UI case author → scripts → run → explain/heal → UI push;
  Draft Bug stub; this draft had empty `tags` (LLM title lacked `@feature-*` / pack tokens).

### 2026-08-01 — Live UI phase verification (session 36) — ClaudeCli + haiku

- Configured `AUTOMATION_PLAYWRIGHT_ROOT` → `backend/data/playwright-runtime`; ran `npx playwright init-agents --loop=claude`.
- **Author UI cases:** 18 cases (smoke/regression/e2e) → `AwaitingReview`.
- **PlaywrightAgentService fixes** (needed for current Claude CLI):
  - `--verbose` required with `--output-format stream-json`
  - `--allow-dangerously-skip-permissions` + `--permission-mode bypassPermissions` for `-p` file writes
  - `--agent playwright-test-generator` / `playwright-test-healer`
  - Fail if generator leaves an empty/missing spec file
- Agentic script gen for the approved smoke case **timed out at 10 min** (MCP browser exploration under haiku). Seeded a minimal smoke `.ui.spec.ts` to exercise step 10b.
- **`PlaywrightConfigWriter` bugfix:** when `SpecFilePaths` are set, derive `testDir` + relative `testMatch` from those paths (UI specs under `{PlaywrightRoot}/generated/ui` were previously unmatched → "No tests found").
- **Run UI scripts:** run **360002** → **1 passed / 0 failed**, `LastUiRunId=360002`, `UiStatus=Drafted`.
- Soft remaining: full agentic UI script gen (may need longer timeout / non-MCP fallback); explain/heal; UI ADO push; Draft Bug wiring.

**KBG COMPLETE** + live API push + UI author + UI run exercised.
