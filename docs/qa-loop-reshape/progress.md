# Progress: QA Loop Re-shape Implementation

## Overview
- **PRD:** prd.md
- **Started:** 2026-06-30
- **Status:** BUILD COMPLETE, E2E-VERIFICATION FIX-UP IN PROGRESS (61 of 61 build tasks done; QAL-062..070 opened 2026-07-01 from live E2E findings, 0 of 9 complete)

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
| QAL-057 | Allure generate single-file + trends + ReportPath + fallback | COMPLETED | 2026-06-30 | 2026-06-30 | GenerateAllureReportAsync: seeds history, npx allure generate --single-file --clean, CopyHistoryBack, finds *.html in allure-report/; fallback=html/index.html or null; provider=playwright skips generate; RunExecutionService sets run.ReportPath; history keyed by (module,pipeline) under allure-history/ |
| QAL-058 | Settings report_provider + activity_enabled (both keys end-to-end) | COMPLETED | 2026-06-30 | 2026-06-30 | Already fully wired in QAL-042/043: AllKeys+OptionKeys, .env bridge (QALOOP_REPORT_PROVIDER/QALOOP_ACTIVITY_ENABLED), RuntimeSettings PostConfigure overlay, OptionValue switch, _qaLoopCache.Clear(); build verified |
| QAL-059 | Allure-rich generated specs (generator prompt amendments) | COMPLETED | 2026-06-30 | 2026-06-30 | Already done in QAL-012: all three *_generator_system.txt prompts have test.step, allure.attachment(request/response/assertion), latency parameter, epic/feature/story/severity/tag/link metadata, [<tcId-pending>] preserved, no use: block; EmbeddedResource via *.txt glob in csproj |
| QAL-060 | ActivityEnabled persistence + streaming gate | COMPLETED | 2026-06-30 | 2026-06-30 | EmitClosure+PersistLog gated by session.ActivityEnabled; SpawnAsync suppressOutput gate; PlaywrightRunSpec.ActivityEnabled; RunExecutionService propagates run.ActivityEnabled |
| QAL-061 | FE Allure report + activity swap + Settings UI | COMPLETED | 2026-06-30 | 2026-06-30 | BE: GET {id}/report endpoint (serves HTML, ?dl=true for download); FE: activityEnabled on run+session DTOs; QaRunsPage drawer: Report↗/Download/unavailable + Console tab with AgentConsole or LinearProgress; qaLoop.tsx activity swap; Settings QA Loop tab with Teams/poll/report-provider/activity-switch |
| QAL-062 | Fix Playwright runtime npm/npx spawn failure on Windows | COMPLETED | 2026-07-01 | 2026-07-01 | 6 compounding layers found+fixed live (see log): npm/npx PATHEXT resolution, Which() extension-ordering, testDir level-name casing, npm ci→install (no lockfile) + exit-code checks, wrong Allure package name, node_modules junction (2 locations). Real run now shows genuine Failed/Passed counts from real Playwright execution |
| QAL-063 | Fix QaPipelinesController DI resolution crash | COMPLETED | 2026-07-01 | 2026-07-01 | PipelineWatcherHostedService now registered as singleton + exposed as IHostedService via factory; all 5 endpoints (list/add/patch/delete/run-latest) verified live via curl, no 500s |
| QAL-064 | Fix api/qaLoop.ts — every method targets the wrong URL | NOT STARTED | | | Entire `/qa-loop` UI non-functional for data (list/detail/start/analysis/generate/push/cancel all 404 live) |
| QAL-065 | Fix api/qaRuns.ts + api/qaPipelines.ts missing {projectId} segment | NOT STARTED | | | `/qa-runs` and `/qa-pipelines` list views show false-empty states |
| QAL-066 | Fix ModuleAnalyzerAgent source-fetch for nested modules | NOT STARTED | | | `SourceDirs`/`Extensions` dead fields; module-name-as-scopePath 404s for any module not at repo root (confirmed against real CRM Project repo) |
| QAL-067 | Fix multi-case Endpoint draft tag-rewrite corruption on push | NOT STARTED | | | Confirmed live by pushing real ADO Test Cases #174569-174571: shared spec file ends up with all 3 cases tagged with the FIRST case's id → silent false-pass for 2 of 3 |
| QAL-068 | Wire a real target base URL + credential for generated specs | NOT STARTED | | | Generated specs read `AUTOMATION_PROFILE` (a profile key, e.g. "infinity") as if it were a URL; no bearer/base-URL wiring exists anywhere |
| QAL-069 | Isolate the ClaudeCli child process from the host Claude Code session | NOT STARTED | | | No `WorkingDirectory`/permission isolation on the spawned `claude -p` process; confirmed live referencing the operator's own session memory mid-completion |
| QAL-070 | Populate WorkItemTitle for QA Loop sessions | NOT STARTED | | | `QaLoopOrchestrator` never fetches the linked work item's title, unlike `AgentOrchestrator`/`BugOrchestrator`; cosmetic |
| QAL-071 | Write Contract generator's schema file to disk | NOT STARTED | | | Newly discovered while verifying QAL-062 live: spec imports a sibling `.schema` file that's never written, aborting the whole Playwright run whenever a Contract-level draft is included |

---

## Summary
- **Total Tasks:** 71 (61 build + 10 E2E fix-up)
- **Completed:** 63
- **In Progress:** 0
- **Not Started:** 8 (QAL-064..071)

---

## Log
### 2026-07-01 (QAL-062, QAL-063 — Ralph loop resumed)
- **QAL-063** COMPLETED — `DependencyInjection.AddQaLoop`: `PipelineWatcherHostedService` now `services.AddSingleton<PipelineWatcherHostedService>()` + `services.AddHostedService(sp => sp.GetRequiredService<PipelineWatcherHostedService>())` (was `AddHostedService<T>()` alone, which never registers T as itself). Verified live: `GET/POST/PATCH/DELETE /api/qaloop/pipelines/*` all resolve (previously 500 on every call); added `add-watch → list → delete` round-trip via curl. Regression test added: `JUnitTriageSplitTests.PipelineWatcherHostedService_resolves_both_as_itself_and_as_the_same_IHostedService_instance` (builds a minimal ServiceCollection mirroring the real registration, asserts `GetRequiredService<PipelineWatcherHostedService>()` and the `IHostedService` resolution are the SAME instance).
- **QAL-062** COMPLETED — turned out to be 6 compounding, previously-hidden layers, each only surfacing once the prior one was fixed and a real run was actually attempted live:
  1. `PlaywrightRunner.SpawnAsync` now resolves `npm`/`npx` via `ClaudeCli.Which()` (made `internal`, reused) instead of a bare `ProcessStartInfo(exe, args)` which can't find the `.cmd` shim on Windows.
  2. `ClaudeCli.Which()` itself had a real bug: it tried the bare (no-extension) name BEFORE PATHEXT extensions, and Node.js on Windows ships both a bare POSIX shell-script `npm` (not a valid Win32 executable) and the real `npm.cmd` side by side — bare-first resolved to the unusable decoy (`Win32Exception: not a valid application for this OS platform`). Fixed the ordering (extensions first, bare name last-resort); added 2 regression tests including the exact decoy-vs-shim scenario.
  3. `PlaywrightRunner.WriteRunConfig`'s `testDir` used the caller's raw `levelFilter` string as a folder name (no case/pluralization normalization) and literally `"*"` for "all levels" (Playwright's `testDir` is not a glob) — neither ever matched the real `contracts`/`endpoints`/`workflows` folders `PersistDraft` writes to. Added `TestLevel.ToDirName()` (single source of truth, `Enums.cs`) used by both `PersistDraft` and `WriteRunConfig`; no-filter now points at the module root (Playwright recursively scans subfolders).
  4. `EnsureRuntimeAsync` ran `npm ci`, which requires a pre-existing `package-lock.json` we never write, and failed immediately — silently, since exit codes were never checked, so `_runtimeReady` got set `true` anyway with no `node_modules` ever installed. Switched to `npm install`; added exit-code checks on both the install and `playwright install` steps so a real failure now surfaces as `TestRun.Status=Error` with a real message instead of a false "Passed".
  5. `WriteRuntimePackageJson` depended on `"@allurereport/cli"`, which does not exist on the npm registry (confirmed via direct registry query: 404). The real, currently-published Node-native Allure 3 CLI package is the unscoped `"allure"` (v3.14.0) — same `npx allure ...` invocation already in the code, just the wrong dependency name.
  6. The per-run `node_modules` link (`Directory.CreateSymbolicLink`) requires elevation/Developer Mode on Windows and failed silently under a normal user account, so `npx playwright test` fell back to an unrelated global npx cache and couldn't find `@playwright/test`. Switched to an NTFS junction (`mklink /J`, no special privileges needed) via a new `LinkDirectory` helper. Generated specs also live under `_scriptsDir` (a sibling tree to the run directory, not a descendant), so Node's upward module resolution from a spec file never reached the run directory's link either — added a second, one-time junction at `_scriptsDir/node_modules` in `EnsureRuntimeAsync`.
  7. (bonus, not part of the original diagnosis) `RunAsync`'s `--output={runDir}` pointed Playwright's own output/artifacts directory at the SAME folder holding `playwright.config.ts`/`package.json`/the node_modules link — Playwright wipes its entire `outputDir` at the start of every run, deleting its own config before it could load it (0 tests found, no error surfaced). Redirected `--output` to a dedicated `runDir/test-output` subfolder.
  - **Live verification, end to end, through the real app (not manual CLI):** `POST /api/qaloop/runs/start` with `levelFilter=Endpoint` against the real Deals drafts pushed earlier → `{"status":"Failed","passed":0,"failed":3,"durationMs":8126}` with a real `junit.xml` — 3 real Playwright tests executed, launched real HTTP requests, failed with `ECONNREFUSED` (the separate, already-tracked QAL-068 issue), and were correctly reported as Failed rather than a false "Passed". A full module-wide run (all 3 levels together) still aborts at file-load time because of one unrelated new bug — filed as **QAL-071** below (a Contract-level spec imports a sibling `.schema.ts` file that's never written to disk; Playwright refuses to run ANYTHING in a batch if any one file fails to load).
  - Tests: `LlmTests` gained `Which_resolves_a_pathext_shim_via_path_lookup`, `Which_returns_null_for_an_unresolvable_name`, `Which_prefers_a_pathext_shim_over_a_same_named_bare_decoy`; `JUnitTriageSplitTests` gained the QAL-063 DI test above plus `TestLevel_ToDirName_matches_regardless_of_caller_casing`. 130/130 backend tests green throughout (started at 122, +8 new).
  - **QAL-071 opened** (not in the original 9 findings — discovered live while re-verifying QAL-062): see its own PRD entry.

### 2026-07-01 (E2E verification pass — QAL-062..070 opened)
- Ran a full API- and UI-level end-to-end verification of the built feature against the **real** ADO org (ConstellationDealer / Ideal Agile), the real "CRM Project" repo, real boards (CRM Team's plan 57868 "Target CRM", suites 173473 COWORK / 174566 Leads), and real ticket #171740 — per user direction, with real ADO write-through authorized (5 real Test Cases created and linked: #174568-174572).
- **Result: not usable end-to-end as delivered**, despite the 122/122 green unit suite and clean build — neither exercises a live repo scan, a live browser click, or a live process spawn, and all three break. 3 critical (whole-surface) blockers, 4 high-severity silent-correctness gaps, 2 architectural/minor findings — folded into new tasks **QAL-062..070** above (not yet started).
- Confirmed working and worth preserving during the fix-up: migration + all routes register cleanly; mechanical scan→generate→push chain is sound once fed real data (created real ADO Test Cases in house style, LinkTestedBy verified against #171740); `ImpactAnalyzerAgent`'s reasoning quality is genuinely good against 5 real Lead QA (#175) builds; CRM Project repo stayed git-clean throughout; Settings → QA Loop tab round-trips real config correctly.
- Also wired end-to-end (not a defect, a follow-up from the same session): `TEAMS_WEBHOOK_URL` added to `.env` by the user — the existing QAL-042/043 two-layer bridge already covered it correctly; verified live post-restart (`teams.webhook_url.set: true`), UI override in Settings still takes precedence over `.env` per the existing DB-over-env design. No code change needed.
- Full findings write-up with reproduction evidence (HTTP traces, file diffs, stack traces) is in the session's rendered report; each finding's summary is captured in its QAL-06x task description above.

### 2026-06-30 (QAL-061)
- **QAL-061** COMPLETED — BE: `GET /api/qaloop/runs/{id}/report` serves `run.ReportPath` as `text/html`; `?dl=true` adds `Content-Disposition: attachment`. FE: `activityEnabled: boolean` added to `TestRunSummary` (qaRuns.ts) + `ApiSessionSummary`/`ApiSessionDetail` (qaLoop.ts). QaRunsPage drawer: report buttons become "Report ↗" + "Download" conditional on `reportPath` (muted "report unavailable" when null); Console tab added for in-flight runs (AgentConsole when `activityEnabled`, LinearProgress otherwise); `DownloadIcon`+`LinearProgress`+`AgentConsole`+`runStreamUrl` imported. qaLoop.tsx: activity swap at "Activity" tab — `AgentConsole` when `activityEnabled` or stored events present, else `LinearProgress`; `LinearProgress` added to MUI import. SettingsPage.tsx: `ScienceOutlinedIcon` + "QA Loop" tab entry added; 5 new K entries (`TEAMS_WEBHOOK`/`TEAMS_TEMPLATE`/`QALOOP_POLL`/`QALOOP_REPORT`/`QALOOP_ACTIVITY`); state vars + loadConfig hydration + secret-clear + QA Loop panel with SecretField/Select/TextField/Switch. tsc + vite build clean; 122/122 backend tests pass.

### 2026-06-30 (QAL-060)
- **QAL-060** COMPLETED — Streaming gate wired: `PlaywrightRunSpec` gains `ActivityEnabled bool` (default true); `PlaywrightRunner.SpawnAsync` gains `suppressOutput` param — gates the `RelayLine` stdout/stderr publish when true; main Playwright run passes `suppressOutput: !spec.ActivityEnabled`; `RunExecutionService` propagates `run.ActivityEnabled` into spec; `QaLoopOrchestrator.EmitClosure` gains optional `activityEnabled` param (no-op lambda when false) — all 4 generator call sites updated; `PersistLog` skips `EventLogJson` when `session.ActivityEnabled = false`; coarse `Pub` phase/status transitions unchanged. 122/122 tests pass, build clean.

### 2026-06-30 (QAL-059)
- **QAL-059** COMPLETED — No changes needed: all three `*_generator_system.txt` prompts already authored in QAL-012 with full Allure instrumentation (test.step, request/response/assertion attachments, latency parameter, epic/feature/story/severity/tag/link metadata, [<tcId-pending>] preserved, no use: block, schema-validation on contract). EmbeddedResource via `*.txt` glob. Build verified.

### 2026-06-30 (QAL-058)
- **QAL-058** COMPLETED — No code changes needed: both keys fully wired in QAL-042/043. `AppSettingsService.AllKeys`/`OptionKeys`/`OptionValue` already include `qaloop.report_provider`+`qaloop.activity_enabled`; `.env` bridge + `RuntimeSettings` PostConfigure in `AddQaLoop` already present; `_qaLoopCache.Clear()` on `ApplyAsync` already wired. Verified build clean.

### 2026-06-30 (QAL-057)
- **QAL-057** COMPLETED — `PlaywrightRunner`: `GenerateAllureReportAsync` seeds history, runs `npx allure generate --single-file --clean`, copies history back, returns first `.html` in `allure-report/`; fallback = `html/index.html` or null; `provider=playwright` skips generate; `_historyDir` added; `PlaywrightRunSpec` gains `TriggerPipelineId`; `RunExecutionService` sets `run.ReportPath = result.ReportPath`.

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
