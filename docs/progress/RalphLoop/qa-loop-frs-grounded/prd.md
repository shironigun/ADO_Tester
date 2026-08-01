# PRD — QA Loop KB-Grounded Redesign (task backlog)

Tasks `KBG-###`, grouped by phase, **built in order**. One task per Ralph-loop iteration; flip `[ ]`→`[x]`
here and log in `progress.md` only after the task is implemented **and** verified.

Acceptance-criteria shorthand: **B** = `dotnet build` clean · **T** = `dotnet test` green (incl. `AdoGoldenTests`) · **F** = `npm run build` + `npm run lint` clean on touched files.

Blueprint reference: `blueprint.md` has file-by-file detail for every task.

---

## Phase 1 — KB enhancement (folder-tree upload + tree view)

- [x] **KBG-102** — `KnowledgebaseDocument.FolderPath TEXT NULL`; extend `IKnowledgebaseService.UploadAsync` with `folderPath?`; add `GetDocumentsByFolderAsync` + `GetDocumentByPathAsync`; implement in `KnowledgebaseService`; migration `AddKbFolderPath`; add `KbFolderUploadItem/Result/Error/Response` contracts; `POST /api/knowledgebase/{projectId}/folder` endpoint. **AC:** B/T; migration applies; `UploadAsync` with no `folderPath` behaves identically to before; `GetDocumentsByFolderAsync("modules")` returns only `modules/` docs; root-folder query (`""`) returns root docs only; `GetDocumentByPathAsync` returns latest by `UploadedAt`.

- [x] **KBG-102b** — KB page: folder-select button (`webkitdirectory`); path-strip algorithm; POST to `/folder`; `SimpleTreeView` render (folder nodes + leaf nodes + delete + expand-all); flat-list fallback for legacy documents. **AC:** F; uploading the FRS directory shows correct tree; existing flat-list documents still display; delete per-file and per-folder works.

---

## Phase 2 — Core model types + state machine

- [x] **KBG-103** — `FrsModuleContext`, `CodeScanBundle`, `CodeScanFile`, `QaLoopUiAnalystOutput`, `UiAnalystFeature`, `UiAnalystFlow`, `UiAnalystScreen` records in `Core/Models/`. **AC:** B; all types compile; `System.Text.Json` round-trip test: serialize then deserialize each type produces identical values.

- [x] **KBG-105** — `ApiSessionStatus` += `RetrievingKb`, `Scanning`, `ApiDone`; `ApiUiStatus` += `AnalyzingUi`, `AwaitingUiAnalysisReview`, `RunningUiScripts`; `ApiTestSession` += 6 new columns (`FrsSlug`, `KbContextJson`, `CodeScanJson`, `UiAnalysisJson`, `UiTestSuiteId`, `LastUiRunId`); `EventPhases` += `RetrieveKb`, `Scan`, `AnalyzeUi`, `RunUi`; migration `AddQaLoopFrsColumns` (both providers). **AC:** B/T; migration applies; all 6 columns present; new enum values serialize as their exact C# member name strings; `AdoGoldenTests` green; `TerminalStatuses` does NOT include `ApiDone`.

---

## Phase 3 — IFrsKbService + IQaLoopCodeScanService

- [x] **KBG-104** — `IFrsKbService`; `FrsKbService` (slug resolution: exact → alias → keyword; YAML frontmatter parser; `GetDocumentsByFolderAsync` + `GetDocumentByPathAsync` usage; RULES.md section extraction; `FeaturesJson` serialization; defensive non-throw on parse errors; scope memoization); register `IFrsKbService` scoped in `DependencyInjection.cs`. **AC:** B/T; unit tests: exact slug match, alias match, keyword match, empty KB → null, non-FRS doc in modules/ → skipped gracefully, RULES section extracted correctly, deduplication returns latest UploadedAt; `ListSlugsAsync` returns filenames only (no disk reads).

- [x] **KBG-106** — `IQaLoopCodeScanService`; `QaLoopCodeScanService` (atlas glob seeds → `GetFileContentAsync`; discovery fallback; deterministic API extractor; caps enforced; ADO failure → `Warn` SSE + empty bundle; `DiscoverySource` field); register scoped. **AC:** B/T; unit tests: atlas paths used when `frsContext` provided; discovery fallback when atlas produces no files; caps truncate at limits; ADO exception → Warn event emitted + empty bundle returned.

---

## Phase 4 — Orchestrator steps 1 + 2 (Retrieve KB + Code Scan)

- [x] **KBG-107** — `QaLoopOrchestrator.RunPipelineInnerAsync`: insert `RetrievingKb` → `Scanning` steps before `Analyzing`; resolve `IFrsKbService` + `IQaLoopCodeScanService` from background scope; persist `FrsSlug`, `KbContextJson`, `CodeScanJson`; remove old `FetchSourceAsync` call from this path (replace with `codeScanBundle`-to-string conversion); push branch: `HasUiPhase = true` → `ApiDone` else `Done`; inline `RunUiPhaseAsync` call after `ApiDone`. **AC:** B/T; scan without FRS uploaded → `Warn` SSE + pipeline continues; scan with FRS → `FrsSlug` set on session; `ApiDone` set after push when `HasUiPhase = true`; `Done` set when `HasUiPhase = false` (existing sessions unchanged); `AdoGoldenTests` green.

---

## Phase 5 — API Analyst KB enrichment + generator extraContext + @qa-meta tags

- [x] **KBG-108** — `ModuleAnalyzerAgent.BuildInventoryPrompt` += `FrsModuleContext? frsContext`; KB Context block injection (module brief ≤ 6 000 chars, FE↔BE, RULES, traps from `AtlasMarkdown` ≤ 3 000 chars); generator `GenerateAsync` calls receive enriched `extraContext` (RULES + FE↔BE + FeaturesJson + tag instructions) when `frsContext != null`; `QaMeta.Build`/`Upsert` extended to extract `@[a-z][a-z0-9\-]+` tokens from first `test(` line of `SpecCode` into the `tags` field. **AC:** B/T; `AdoGoldenTests` green (system prompts unchanged); KB Context block appears in user prompt when `frsContext` non-null; tags extracted correctly from spec title; `@feature-*` and pack tags populated on generated drafts.

---

## Phase 6 — Contracts + scan form + frontend polling infrastructure

- [x] **KBG-109** — `QaLoopContracts.cs`: add `RerunUiAnalysisRequest`, `ExplainFailureRequest/Response`, `PatchUiSuiteRequest`, update `HealSpecsRequest` += `UserContext?`, update `ApiSessionDetail` += `frsSlug?`, `uiAnalysisJson?`, `uiTestSuiteId?`, `lastUiRunId?` (NOT `kbContextJson`/`codeScanjson`); update `ApiSessionDetail.From()` mapper; `QaLoopController` += `GET {projectId}/frs-modules` + `PATCH sessions/{id}/ui-suite` (sync). **AC:** B; `frs-modules` returns string array; `ui-suite` PATCH updates `UiTestSuiteId` on session; `ApiSessionDetail` contains new fields; large JSON fields excluded.

- [x] **KBG-109b** — Frontend: `api/qaLoop.ts` += `getFrsModules`, `rerunUiAnalysis`, `runUiScripts`, `explainFailure`, `patchUiSuite`, update `healSpecs` + `ApiSessionDetail` type; `qaLoop.tsx`: module field → `Autocomplete` combobox, Advanced expander, `activeStepIndex` updated, `PIPELINE_STEPS[0]` relabeled "Retrieve KB", `qaLoopConfig.inFlightStatuses` extended, `getUiStatus` + `uiInFlightStatuses` added, `PushPanel isDone` fix, `DraftsByLevel readOnly ApiDone`, `"KB: {slug}"` chip; `shared.tsx`: `SessionModuleConfig` += `getUiStatus?` + `uiInFlightStatuses?`, `useSessionDetail refetchInterval` updated; `PHASE_LABEL` new entries in `shared.tsx` and `AgentConsole.tsx`. **AC:** F; scan form shows module dropdown; free-text still works; `PIPELINE_STEPS[0]` label is "Retrieve KB"; polling continues for new states; `PushPanel` shows "Pushed ✓" for `ApiDone`.

---

## Phase 7 — UI Analyst agent + orchestrator step 7

- [x] **KBG-110** — `qa_loop_ui_analyst_system.txt` (author + add as embedded resource); `QaLoopUiAnalystAgent` with `AnalyzeAsync` + `RefineAsync`; `AgentPrompts.QaLoopUiAnalystSystem`; `IQaLoopOrchestrator` += `RerunUiAnalysisAsync`; `QaLoopOrchestrator.RunUiPhaseAsync` (step 7: `AnalyzingUi` → agent → `UiAnalysisJson` → `AwaitingUiAnalysisReview`); `RerunUiAnalysisAsync` + inner (guard `AwaitingUiAnalysisReview`, prior output + feedback → replace `UiAnalysisJson`); `QaLoopController` += `POST sessions/{id}/rerun-ui-analysis` (202, guard). **AC:** B/T; `AnalyzingUi` state set while agent runs; `UiAnalysisJson` populated; rerun replaces output; guard rejects non-`AwaitingUiAnalysisReview` state with 409.

---

## Phase 8 — UI Case Author agent + author-ui-cases update

- [x] **KBG-111** — `qa_loop_ui_case_author_system.txt` (author + embed); `QaLoopUiCaseAuthorAgent` with `AuthorCasesAsync(module, QaLoopUiAnalystOutput, …)`; `AgentPrompts.QaLoopUiCaseAuthorSystem`; `AuthorUiCasesInnerAsync`: backward-compat guard (`UiAnalysisJson != null` → new agent; else → deprecated `UiCaseAuthorAgent`); `author-ui-cases` controller guard (`UiStatus = null OR AwaitingUiAnalysisReview`; else 409). **AC:** B/T; session with `UiAnalysisJson` set → new agent used; old Slab 2 session (no `UiAnalysisJson`) → deprecated agent used; 409 when `UiStatus` is a non-permitted state.

---

## Phase 9 — Playwright per-case scripts (step 10 updates)

- [x] **KBG-112** — `GenerateUiScriptsInnerAsync`: `AwaitingReview` guard (409 if not); atlas `routes[0]` → `moduleRoute`; `SemaphoreSlim(2)` concurrency; 10-min per-case timeout via linked `CancellationTokenSource`; spec path uses `FrsSlug ?? Module`; `generate-ui-scripts` controller guard. **AC:** B/T; mock test: concurrency capped at 2; timeout → individual draft `ErrorMessage` set + others continue; `moduleRoute` from atlas passed to `GenerateScriptAsync`; 409 on wrong `UiStatus`.

---

## Phase 10 — Inline execution (step 10b)

- [x] **KBG-113** — `IQaLoopOrchestrator` += `RunUiScriptsAsync`; `QaLoopOrchestrator.RunUiScriptsAsync` + inner: `RunningUiScripts` state; `LastUiRunId` computed + stored; `PlaywrightRunSpec { RunId, SpecFilePaths, EnableCapture=true, Headed=false, Workers=null, Module=frsSlug??module }`; `PlaywrightRunner.RunAsync`; JUnit parse + `classname`-to-absolute-path normalization; `draft.ErrorMessage` set/cleared per result; SSE events per spec; `Drafted` state after; `PipelineConcurrency` + `CancellationRegistry` respected; `POST sessions/{id}/run-ui-scripts` (202); guards: `PlaywrightRoot` configured (400), `UiStatus != RunningUiScripts` (409), ≥ 1 Approved UiScript (400). **AC:** B/T; unit test: JUnit parse → correct draft `ErrorMessage` set; passing spec clears `ErrorMessage`; 409 on double-call; `LastUiRunId` persisted; `Status = Drafted` after run.

---

## Phase 11 — Failure explanation + heal update

- [x] **KBG-114** — `qa_loop_failure_analyst_system.txt` (author + embed); `QaLoopFailureAnalystAgent.ExplainAsync`; `AgentPrompts.QaLoopFailureAnalystSystem`; `POST sessions/{id}/explain-failure` (sync, 30s via `CancellationTokenSource`; guards: `DraftKind=UiScript`, `ErrorMessage!=null`; returns `ExplainFailureResponse`; screenshot dir from `LastUiRunId`; graceful if null); `IPlaywrightAgentService.HealAsync` += `userContext?`; `PlaywrightAgentService` appends `userContext` to failure message; `HealSpecsInnerAsync` passes `request.UserContext` to `HealAsync`. **AC:** B/T; unit test: `ExplainAsync` returns non-empty string; 404 when draft not found; 400 when `ErrorMessage` null; `HealAsync` with non-null `userContext` appends correctly (verify via `BuildHealPrompt` unit test path).

---

## Phase 12 — UiPhasePanel rewrite (frontend)

- [x] **KBG-115** — `UiPhasePanel` in `qaLoop.tsx`: full state-machine render for all `ApiUiStatus` values (`AnalyzingUi`, `AwaitingUiAnalysisReview`, `Authoring`, `AwaitingReview`, `Drafting`, `Drafted`, `RunningUiScripts`, `Pushing`, `Done`); UI Analysis card renders `session.uiAnalysisJson`; refine box + "Rerun UI Analysis" button; "Author Cases" button (approval action); `UiTestSuiteId` picker (same `testPlanId`, always visible, calls `patchUiSuite`); test cases cards with bucket chips; scripts per-case status; "Run Scripts" button; `ExplainFailureCard` sub-component (explanation text + Heal input + Heal button + "Draft Bug" button only when `adoTestCaseId != null`). **AC:** F; `npm run build` + `npm run lint` clean; `AnalyzingUi` → spinner; `AwaitingUiAnalysisReview` → analysis card + refine + Author Cases; `Drafted` → scripts + Run Scripts; `ExplainFailureCard` renders explanation; "Draft Bug" hidden when `adoTestCaseId` null.

---

## Phase 13 — Tests + docs close-out

- [x] **KBG-116** — Full test suite: `FrsKbServiceTests` (slug resolution, YAML parse, empty KB, RULES extraction, defensive non-FRS skip, dedup latest); `QaLoopCodeScanServiceTests` (atlas seeds, discovery fallback, caps, ADO failure → Warn); `QaLoopOrchestratorTests` additions (`RunUiScriptsAsync` JUnit→draft mapping, `RerunUiAnalysisAsync` replacement, backward-compat `AuthorUiCasesAsync`); `QaLoopFailureAnalystAgentTests` (non-empty output, graceful null screenshot); full B/T/F sweep. Feature doc `docs/features/qa-loop-frs-grounded.md` + `docs/changelog/` entry. All tasks `[x]` in this PRD. **AC:** all green; docs written; verification checklist from `solution.md §Verification` passed (ADO-touching steps recorded as pending if PAT expired per `ralph-loop-ops.mdc`). **KBG COMPLETE.**
