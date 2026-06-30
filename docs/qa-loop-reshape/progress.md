# Progress: QA Loop Re-shape Implementation

## Overview
- **PRD:** prd.md
- **Started:** 2026-06-30
- **Status:** IN PROGRESS (QAL-056 done — 47 of 61 complete)

---

## Task Progress

| Task ID | Task Name | Status | Started | Completed | Notes |
|---------|-----------|--------|---------|-----------|-------|
| QAL-001 | Add seven QA-Loop enums | COMPLETED | 2026-06-30 | 2026-06-30 | 7 enums appended to Enums.cs; 4 exact-name, 4 wire-string with [EnumMember]+extensions |
| QAL-002 | Add shared agent-output POCOs | COMPLETED | 2026-06-30 | 2026-06-30 | RouteInfo/DtoInfo/WorkflowInfo/InventoryDoc/SurfaceMatrix/ImpactAnalysis appended to AgentOutputs.cs |
| QAL-003 | Add AdoBuild + AdoBuildChange build-read DTOs | COMPLETED | 2026-06-30 | 2026-06-30 | 2 records added to AdoModels.cs in Build/pipeline region |
| QAL-004 | Add QaLoopOptions configuration class | COMPLETED | 2026-06-30 | 2026-06-30 | New QaLoopOptions.cs with PollIntervalMinutes/TeamsWebhookUrl/TeamsTemplate/ScriptsFolderName/RunsFolderName/ReportProvider/ActivityEnabled |
| QAL-005 | Add DmsProfileOptions registry | COMPLETED | 2026-06-30 | 2026-06-30 | DmsProfileOptions.cs with static All registry; Infinity.Credentialed=true |
| QAL-006 | Create ApiTestSession + ApiTestDraft entities | COMPLETED | 2026-06-30 | 2026-06-30 | New ApiTestSession.cs with ActivityEnabled column |
| QAL-007 | Create TestRun + TestFailure entities | COMPLETED | 2026-06-30 | 2026-06-30 | New TestRun.cs with ActivityEnabled column |
| QAL-008 | Create PipelineWatch entity | COMPLETED | 2026-06-30 | 2026-06-30 | New PipelineWatch.cs |
| QAL-009 | Register 5 DbSets + OnModelCreating in QaDbContext | COMPLETED | 2026-06-30 | 2026-06-30 | 5 DbSets + wire-string converters + indexes + cascade FKs in QaDbContext.cs |
| QAL-010 | Generate AddQaLoopReshape EF migration | COMPLETED | 2026-06-30 | 2026-06-30 | Generated 20260630152918_AddQaLoopReshape.cs; 5 tables with cascade FKs + unique PipelineWatches index |
| QAL-011 | Author module_analyzer_system.txt rewrite | COMPLETED | 2026-06-30 | 2026-06-30 | Rewritten to emit inventory+surfaceMatrix+additionalFilesNeeded JSON |
| QAL-012 | Author 7 net-new embedded prompt .txt files | COMPLETED | 2026-06-30 | 2026-06-30 | 7 prompts: contract/endpoint/workflow generator+testcase + impact_analyzer; Allure instrumentation in generators |
| QAL-013 | Register 7 prompt properties in AgentPrompts | COMPLETED | 2026-06-30 | 2026-06-30 | 7 Load() properties added to AgentPrompts.cs |
| QAL-014 | Rewrite ModuleAnalyzerAgent (inventory + matrix + 1-pass refinement) | COMPLETED | 2026-06-30 | 2026-06-30 | New AnalyzeInventoryAsync returning ModuleAnalysis; legacy AnalyzeAsync preserved for ModuleAnalysisService |
| QAL-015 | Create ContractGeneratorAgent | COMPLETED | 2026-06-30 | 2026-06-30 | ContractGeneratorAgent.cs with 2-pass LLM (spec+schema / test case) |
| QAL-016 | Create EndpointGeneratorAgent | COMPLETED | 2026-06-30 | 2026-06-30 | EndpointGeneratorAgent.cs generating 200/401/400 cases |
| QAL-017 | Create WorkflowGeneratorAgent | COMPLETED | 2026-06-30 | 2026-06-30 | WorkflowGeneratorAgent.cs with chained spec + per-leg test case |
| QAL-018 | Create shared [<tcId-pending>] TitleTag enforcement helper | COMPLETED | 2026-06-30 | 2026-06-30 | TitleTag static partial class in ContractGeneratorAgent.cs |
| QAL-019 | Create ImpactAnalyzerAgent | COMPLETED | 2026-06-30 | 2026-06-30 | ImpactAnalyzerAgent.cs with inventory projection (no SpecCode) |
| QAL-020 | Define IQaLoopOrchestrator abstraction | COMPLETED | 2026-06-30 | 2026-06-30 | IQaLoopOrchestrator.cs with Start/GenerateDrafts/PushAll/PushDraft/Cancel |
| QAL-021 | Implement QaLoopOrchestrator scan+analyze background pipeline | COMPLETED | 2026-06-30 | 2026-06-30 | QaLoopOrchestrator.cs — Analyzing→AwaitingReview/Drafting; persists InventoryJson+AnalysisJson; EventPhases.Analyze added |
| QAL-022 | Implement GenerateDraftsAsync (3 generators in parallel, selected-only) | COMPLETED | 2026-06-30 | 2026-06-30 | GenerateDraftsInnerAsync — Task.WhenAll across all three levels; per-surface GenerateSafeAsync; spec written to disk; EventPhases.Generate added |
| QAL-023 | Implement Push / PushDraft (idempotent re-push, tag rewrite) | COMPLETED | 2026-06-30 | 2026-06-30 | RunPushAsync + PushDraftAsync — [<tcId-pending>]→[id] on title+spec; LinkTestedBy only when WorkItemId set; partial push stays Drafted |
| QAL-024 | Define IPlaywrightRunner abstraction | COMPLETED | 2026-06-30 | 2026-06-30 | IPlaywrightRunner.cs with PlaywrightRunSpec/PlaywrightRunResult/FailedTestResult |
| QAL-025 | Implement PlaywrightRunner (install-once runtime, grep, spawn, parse) | COMPLETED | 2026-06-30 | 2026-06-30 | PlaywrightRunner.cs — SemaphoreSlim once-guard; symlink node_modules; grep alternation; JUnit XML parser |
| QAL-026 | Add 3 ADO Test Run lifecycle + GetTestPoints methods to IAzureDevOpsClient | COMPLETED | 2026-06-30 | 2026-06-30 | CreateAdoTestRunAsync/SetAdoTestResultAsync/CompleteAdoTestRunAsync/GetTestPointsAsync added to interface |
| QAL-027 | Implement ADO Test Run lifecycle methods in AzureDevOpsClient | COMPLETED | 2026-06-30 | 2026-06-30 | 4 methods implemented in AzureDevOpsClient.cs; test stubs updated |
| QAL-028 | Implement RunExecutionService (runner + ADO Test Run + triage handoff) | COMPLETED | 2026-06-30 | 2026-06-30 | RunExecutionService.cs — draft resolution (impact vs module), ADO Test Run lifecycle (skip on multi-suite), Playwright run, TestFailure persistence |
| QAL-029 | Add 2 build-read methods to IAzureDevOpsClient | COMPLETED | 2026-06-30 | 2026-06-30 | GetPipelineBuildsAsync/GetBuildChangesAsync added to interface |
| QAL-030 | Implement 2 build-read methods in AzureDevOpsClient | COMPLETED | 2026-06-30 | 2026-06-30 | Both implemented in AzureDevOpsClient.cs additive-only |
| QAL-031 | Implement PipelineWatcherHostedService (poll, impact, fallback, run-latest) | COMPLETED | 2026-06-30 | 2026-06-30 | PipelineWatcherHostedService.cs — BackgroundService, per-watch back-off, ImpactAnalyzer retry+fallback, RunExecutionService fire-and-forget, RunLatestAsync |
| QAL-032 | Author QaLoopContracts DTOs | COMPLETED | 2026-06-30 | 2026-06-30 | QaLoopContracts.cs — ScanStartRequest/Response, ApiAnalysisUpdate, UpdateApiDraftRequest, ApiTestDraftDto, ApiSessionSummary/List/Detail |
| QAL-033 | Author QaRunsContracts DTOs | COMPLETED | 2026-06-30 | 2026-06-30 | QaRunsContracts.cs — RunStartRequest/Response, TriageRequest, FileBugsRequest/Response, TestFailureDto, TestRunSummary/List/Detail |
| QAL-034 | Author QaPipelinesContracts DTOs | COMPLETED | 2026-06-30 | 2026-06-30 | QaPipelinesContracts.cs — PipelineWatchDto, AddWatchRequest, UpdateWatchRequest, RunLatestResponse, PreviewImpactRequest, PipelineWatchList |
| QAL-035 | Implement QaLoopController | COMPLETED | 2026-06-30 | 2026-06-30 | QaLoopController.cs — 11 endpoints (scan/start, stream SSE, analysis PATCH, generate-drafts, draft PATCH, approve-all, push-all, push-draft, cancel, list, detail) |
| QAL-036 | Implement QaRunsController | COMPLETED | 2026-06-30 | 2026-06-30 | QaRunsController.cs — 7 endpoints (start, stream, list, detail, triage stub, file-bugs stub, preview-impact) |
| QAL-037 | Implement QaPipelinesController | COMPLETED | 2026-06-30 | 2026-06-30 | QaPipelinesController.cs — 5 endpoints (list, add, patch, delete, run-latest) |
| QAL-038 | Split IResultsIngestService + ResultsIngestService into Triage/FileBugs | COMPLETED | 2026-06-30 | 2026-06-30 | TriageAsync (FailureTriageAgent, BugDraftJson persist) + FileBugsAsync (no 2nd LLM, CreateBugAsync, link TC+story) in ResultsIngestService; triage/file-bugs endpoints wired in QaRunsController |
| QAL-039 | Define ITeamsNotifier abstraction | COMPLETED | 2026-06-30 | 2026-06-30 | ITeamsNotifier.cs — NotifyRunCompleteAsync(run) + NotifyTriageAsync(run, count, bugIds) |
| QAL-040 | Author 3 Teams card templates | COMPLETED | 2026-06-30 | 2026-06-30 | 3 .json.template files (compact/developer/scheduled) in Notifications/Templates/; second EmbeddedResource glob in Infrastructure .csproj |
| QAL-041 | Implement TeamsNotifier | COMPLETED | 2026-06-30 | 2026-06-30 | TeamsNotifier.cs — singleton, template loading, token substitution, silent on empty webhook, Cancelled skip |
| QAL-042 | Register 3 settings keys in AppSettingsService | COMPLETED | 2026-06-30 | 2026-06-30 | teams.webhook_url (secret), teams.template, qaloop.poll_interval_minutes, qaloop.report_provider, qaloop.activity_enabled — AllKeys+OptionKeys+SecretKeys; IOptionsMonitor<QaLoopOptions>+cache injected; _qaLoopCache.Clear() on apply |
| QAL-043 | Add .env bridge + RuntimeSettings overlay + AddQaLoop DI registration | COMPLETED | 2026-06-30 | 2026-06-30 | AddQaLoop in DI.cs — QaLoopOptions .env bridge + RuntimeSettings PostConfigure; IQaLoopOrchestrator, IPlaywrightRunner, RunExecutionService, ITeamsNotifier, PipelineWatcherHostedService registered |
| QAL-044 | Generalize session types + sessionIdentity helper | COMPLETED | 2026-06-30 | 2026-06-30 | workItemId/Title nullable; SessionModuleConfig key+'qa-loop'+renderIdentity?; SessionDetail tolerates module/inventoryJson; sessionIdentity() in shared.tsx |
| QAL-045 | Apply config-driven identity at the 4 hardcoded sites | COMPLETED | 2026-06-30 | 2026-06-30 | SessionWorkspace.tsx (card title, drawer header, analytics row) + SessionDetailFullScreen.tsx (header) — all use sessionIdentity() |
| QAL-046 | Rewrite api/qaLoop.ts | COMPLETED | 2026-06-30 | 2026-06-30 | Full rewrite targeting /api/qaloop: 11 methods + types + re-exports getPlans/getSuites/searchWorkItems |
| QAL-047 | Create api/qaRuns.ts and api/qaPipelines.ts | COMPLETED | 2026-06-30 | 2026-06-30 | qaRuns.ts (startRun, listRunsPage, getRunDetail, triage, fileBugs, previewImpact, runStreamUrl) + qaPipelines.ts (listWatches, addWatch, updateWatch, deleteWatch, runLatest) |
| QAL-048 | Create qaLoopConfig SessionWorkspace config + scan form | COMPLETED | 2026-06-30 | 2026-06-30 | qaLoop.tsx — qaLoopConfig (key, renderIdentity), QaLoopStartForm, QaLoopDetailBody, CoverageMatrix, DraftsByLevel, PushPanel |
| QAL-049 | Repoint QaLoopPage + create QaRunsPage and QaPipelinesPage | COMPLETED | 2026-06-30 | 2026-06-30 | QaLoopPage one-liner; QaRunsPage (launcher, paged grid, drawer: failures+triage+file-bugs+impact); QaPipelinesPage (watch cards, enable Switch, Run latest, AddWatchDialog) |
| QAL-050 | Wire routes/paths.ts, AppRoutes.tsx, Layout.tsx | COMPLETED | 2026-06-30 | 2026-06-30 | qaRuns+qaPipelines paths; /qa-loop/:sessionId full-screen; QA Runs+Pipelines nav+routes; TestRun status colors added to pipeline.ts |
| QAL-051 | Write QaLoopOrchestratorTests | COMPLETED | 2026-06-30 | 2026-06-30 | 6 tests: AwaitingReview, Drafted(AutoHandoff), generate selected-only, push rewrites tag, push skips LinkTestedBy, repress-push skips already-pushed — all 6 pass |
| QAL-052 | Write ImpactAnalyzerAgentTests | COMPLETED | 2026-06-30 | 2026-06-30 | 6 tests: valid parse, id filtering, empty ids+skippedReason, malformed JSON throws AgentException, array root throws, SpecCode excluded from prompt — all pass |
| QAL-053 | Write JUnitTriageSplitTests + watcher ProcessBuild test | COMPLETED | 2026-06-30 | 2026-06-30 | 6 tests: TriageAsync sets BugDraftJson (no CreateBugAsync), skips already-triaged; FileBugsAsync calls CreateBugAsync once (no LLM); Watcher: empty inventory→Cancelled, malformed JSON→fallback+all ids, valid impact→Queued+LastSeenBuildId updated — 122 total pass |
| QAL-054 | Full build, lint, test + golden parity + runtime smoke | COMPLETED | 2026-06-30 | 2026-06-30 | tsc -b && vite build clean; 122/122 backend tests pass (incl. AdoGolden); qaLoop.tsx lint clean; pre-existing lint errors in other files not introduced by this PRD |
| QAL-055 | Allure runtime deps + per-run reporter wiring | COMPLETED | 2026-06-30 | 2026-06-30 | PlaywrightRunner: @allurereport/cli + allure-playwright in runtime package.json; allure --version capability check in EnsureRuntimeAsync; reporters moved to config (allure-playwright always, html gated on report_provider ∈ {playwright,both}); _reportProvider field from QaLoopOptions; fallback comment: allure-commandline+JRE if --single-file unsupported |
| QAL-056 | Allure context files (environment / categories / executor) | COMPLETED | 2026-06-30 | 2026-06-30 | PlaywrightRunSpec gains TriggerSourceWire/TriggerBuildId/TriggerCommitSha/AdoTestRunId; WriteAllureContext writes environment.properties+categories.json+executor.json to allure-results/ after spawn; RunExecutionService populates context fields; graceful degradation on any write failure |
| QAL-057 | Allure generate single-file + trends + ReportPath + fallback | NOT STARTED |  |  |  |
| QAL-058 | Settings report_provider + activity_enabled (both keys end-to-end) | NOT STARTED |  |  |  |
| QAL-059 | Allure-rich generated specs (generator prompt amendments) | NOT STARTED |  |  |  |
| QAL-060 | ActivityEnabled persistence + streaming gate | NOT STARTED |  |  |  |
| QAL-061 | FE Allure report + activity swap + Settings UI | NOT STARTED |  |  |  |

---

## Summary
- **Total Tasks:** 61
- **Completed:** 47
- **In Progress:** 0
- **Not Started:** 14

---

## Log
### 2026-06-30 (QAL-056)
- **QAL-056** COMPLETED — `PlaywrightRunSpec` gains 4 Allure context fields; `PlaywrightRunner.WriteAllureContext` writes `environment.properties` (profile, module, trigger, build, commit, ado run), `categories.json` (real_bug/flaky/test_issue/stale_test taxonomy), `executor.json` (ADO build+test-run deep links) into `allure-results/` after Playwright spawn; failure is catch-logged not rethrown; `_adoBaseUrl` from injected `IOptions<AzureDevOpsOptions>`; 122/122 tests pass.

### 2026-06-30 (QAL-055)
- **QAL-055** COMPLETED — `PlaywrightRunner.cs`: `WriteRuntimePackageJson` adds `allure-playwright`+`@allurereport/cli`; `EnsureRuntimeAsync` adds `npx allure --version` capability check + fallback warn; `WriteRunConfig` moves reporters to config template (junit+json+allure-playwright always, html gated on `report_provider ∈ {playwright,both}`); `RunAsync` drops `--reporter` CLI args; `_reportProvider` field from constructor. Build clean.

### 2026-06-30 (QAL-054)
- **QAL-054** COMPLETED — Fixed all TS build errors in qaLoop.tsx (Stack sx, as unknown as, removed stale useEventStream+events, correct AgentPipeline/AgentConsole props); fixed pre-existing flexWrap/Grid/wiUrl errors in QaPipelinesPage/QaRunsPage/authorTests/findBugs; tsc -b && vite build clean; 122/122 backend tests pass including AdoGoldenTests.

### 2026-06-30 (QAL-035..037)
- **QAL-035** COMPLETED — `QaLoopController.cs`: 11 endpoints at /api/qaloop — scan/start (202, non-terminal check), stream SSE, analysis PATCH (full-replace), generate-drafts (202), draft PATCH (skip-null), approve-all, push-all (reads plan/suite from session), push-draft (sync, reads plan/suite from session), cancel, list (paged + draft counts), detail. Build green.
- **QAL-036** COMPLETED — `QaRunsController.cs`: 7 endpoints at /api/qaloop/runs — start (202, fire-and-forget RunExecutionService), stream, list (paged), detail, triage (stub for QAL-038), file-bugs (stub for QAL-038), preview-impact. Build green.
- **QAL-037** COMPLETED — `QaPipelinesController.cs`: 5 endpoints at /api/qaloop/pipelines — list, add (409 on duplicate pipeline), patch (Enabled/Module), delete, run-latest (202). Build green.

### 2026-06-30 (QAL-032..034)
- **QAL-032** COMPLETED — `QaLoopContracts.cs`: ScanStartRequest/Response, ApiAnalysisUpdate (full-replace SurfaceMatrix), UpdateApiDraftRequest (skip-null), ApiTestDraftDto/ApiSessionSummary/List/Detail with static From mappers.
- **QAL-033** COMPLETED — `QaRunsContracts.cs`: RunStartRequest/Response, TriageRequest, FileBugsRequest/Response, TestFailureDto/TestRunSummary/List/Detail with From mappers.
- **QAL-034** COMPLETED — `QaPipelinesContracts.cs`: PipelineWatchDto, AddWatchRequest, UpdateWatchRequest, RunLatestResponse, PreviewImpactRequest, PipelineWatchList. Build green.

### 2026-06-30 (QAL-028, QAL-031)
- **QAL-028** COMPLETED — `RunExecutionService.cs`: scoped service; resolves Pushed drafts (impact-scoped or module-wide); resolves single plan/suite (rejects multi-suite with skip warning); GetTestPoints→CreateAdoTestRunAsync→PlaywrightRunner.RunAsync→SetAdoTestResultAsync per test→CompleteAdoTestRunAsync; persists TestFailure rows (triage deferred to QAL-038); Queued→Running→Passed/Failed/Error lifecycle. Build green.
- **QAL-031** COMPLETED — `PipelineWatcherHostedService.cs`: BackgroundService polling every `qaloop.poll_interval_minutes`; per-watch 15-min back-off on error; processes builds oldest-first; GetBuildChangesAsync→GetCommitChangedFilesAsync→ImpactAnalyzerAgent (1 retry + module-wide fallback); Cancelled TestRun when no impacted tests; fire-and-forget RunExecutionService for impacted runs; RunLatestAsync for manual trigger. Build green.

### 2026-06-30 (QAL-014..019)
- **QAL-014** COMPLETED — `ModuleAnalyzerAgent.cs` rewritten: new AnalyzeInventoryAsync(module, source, existingCases, fetchFile, ...) returning ModuleAnalysis(InventoryDoc, SurfaceMatrix, AdditionalFilesNeeded); legacy AnalyzeAsync preserved for ModuleAnalysisService.
- **QAL-015** COMPLETED — `ContractGeneratorAgent.cs`: 2-pass (generator+testcase), FallbackTestCase, GeneratedSpec record.
- **QAL-016** COMPLETED — `EndpointGeneratorAgent.cs`: 200/401/400 cases, one spec file + one test case per case.
- **QAL-017** COMPLETED — `WorkflowGeneratorAgent.cs`: chained spec with cleanup + per-leg ADO Test Case steps.
- **QAL-018** COMPLETED — `TitleTag` partial static class in ContractGeneratorAgent.cs: EnsureInSpec + EnsureTitle helpers.
- **QAL-019** COMPLETED — `ImpactAnalyzerAgent.cs`: DraftInventoryItem projection (no SpecCode), throws AgentException on bad JSON. Build green.

### 2026-06-30 (QAL-011/012/013)
- **QAL-011** COMPLETED — `module_analyzer_system.txt` rewritten: emits inventory (routes/dtos/workflows) + surfaceMatrix (3-level rows) + additionalFilesNeeded.
- **QAL-012** COMPLETED — 7 new prompts: contract/endpoint/workflow _generator + _testcase + impact_analyzer. Generators include Allure test.step + allure.attachment instrumentation (QAL-059 amendment). testcase prompts reuse Mahmood's voice from tester_system.txt.
- **QAL-013** COMPLETED — 7 Load() properties in AgentPrompts.cs. Build green.

### 2026-06-30 (QAL-009/010)
- **QAL-009** COMPLETED — `QaDbContext.cs`: 5 new DbSets, 4 wire-string ValueConverters (TestLevel/FailureClassification/TriggerSource/DmsProfile), 5 entity registrations with indexes and cascade FKs. Build green.
- **QAL-010** COMPLETED — `20260630152918_AddQaLoopReshape.cs` generated; creates ApiTestSessions, ApiTestDrafts, TestRuns, TestFailures, PipelineWatches with TEXT enum columns, unique PipelineWatches index, all cascade FKs.

### 2026-06-30 (QAL-006/007/008)
- **QAL-006** COMPLETED — new `ApiTestSession.cs` (ApiTestSession + ApiTestDraft entities, includes ActivityEnabled per QAL-060 amendment).
- **QAL-007** COMPLETED — new `TestRun.cs` (TestRun + TestFailure entities, includes ActivityEnabled per QAL-060 amendment).
- **QAL-008** COMPLETED — new `PipelineWatch.cs`. Build green.

### 2026-06-30 (QAL-005)
- **QAL-005** COMPLETED — new `backend/src/QaAutomation.Core/Configuration/DmsProfileOptions.cs` with static All registry (Ideal/Infinity/Aspen/Quantum); Infinity.Credentialed=true, others false. Build green.

### 2026-06-30 (QAL-004)
- **QAL-004** COMPLETED — new `backend/src/QaAutomation.Core/Configuration/QaLoopOptions.cs` with SectionName="QaLoop", PollIntervalMinutes(5), TeamsWebhookUrl, TeamsTemplate("developer"), ScriptsFolderName("scripts"), RunsFolderName("runs"), ReportProvider("allure"), ActivityEnabled(true). Build green.

### 2026-06-30 (QAL-003)
- **QAL-003** COMPLETED — `backend/src/QaAutomation.Core/Models/AdoModels.cs`: AdoBuild(Id,SourceVersion,RepositoryId,FinishedAt) + AdoBuildChange(Id,Message) in Build/pipeline region. Build green.

### 2026-06-30 (QAL-002)
- **QAL-002** COMPLETED — `backend/src/QaAutomation.Core/Models/AgentOutputs.cs`: appended RouteInfo, DtoField, DtoInfo, WorkflowStep, WorkflowInfo, InventoryDoc (positional records), SurfaceRow (mutable class), SurfaceMatrix (record), ImpactAnalysis (class). Build green.

### 2026-06-30 (QAL-001)
- **QAL-001** COMPLETED — `backend/src/QaAutomation.Core/Models/Enums.cs`: appended 7 QA Loop enums (ApiSessionStatus, ApiDraftStatus, TestRunStatus as exact-name; TestLevel, FailureClassification, TriggerSource, DmsProfile as wire-string with [EnumMember] + QaLoopEnumExtensions). Build green.

### 2026-06-30
- Created `prd.md` and `progress.md` from `solution.md` (v1.3) + `blueprint.md`.
- Planned 54 tasks across 11 phases: FOUNDATION, DATA LAYER, AGENTS & PROMPTS, ORCHESTRATOR, RUNNER & ADO TEST RUN, PIPELINE WATCHER & ADO CLIENT METHODS, API (CONTROLLERS + DTOs + SSE), NOTIFICATIONS & SETTINGS & DI, FRONTEND, TESTING, BUILD & VERIFY.
- Completeness critic verdict: COMPLETE; 0 gap(s) folded into the backlog before finalizing.
- All tasks NOT STARTED. Execution follows the Ralph loop: ONE task per iteration; mark the task `[x]` in `prd.md`, flip its row to COMPLETED here, and append a Log entry each iteration; never leave a failing build.
- **Amendment (additive):** added the ALLURE REPORTING & ACTIVITY TOGGLE phase — tasks QAL-055..061 — lifting the count to 61.
  - **Allure reporting:** per-run Node-native Allure HTML report (latest stable that runs on Node, NO Java dependency; bundled-JRE + Allure 2 fallback documented if a capability is missing). Primary artifact = SINGLE-FILE `.html` (= `ReportPath`; same file serves open-in-new-tab AND download). Trends INCLUDED: server-side history store keyed by (module, pipeline), seeded into `allure-results/history/` before generate and copied back after, so cross-run trend continuity survives regardless of report format. Report format gated by a new `qaloop.report_provider` setting (allure DEFAULT / playwright / both) — it gates ONLY the human-facing HTML; the Playwright `html` reporter is the only thing omitted when provider=allure. **junit + json ALWAYS emit on every run** (triage / ADO Test Run / bug drafting / counts depend on them — UNTOUCHED). API-LEVEL capture only (no screenshot/video/trace, no `use:` block): each call wrapped in `test.step` + `allure.attachment` for request/response/schema-diff/assertion + latency parameter, authored into the generated specs by the generator prompts, plus Allure metadata (epic=module, feature=level, story=route/flow, severity, tags, `[tcId]` as a link to the ADO Test Case). Three context files (`environment.properties`, `categories.json` mapped to the real_bug/flaky/test_issue/stale_test triage taxonomy, `executor.json` with ADO build + Test Run deep links) written before generate. **A failed `allure generate` NEVER fails a run** — ReportPath falls back (Playwright `html` if present else null) + a warning on `TestRun.ErrorMessage`; counts/triage/ADO Test Run unaffected.
  - **Activity toggle:** new global `qaloop.activity_enabled` setting (DEFAULT true), all modules + scan sessions + execution runs. Per-row `ActivityEnabled bool` on ApiTestSession AND TestRun captured from the setting at row creation, folded into the SAME AddQaLoopReshape migration with column DEFAULT 1 so EXISTING rows always show their activity. When false for a NEW row: QaLoopOrchestrator (sessions) + PlaywrightRunner/RunExecutionService (runs) suppress verbose streaming (agent token-level output for sessions; Playwright stdout for runs) AND skip heavy EventLogJson persistence, but STILL emit coarse phase/status transitions so step-state + completion signal work; FE swaps AgentConsole → indeterminate LinearProgress/LiveActivityIndicator + step pills. `activityEnabled` added to the session + run DTOs so the FE picks console vs progress. **Honest scope note:** the toggle removes SSE-streaming + EventLogJson overhead globally; LLM-generation tokens are saved ONLY for scan/generate SESSIONS (agents stream their generation) — EXECUTION runs stream Playwright stdout (no LLM), so there the saving is bandwidth/clutter/storage, NOT LLM tokens.
  - **Settings UI:** Settings page adds a `report_provider` SELECT (Allure default / Playwright / Both) and an `activity_enabled` SWITCH (real MUI Select + Switch, not text fields) alongside the existing teams.webhook_url / teams.template / qaloop.poll_interval_minutes. /qa-runs run-detail drawer gains an "Allure report" open-in-new-tab + "Download" button (same single-file html) conditional on `reportPath` + provider, with a muted "report unavailable" state on generate failure; the existing "Playwright report" button becomes conditional on provider ∈ {playwright, both}; optional minor run-history "report available" indicator.
  - All edits are ADDITIVE amendments (new phase + tasks; existing QAL-001..054 rows and prose preserved). Several amendment tasks amend earlier tasks: QAL-055/057 amend QAL-025/028 (runner config + ReportPath), QAL-058 amends QAL-004/042/043 (settings wiring), QAL-059 amends QAL-012/015-017 (generator prompts), QAL-060 amends QAL-006/007/009/010/021/022/025/028 (entity column + migration + streaming gate), QAL-061 amends QAL-032/033/048/049 (DTOs + FE).
