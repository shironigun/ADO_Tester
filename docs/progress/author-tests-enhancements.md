# Progress tracker — Author Tests enhancements (Ralph loop)

**Plan:** free-form context + code-backtracking + Analyst/Tester refine loops + existing-test dedup.
**Branch:** `claude/chat-session-flzhjh`
**Execution model:** Ralph loop — each iteration: read this tracker → do the smallest next `todo` unit
→ verify → update this tracker + `/docs` (docs only after verified) → commit/push → repeat.

## Environment constraint (important)
This remote container has **no .NET SDK** and the org egress policy **blocks the .NET/NuGet hosts**, so
`dotnet build`/`dotnet test` **cannot run here**. Decision (user-approved): write backend code against
existing patterns, verify **frontend** here (`npm run build`/`lint`), and mark backend units
`pending-local-verify`. The user runs `dotnet build`/`dotnet test` locally/CI and reports failures,
which are fixed in subsequent loop iterations. A backend unit is only `done` once locally verified.

## Status legend
`todo` · `in-progress` · `done` (verified) · `pending-local-verify` (backend written, awaiting user's
local build/test) · `blocked` (needs a human decision)

---

## Current iteration
**Iter 1 — Phase 1 backend (data + agents + orchestrator + controller).** Written; `pending-local-verify`.
Next: Phase 1 frontend (verifiable here via `npm`), then Phase 1 docs.

### ⚠️ Migration must be generated locally (one command)
The model gained 6 nullable columns on `TestSession`. Do **not** hand-edit the EF snapshot (it would make
a later `migrations add` emit an empty migration). On a machine with the SDK, run:
```
dotnet ef migrations add AuthorTestsContextFields \
  --project src/QaAutomation.Infrastructure --startup-project src/QaAutomation.Api
```
It generates the migration + Designer + snapshot update; applies automatically on next backend start.

---

## Phase 1 — Free-text context + refine loops (prompt-only)

### Backend
- [x] `TestSession.cs`: add `AdditionalContext`, `CodeContext`, `TesterContext`, `ReferenceTestPlanId`, `ReferenceTestSuiteId`, `PlanNavigationHint`. — `pending-local-verify`
- [x] `AuthorRunContext` record (Core/Models) — carries free-text + reference plan through the interface. — `pending-local-verify`
- [x] `AuthorTestsContracts.cs`: `StartRequest`/`RunTesterRequest`/`RerunAnalystRequest`/`RefineTesterRequest`(+`TestFeedback`) + `TestSessionDetail` new fields. — `pending-local-verify`
- [x] `IAgentOrchestrator.cs`: add `RerunAnalystAsync`, `RefineTesterAsync`; extend `StartAsync`/`RunTesterAsync` signatures. — `pending-local-verify`
- [x] `AnalystAgent`: `BuildUserPrompt` gains `additionalContext`+`codeContext`; add `RefineAsync`. — `pending-local-verify`
- [x] `TesterAgent`: `BuildUserPrompt` gains `testerContext`+`existingTestsBlock`; add `RefineAsync` (+`RefinedTestCase`/`ExistingDraftForRefine`). — `pending-local-verify`
- [x] `AgentOrchestrator`: thread context via session; `RerunAnalystInnerAsync`; `RefineTesterInnerAsync`+`ReconcileDrafts` merge; `ApplyRunContextAsync`. — `pending-local-verify`
- [x] `BugOrchestrator.cs:169`: AnalystAgent call switched to named args (parity-preserving) after AnalyzeAsync gained params. — `pending-local-verify`
- [x] `AuthorTestsController.cs`: thread new fields; add `rerun-analyst` + `refine-tester` endpoints; `run-tester` optional body. — `pending-local-verify`
- [ ] EF migration `AuthorTestsContextFields` — **generate locally** (see command above). — `needs-user-migration`

### Frontend  (verified here — `npm run build` green; lint net-zero: 65 problems before & after)
- [x] `api/authorTests.ts`: extend `startSession`/`runTester` bodies; add `rerunAnalyst`, `refineTester`, `TestFeedbackItem`.
- [x] `session/types.ts`: `SessionDetail` tolerates `additionalContext`/`testerContext`/reference-plan fields.
- [x] `authorTests.tsx` start form: "Additional context" field → `startSession` body.
- [x] `authorTests.tsx` AwaitingTester gate: "Extra Tester context" box → `runTester` body.
- [x] `authorTests.tsx` Analyst panel: "Refine analysis with feedback" → `rerunAnalyst`.
- [x] `authorTests.tsx` `TestDraftCard`: per-test feedback + Remove toggle; dialog: overall feedback + "Regenerate with feedback" → `refineTester`.
- [x] Fields seed from `session` detail via `useState` initializers (no extra effect → no new lint error).

### Docs (after Phase 1 verified end-to-end incl. backend build)
- [ ] `docs/features/author-tests-context-and-refine.md` + `docs/changelog/` entry. — deferred until backend builds locally

## Phase 2 — Code discovery
- [ ] Extract `ICodeContextService` + `CodeContextService` from `BugOrchestrator` (behavior-preserving delegation). — `pending-local-verify`
- [ ] Add *Discover code* pipeline stage in `AgentOrchestrator`; feed Analyst. — `pending-local-verify`
- [ ] Frontend: *Discover code* pipeline step + findings panel.
- [ ] Verify bug-pipeline parity (`AdoGoldenTests`, `AgentOrchestratorTests`). — `pending-local-verify`
- [ ] Docs: feature page + changelog.

## Phase 3 — Existing-test dedup + plan scan/index
- [ ] `IExistingTestsIndexService` + `ExistingTestsIndexService` (scan/cache/FTS/pattern-learning). — `pending-local-verify`
- [ ] EF: `TestPlanScan` + `ExistingTestCaseIndexEntry` tables + FTS bootstrap in `DatabaseInitializer`. — `pending-local-verify`
- [ ] Wire dedup into Tester paths (prompt block + post-gen `FindDuplicatesAsync`). — `pending-local-verify`
- [ ] Frontend: reference plan/suite picker + nav hint + rescan + skipped-dupes list.
- [ ] Docs: feature page + changelog.

---

## Decisions / log
- **Iter 0:** Backend unbuildable in this container (.NET/NuGet egress-blocked) → user approved
  "write now, verify locally". Frontend is verifiable here.
- Safe defaults (from planning): refine removal is feedback-driven only; changed tests reset
  `Approved→Draft`; pushed tests locked; post-gen dedup auto-drops clear dupes, flags borderline.
