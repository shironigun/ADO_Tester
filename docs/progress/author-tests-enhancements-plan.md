# Author Tests enhancements — design & plan (committed for local handoff)

Full design for the effort tracked in `author-tests-enhancements.md`. Committed so the work can be
continued from any machine (the original plan lived in a session-local plan file, not the repo).

## Context
Author Tests today only takes a work-item id; the Analyst runs on ticket text + KB with **zero code
awareness**, and there's no plain "here's extra context" channel or feedback-driven re-run. This effort adds:
1. **Free-text context** at start (→ Analyst) and at the Analyst→Tester gate (→ Tester).
2. **Code discovery** — a *Discover code* step backtracking the feature through the ADO repo (linked
   PRs/commits + free-text route/screen/module search) into the Analyst.
3. **Refine loops** — re-run the Analyst with feedback to improve-in-place; re-run the Tester as a
   feedback-driven **merge** (update flagged, add new, remove only flagged; pushed tests locked).
4. **Existing-test dedup** — optional reference Test Plan so the Tester considers existing ADO cases
   (even unlinked ones) via a cached scan/index and doesn't duplicate them.

## Locked decisions
- Work item stays required (no story-less runs). Free-text is guidance, not a replacement.
- Code discovery is a **shared service** extracted from `BugOrchestrator.DiscoverAnchoredContextAsync`,
  reused (behavior-preserving) so bug-pipeline parity holds.
- Injection at the **two meaningful gates** only (start, Analyst→Tester), not after every step.
- Dedup: load **everything in scope but efficiently** — all titles + learned title-patterns + FTS-ranked
  full steps for the most relevant; **both** enforcement layers (prompt-level + post-gen similarity);
  cached **first-time plan scan** reused across runs.
- Safe defaults: refine removal is feedback-driven only; changed tests reset `Approved→Draft`; pushed
  tests immutable; post-gen dedup auto-drops clear dupes, flags borderline.
- Frozen system prompts (`AnalystSystem`/`TesterSystem`) must NOT change — all guidance is user-prompt only.

## Phase 1 — context + refine loops  ✅ CODE COMPLETE (frontend verified; backend pending local build)
Backend: `TestSession` (+6 nullable cols), `AuthorRunContext`, contracts (`RunTesterRequest`,
`RerunAnalystRequest`, `RefineTesterRequest`/`TestFeedback`), `IAgentOrchestrator` (+`RerunAnalystAsync`,
`RefineTesterAsync`; `StartAsync`/`RunTesterAsync` take `AuthorRunContext`), `AnalystAgent.RefineAsync`,
`TesterAgent.RefineAsync` (+`RefinedTestCase`/`ExistingDraftForRefine`), `AgentOrchestrator`
(`RerunAnalystInnerAsync`, `RefineTesterInnerAsync`+`ReconcileDrafts`, `ApplyRunContextAsync`),
`AuthorTestsController` (+`rerun-analyst`, `refine-tester`; `run-tester` optional body).
Frontend: `api/authorTests.ts`, `session/types.ts`, `authorTests.tsx` (start-context field, gate
tester-context box, Analyst refine box, per-test feedback + Remove toggle, overall feedback + Regenerate).
**Remaining:** generate the migration locally; `dotnet build`/`dotnet test`.

## Phase 2 — code discovery  ⬜ NOT STARTED
- Extract `ICodeContextService` (Core/Abstractions) + `CodeContextService` (Infrastructure/CodeContext)
  from `BugOrchestrator.cs` `DiscoverAnchoredContextAsync` (~L417-532) + `TriageFilesAsync` (~L534-563)
  + budget consts (`MaxFiles`/`MaxBytesPerFile`/`MaxTotalBytes`). Generalize with `extraHintText`
  (adds user hints to `SearchCodeAsync` seeds) and `alwaysSearch` (search even when linked refs exist).
- `BugOrchestrator` delegates to it with `alwaysSearch:false` + no hint (byte-for-byte parity — verify
  `AdoGoldenTests`, `AgentOrchestratorTests`).
- `AgentOrchestrator.RunPipelineInnerAsync`: add a *Discover code* stage after Retrieve KB (~L201),
  emit `EventPhases.Discover`/`Triage`, store on `session.CodeContext`; it's already fed to the Analyst
  (the Analyst call already passes `session.CodeContext`).
- Frontend: insert a *Discover code* `PIPELINE_STEPS` entry between `retrieve` and `analyst`; update
  `activeStepIndex`; add a findings panel listing anchored/expanded files (Discover result event `files`).
- Reusable ADO surface: `SearchCodeAsync`, `GetFileContentAsync`, `GetWorkItemGitRefsAsync`,
  `GetPullRequestDiffAsync`, `GetCommitDiffAsync`, `GetCommitsForPathAsync`, `ListRepoFilePathsAsync`;
  helpers in `BugHelpers` (`SearchQuery`/`ExtractRelativeImports`/`ResolveImport`).

## Phase 3 — existing-test dedup + plan scan/index  ⬜ NOT STARTED
- `IExistingTestsIndexService` + `ExistingTestsIndexService`: `EnsureIndexAsync(planId, suiteId?,
  forceRescan)` (scan via `GetSuiteTestCasesAsync(recurse:true)` — already returns titles+steps across
  sub-suites), `BuildTesterContextAsync(scope, analysis, budget)`, `FindDuplicatesAsync(scope, cases)`.
- EF tables `TestPlanScan` + `ExistingTestCaseIndexEntry` + FTS5 mirror (bootstrap beside `doc_chunks`
  in `DatabaseInitializer`); reuse the KB FTS5 pattern.
- Wire into Tester paths: build the dedup block (session has `ReferenceTestPlanId`/`ReferenceTestSuiteId`/
  `PlanNavigationHint` already) → pass as `existingTestsBlock` to `TesterAgent.GenerateAsync`/`RefineAsync`
  (the param already exists, currently passed `null`); post-gen `FindDuplicatesAsync` → drop/flag.
- Frontend: reference plan/suite picker (reuse `getPlans`/`getSuites`) + nav-hint box in start form &
  gate; "Rescan plan" action; "skipped as duplicates" list from the dedup event.
- Note: suite hierarchy is a flat list with `ParentId` (rebuild tree client/server-side); `AdoTestSuite`
  has no `SuiteType`; preconditions aren't read (dedup on title+steps).

## Verification (per phase, before its docs are written)
`dotnet build`; `dotnet test` (esp. `AdoGoldenTests`, `AgentOrchestratorTests`); `npm run build` +
`npm run lint` (repo has a pre-existing lint baseline of 55 errors — only regressions count).
Manual: ticket **171740**; a thin ticket + route hint; refine loops; dedup against a reference plan.
