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
| QAL-004 | Add QaLoopOptions configuration class | NOT STARTED |  |  |  |
| QAL-005 | Add DmsProfileOptions registry | NOT STARTED |  |  |  |
| QAL-006 | Create ApiTestSession + ApiTestDraft entities | NOT STARTED |  |  |  |
| QAL-007 | Create TestRun + TestFailure entities | NOT STARTED |  |  |  |
| QAL-008 | Create PipelineWatch entity | NOT STARTED |  |  |  |
| QAL-009 | Register 5 DbSets + OnModelCreating in QaDbContext | NOT STARTED |  |  |  |
| QAL-010 | Generate AddQaLoopReshape EF migration | NOT STARTED |  |  |  |
| QAL-011 | Author module_analyzer_system.txt rewrite | NOT STARTED |  |  |  |
| QAL-012 | Author 7 net-new embedded prompt .txt files | NOT STARTED |  |  |  |
| QAL-013 | Register 7 prompt properties in AgentPrompts | NOT STARTED |  |  |  |
| QAL-014 | Rewrite ModuleAnalyzerAgent (inventory + matrix + 1-pass refinement) | NOT STARTED |  |  |  |
| QAL-015 | Create ContractGeneratorAgent | NOT STARTED |  |  |  |
| QAL-016 | Create EndpointGeneratorAgent | NOT STARTED |  |  |  |
| QAL-017 | Create WorkflowGeneratorAgent | NOT STARTED |  |  |  |
| QAL-018 | Create shared [<tcId-pending>] TitleTag enforcement helper | NOT STARTED |  |  |  |
| QAL-019 | Create ImpactAnalyzerAgent | NOT STARTED |  |  |  |
| QAL-020 | Define IQaLoopOrchestrator abstraction | NOT STARTED |  |  |  |
| QAL-021 | Implement QaLoopOrchestrator scan+analyze background pipeline | NOT STARTED |  |  |  |
| QAL-022 | Implement GenerateDraftsAsync (3 generators in parallel, selected-only) | NOT STARTED |  |  |  |
| QAL-023 | Implement Push / PushDraft (idempotent re-push, tag rewrite) | NOT STARTED |  |  |  |
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
- **Completed:** 3
- **In Progress:** 0
- **Not Started:** 58

---

## Log
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
