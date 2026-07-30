# Ralph Loop — Scripts Board Module

Scripts Board is the third and final slab of the QA Loop v2 feature. It is a **new top-level module**
that gives QA a single place to manage, run, and track the entire generated automation framework
(API + UI specs, all levels) with a live xterm.js terminal, history, trends, and flakiness analytics.

This folder is a **dedicated doc set** for Scripts Board only. The parent qa-loop-v2 docs cover the
full three-slab story (see `docs/progress/RalphLoop/qa-loop-v2/`); this folder tracks the Slab 3
implementation independently so progress, blockers, and design notes have a single clear home.

**Task IDs:** `SB-3##` (mapped 1:1 to the `QLV2-3##` IDs carried over from the qa-loop-v2 PRD —
`SB-301` ≡ `QLV2-301`, etc. Cross-references in both PRDs are kept consistent).

## Slabs already shipped (prerequisites)
- **Slab 1 — Free-text + merge-refine in QA Loop** — `[COMPLETE]` (QLV2-101..113)
- **Slab 2 — UI testing phase** — `[COMPLETE]` (QLV2-201..208)

## Scripts Board tasks
Tasks `SB-301..SB-309`, built in order. Current resume point: **SB-304**.

---

## Per-iteration cycle

1. **Read** `progress.md` for the resume point and completed work.
2. **Select** the highest-priority incomplete `[ ]` task from `prd.md`.
3. **Read existing code** before writing any new code (`backend/src/`, `client/src/`).
4. **Implement** that single task following existing patterns (see `blueprint.md`).
5. **Test** — add/update xUnit (backend) or update frontend build/lint as applicable.
6. **Verify** — `dotnet build` + `dotnet test` (esp. `AdoGoldenTests` — byte-exact ADO parity is
   load-bearing) and `npm run build` + `npm run lint`. Fix failures before proceeding.
7. **Update PRD** — flip `[ ]` → `[x]` for the completed task.
8. **Update Progress** — append task ID, files changed, and a what/why note to `progress.md`.
9. **Commit** only verified chunks. **Loop** to step 1, or declare **SCRIPTS BOARD COMPLETE** when
   all SB-309 tasks are done.

## Rules

- ONE task per iteration; `progress.md` is memory between iterations and is always updated.
- **NEVER commit failing code.**
- If a task is `[x]` in `prd.md` but not recorded in `progress.md`, treat it as **not started**.
- If stuck after 3 attempts: log the blocker in `progress.md` and move to the next task.
- All new agent instructions go in **USER** prompts — embedded system prompts stay byte-exact.
- Additive EF migrations only; they apply on startup via `DatabaseInitializer`.
- New `qaloop.*` settings follow the two-layer pattern (.env → Options → RuntimeSettings overlay) +
  `AppSettingsService` registration.
- Verification for every task with a runtime surface: exercise against standing story **177540**
  (see `.cursor/rules/ralph-loop-ops.mdc`). If the ADO PAT is expired, use `dotnet test` + flag the
  live check as pending in `progress.md` rather than faking it.
- Scripts Board runs use the **negative `RunId` namespace** (`RunId = -scriptBoardRun.Id`) to keep
  disk artifacts disjoint from `TestRun` positive IDs — never change this convention.
- The raw-ANSI live terminal uses a **separate spawn path** (`ScriptsBoardRunner`) — do NOT touch
  `PlaywrightRunner.SpawnAsync` (golden-tested QA-Runs path).

## Build / Test / Dev commands

```powershell
# Backend (from backend/)
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~AdoGoldenTests"
dotnet test --filter "FullyQualifiedName~ScriptBoard"   # Scripts Board tests only

# Frontend (from client/)
npm run build
npm run lint

# Dev servers for manual UI verification
dotnet run --project backend/src/QaAutomation.Api   # :5250, Swagger /swagger
npm run dev                                          # :5173, proxies /api → :5250
```

> The `dotnet` SDK is not on PATH on this machine — use the wrapper from the `dotnet-build-environment`
> memory before running the backend commands. Required: `required_permissions: ["all"]` on every Shell
> tool call (see `.cursor/rules/ralph-loop-ops.mdc`).

## Docs discipline (CLAUDE.md standing rule)

- `docs/features/scripts-board.md` + a `docs/changelog/` entry are written **after** the full module
  is implemented **and** verified (SB-309). `progress.md` is updated **every iteration**.
- Keep `solution.md` and `blueprint.md` authoritative — if an implementation decision deviates from
  them, update the doc in the same commit and note the deviation in `progress.md`.
