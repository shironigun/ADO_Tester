# Changelog

Newest entries first.

---

## 2026-07-29

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
