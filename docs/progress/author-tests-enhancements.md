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
**Iter 0 — scaffolding.** Create docs structure, CLAUDE.md rule, this tracker. → then start Phase 1.

---

## Phase 1 — Free-text context + refine loops (prompt-only)

### Backend
- [ ] `TestSession.cs`: add `AdditionalContext`, `CodeContext`, `TesterContext`, `ReferenceTestPlanId`, `ReferenceTestSuiteId`, `PlanNavigationHint`. — `pending-local-verify`
- [ ] `AuthorTestsContracts.cs`: `StartRequest`/`RunTesterRequest`/`RerunAnalystRequest`/`RefineTesterRequest`(+`TestFeedback`) + `TestSessionDetail` new fields. — `pending-local-verify`
- [ ] `IAgentOrchestrator.cs`: add `RerunAnalystAsync`, `RefineTesterAsync`; extend `StartAsync`/`RunTesterAsync` signatures. — `pending-local-verify`
- [ ] `AnalystAgent`: `BuildUserPrompt` gains `additionalContext`+`codeContext`; add `RefineAsync`. — `pending-local-verify`
- [ ] `TesterAgent`: `BuildUserPrompt` gains `testerContext`(+`existingTestsBlock` stub); add `RefineAsync`. — `pending-local-verify`
- [ ] `AgentOrchestrator`: thread `additionalContext`; add `RerunAnalystAsync`; add `RefineTesterAsync`+`ReconcileDrafts` merge. — `pending-local-verify`
- [ ] `AuthorTestsController.cs`: pass new fields; add `rerun-analyst` + `refine-tester` endpoints. — `pending-local-verify`
- [ ] EF migration `AuthorTestsContextAndIndex` (session fields; index tables added in Phase 3). — `pending-local-verify`

### Frontend
- [ ] `api/authorTests.ts`: extend `startSession`/`runTester` bodies; add `rerunAnalyst`, `refineTester`.
- [ ] `authorTests.tsx` start form: "Additional context" field.
- [ ] `authorTests.tsx` AwaitingTester gate: "Extra Tester context" box.
- [ ] `authorTests.tsx` Analyst panel: "Refine analysis with feedback".
- [ ] `authorTests.tsx` Tester panel/`TestDraftCard`: per-test feedback + remove toggle + overall feedback + "Regenerate with feedback".
- [ ] Re-seed all new fields from `session` detail.
- [ ] Verify: `npm run build` + `npm run lint` green.

### Docs (after Phase 1 verified)
- [ ] `docs/features/author-tests-context-and-refine.md` + `docs/changelog/` entry.

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
