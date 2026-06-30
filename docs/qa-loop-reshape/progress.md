# Progress: QA Loop Re-shape Implementation

## Overview
- **PRD:** prd.md
- **Started:** 2026-06-30
- **Status:** NOT STARTED

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
| QAL-024 | Define IPlaywrightRunner abstraction | NOT STARTED |  |  |  |
| QAL-025 | Implement PlaywrightRunner (install-once runtime, grep, spawn, parse) | NOT STARTED |  |  |  |
| QAL-026 | Add 3 ADO Test Run lifecycle + GetTestPoints methods to IAzureDevOpsClient | NOT STARTED |  |  |  |
| QAL-027 | Implement ADO Test Run lifecycle methods in AzureDevOpsClient | NOT STARTED |  |  |  |
| QAL-028 | Implement RunExecutionService (runner + ADO Test Run + triage handoff) | NOT STARTED |  |  |  |
| QAL-029 | Add 2 build-read methods to IAzureDevOpsClient | NOT STARTED |  |  |  |
| QAL-030 | Implement 2 build-read methods in AzureDevOpsClient | NOT STARTED |  |  |  |
| QAL-031 | Implement PipelineWatcherHostedService (poll, impact, fallback, run-latest) | NOT STARTED |  |  |  |
| QAL-032 | Author QaLoopContracts DTOs | NOT STARTED |  |  |  |
| QAL-033 | Author QaRunsContracts DTOs | NOT STARTED |  |  |  |
| QAL-034 | Author QaPipelinesContracts DTOs | NOT STARTED |  |  |  |
| QAL-035 | Implement QaLoopController | NOT STARTED |  |  |  |
| QAL-036 | Implement QaRunsController | NOT STARTED |  |  |  |
| QAL-037 | Implement QaPipelinesController | NOT STARTED |  |  |  |
| QAL-038 | Split IResultsIngestService + ResultsIngestService into Triage/FileBugs | NOT STARTED |  |  |  |
| QAL-039 | Define ITeamsNotifier abstraction | NOT STARTED |  |  |  |
| QAL-040 | Author 3 Teams card templates | NOT STARTED |  |  |  |
| QAL-041 | Implement TeamsNotifier | NOT STARTED |  |  |  |
| QAL-042 | Register 3 settings keys in AppSettingsService | NOT STARTED |  |  |  |
| QAL-043 | Add .env bridge + RuntimeSettings overlay + AddQaLoop DI registration | NOT STARTED |  |  |  |
| QAL-044 | Generalize session types + sessionIdentity helper | NOT STARTED |  |  |  |
| QAL-045 | Apply config-driven identity at the 4 hardcoded sites | NOT STARTED |  |  |  |
| QAL-046 | Rewrite api/qaLoop.ts | NOT STARTED |  |  |  |
| QAL-047 | Create api/qaRuns.ts and api/qaPipelines.ts | NOT STARTED |  |  |  |
| QAL-048 | Create qaLoopConfig SessionWorkspace config + scan form | NOT STARTED |  |  |  |
| QAL-049 | Repoint QaLoopPage + create QaRunsPage and QaPipelinesPage | NOT STARTED |  |  |  |
| QAL-050 | Wire routes/paths.ts, AppRoutes.tsx, Layout.tsx | NOT STARTED |  |  |  |
| QAL-051 | Write QaLoopOrchestratorTests | NOT STARTED |  |  |  |
| QAL-052 | Write ImpactAnalyzerAgentTests | NOT STARTED |  |  |  |
| QAL-053 | Write JUnitTriageSplitTests + watcher ProcessBuild test | NOT STARTED |  |  |  |
| QAL-054 | Full build, lint, test + golden parity + runtime smoke | NOT STARTED |  |  |  |
| QAL-055 | Allure runtime deps + per-run reporter wiring | NOT STARTED |  |  |  |
| QAL-056 | Allure context files (environment / categories / executor) | NOT STARTED |  |  |  |
| QAL-057 | Allure generate single-file + trends + ReportPath + fallback | NOT STARTED |  |  |  |
| QAL-058 | Settings report_provider + activity_enabled (both keys end-to-end) | NOT STARTED |  |  |  |
| QAL-059 | Allure-rich generated specs (generator prompt amendments) | NOT STARTED |  |  |  |
| QAL-060 | ActivityEnabled persistence + streaming gate | NOT STARTED |  |  |  |
| QAL-061 | FE Allure report + activity swap + Settings UI | NOT STARTED |  |  |  |

---

## Summary
- **Total Tasks:** 61
- **Completed:** 19
- **In Progress:** 0
- **Not Started:** 42

---

## Log
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
