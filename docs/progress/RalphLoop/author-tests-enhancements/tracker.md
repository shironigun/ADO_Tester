# Author Tests Enhancements — Ralph Loop Tracker

**Branch:** `claude/chat-session-flzhjh`  
**Plan doc:** `docs/progress/RalphLoop/author-tests-enhancements/plan.md`  
**Last updated:** 2026-07-28

## Status summary

| Phase | Description | Status |
|-------|-------------|--------|
| 1 | Free-text context + refine loops | ✅ done |
| 2 | Code discovery (ICodeContextService) | ✅ done |
| 3 | Existing-test dedup (IExistingTestsIndexService) | ✅ done |
| — | EF migration (local) | ⏳ pending-local-verify |
| — | Feature doc + changelog | ⏳ pending-local-verify |

---

## Phase 1 — Free-text context + refine loops

### Backend

- [x] `TestSession.cs` — add `AdditionalContext`, `CodeContext`, `TesterContext` (`string?`), `ReferenceTestPlanId`/`ReferenceTestSuiteId` (`int?`), `PlanNavigationHint` (`string?`) · **done**
- [x] `AuthorTestsContracts.cs` — `StartRequest` += context/ref-plan fields; `RunTesterRequest` with context + ref-plan; `RerunAnalystRequest { Feedback }`; `RefineTesterRequest { OverallFeedback, PerTest }` · **done**
- [x] `AuthorTestsController.cs` — pass new fields through `Start`/`RunTester`; add `POST /sessions/{id}/rerun-analyst`; add `POST /sessions/{id}/refine-tester` · **done**
- [x] `IAgentOrchestrator.cs` — add `RerunAnalystAsync` / `RefineTesterAsync` signatures · **done**
- [x] `AnalystAgent` — `additionalContext` + `codeContext` params → `## Additional Context` / `## Code Context` sections · **done**
- [x] `AnalystAgent` refine overload — `RefineAsync(prior, feedback, wi, …)` with `## Reviewer Feedback` section · **done**
- [x] `TesterAgent` — `testerContext` param → `## Extra Context` section · **done**
- [x] `TesterAgent` refine overload — `RefineAsync(…, existingTests, overallFeedback, …)` with per-test ref + feedback · **done**
- [x] `AgentOrchestrator` — `RerunAnalystAsync`, `RefineTesterAsync` (merge reconcile via `ReconcileDrafts`) · **done**

### Frontend

- [x] `api/authorTests.ts` — `rerunAnalyst`, `refineTester`, extend `startSession`/`runTester` bodies · **done**
- [x] `AuthorStartForm` — "Additional context" TextField · **done**
- [x] `AwaitingTester` banner — "Extra context for the Tester" box; pass to `runTester` · **done**
- [x] Analyst panel — "Refine analysis with feedback" box + button → `rerunAnalyst` · **done**
- [x] Tester panel — per-test feedback + remove toggle + overall feedback + "Regenerate with feedback" → `refineTester` · **done**

---

## Phase 2 — Code discovery (ICodeContextService)

### Backend

- [x] `QaAutomation.Core/Abstractions/ICodeContextService.cs` — interface: `DiscoverAsync(…, extraHintText, alwaysSearch, …)` returning `(string DiffSummary, string Code)` · **done**
- [x] `QaAutomation.Infrastructure/CodeContext/CodeContextService.cs` — logic extracted + generalized from `BugOrchestrator`; `alwaysSearch`/`extraHintText` knobs; `MaxFiles=20`, `MaxBytesPerFile=30_000`, `MaxTotalBytes=250_000` · **done**
- [x] `DependencyInjection.cs` — register `ICodeContextService` as singleton · **done**
- [x] `AgentOrchestrator` — insert Discover step after Retrieve KB; emit `EventPhases.Discover`; store `session.CodeContext`; feed `additionalContext` + code into Analyst · **done**
- [x] `ConsoleEvent.cs` — `EventPhases.Dedup = "dedup"` added · **done**
- [x] Bug-pipeline parity — `BugOrchestrator` still calls extracted logic unchanged; parity tests expected to remain green · **done** *(user verifies locally)*

### Frontend

- [x] `PIPELINE_STEPS` — insert "Discover code" step at index 2 (TravelExploreIcon) · **done**
- [x] `activeStepIndex` — shift indices: discover=2, analyst=3, tester=4 · **done**
- [x] `reachedIdx` — use `session.codeContext` for the discover gate · **done**
- [x] `sel === 2` panel — code context card (pre-formatted snippet or "No code context" fallback) · **done**
- [x] `useEventStream.ts` — `'dedup'` added to phase union · **done**
- [x] `shared.tsx` — `dedup: 'DEDUP'` added to `PHASE_LABEL` · **done**

---

## Phase 3 — Existing-test dedup (IExistingTestsIndexService)

### Backend

- [x] `QaAutomation.Core/Abstractions/IExistingTestsIndexService.cs` — interface: `EnsureIndexAsync`, `GetScanStatusAsync`, `BuildTesterContextAsync`, `FindDuplicatesAsync`; `DedupResult` + `GeneratedTestCase` records · **done**
- [x] `QaAutomation.Core/Models/TestPlanScan.cs` — EF entity (planId, suiteId?, scannedAt, caseCount, titlePatternSummary) · **done**
- [x] `QaAutomation.Core/Models/ExistingTestCaseIndexEntry.cs` — EF entity (scanId FK, adoTestCaseId, suitePath, title, titleSignature, stepsSummary, stepsHash) · **done**
- [x] `QaAutomation.Infrastructure/ExistingTests/ExistingTestsIndexService.cs` — full implementation; FTS5 `test_case_index`; 24h TTL cache; title-signature normalization; step-overlap dedup · **done**
- [x] `QaDbContext.cs` — `TestPlanScans` + `ExistingTestCaseIndexEntries` DbSets; explicit Phase 1 TestSession field mappings · **done**
- [x] `DatabaseInitializer.cs` — bootstrap `test_case_index` FTS5 virtual table alongside `doc_chunks` · **done**
- [x] `DependencyInjection.cs` — register `IExistingTestsIndexService` as singleton · **done**
- [x] `AgentOrchestrator` — inject dedup block before Tester runs; post-gen `FindDuplicatesAsync`; emit dedup result event · **done**
- [x] `AuthorTestsController.cs` — `POST plans/{planId}/rescan` + `GET plans/{planId}/scan-status` endpoints · **done**
- [x] `AuthorTestsContracts.cs` — `RescanPlanResponse` + `ScanStatusResponse` contracts · **done**
- [ ] **EF migration** — `dotnet ef migrations add AuthorTestsContextAndIndex --project src/QaAutomation.Infrastructure --startup-project src/QaAutomation.Api` · **pending-local-verify** *(run locally by user)*

### Frontend

- [x] `api/authorTests.ts` — `rescanPlan`, `getScanStatus` functions · **done**
- [x] `AwaitingTester` gate — ref plan + suite pickers; "Rescan plan" button; "indexed N cases" status; nav hint field · **done**
- [x] `AuthorStartForm` — ref plan + suite pickers; nav hint field; pass all to `startSession` · **done**
- [x] Tester panel — dedup results card (skipped / flagged chips with matched-TC tooltip) · **done**

---

## Decisions log

| Date | Decision |
|------|----------|
| 2026-07-28 | Docs discipline rule added to `CLAUDE.md`; `/docs` structure established |
| 2026-07-28 | Phase 1 backend committed (`4ca1206`); frontend committed (`9ba61ca`) |
| 2026-07-28 | Phase 2+3 backend + frontend committed (`14b17b2`) |
| 2026-07-28 | Unicode curly-quote injection from Edit tool fixed via Python byte-replacement |
| 2026-07-28 | Lint: 3 new `react-hooks/set-state-in-effect` warnings — same pre-existing pattern, not blocking |
| 2026-07-28 | `BugOrchestrator` left unchanged (parity guardrail); `CodeContextService` used by Author Tests only for now |
| 2026-07-28 | EF migration deferred to user's local machine (no `dotnet` SDK in CI container) |

---

## Verification checklist (user-runs locally)

- [ ] `dotnet build` — all projects green
- [ ] `dotnet test --filter "FullyQualifiedName~AdoGoldenTests"` — parity unchanged
- [ ] `dotnet test --filter "FullyQualifiedName~AgentOrchestratorTests"` — orchestrator tests green
- [ ] `dotnet ef migrations add AuthorTestsContextAndIndex ...` — migration generates and applies on restart
- [ ] `npm run build && npm run lint` — frontend green (lint warnings pre-existing, not new errors)
- [ ] Manual: start a run with additional context → Discover step visible in pipeline → code panel populated
- [ ] Manual: thin ticket + route hint → `SearchCodeAsync` backtracking finds implementation
- [ ] Manual: AwaitingTester gate → extra Tester context → passes to Tester
- [ ] Manual: Analyst refine → gaps filled in analysis, session returns to AwaitingTester
- [ ] Manual: Tester refine → per-test update, removal, new tests, pushed tests untouched
- [ ] Manual: reference plan → first run scans + indexes; dedup result card shows skipped/flagged; second run reuses cache; Rescan plan resets
- [ ] Once above pass: write `docs/features/author-tests-free-form-context.md` + `docs/changelog/` entry

---

## Current iteration note

All three phases implemented and pushed (`14b17b2`). EF migration and feature-doc/changelog are the only remaining items, both gated on user's local verification pass.
