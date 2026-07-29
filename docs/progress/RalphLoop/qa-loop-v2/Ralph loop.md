# Ralph Loop — Agentic Development Pattern for QA Loop v2

This feature is delivered as **three independently-shippable slabs**, built in order. The doc set is
**flat** (matching `author-tests-enhancements/` and `qa-loop-reshape/`): `solution.md` (authoritative
design) → `blueprint.md` (file-by-file guide) → `prd.md` (tasks `QLV2-###`, grouped by slab, built in
order) → `progress.md` (execution memory). `solution.md` is the source of truth over memory.

Slabs (build order):
1. **Slab 1 — Free-text + merge-refine in QA Loop**
2. **Slab 2 — UI testing phase**
3. **Slab 3 — Scripts Board module**

## Per-iteration cycle

1. **Read** `progress.md` for the resume point and completed work.
2. **Select** the highest-priority incomplete task `[ ]` from `prd.md` (slabs are built in order).
3. **Read existing code** before writing new code (backend `backend/src/`, frontend `client/src/`).
4. **Implement** that single task following existing patterns.
5. **Test** — add/update xUnit (backend) or the frontend build/lint as applicable.
6. **Verify** — `dotnet build` + `dotnet test` (esp. `AdoGoldenTests` — byte-exact ADO parity is
   load-bearing) and `npm run build` + `npm run lint`. Fix failures before proceeding.
7. **Update PRD** — flip `[ ]` → `[x]` for the completed task.
8. **Update Progress** — append task ID, files changed, and a what/why note to `progress.md`.
9. **Commit** only verified chunks. **Loop** to step 1, or `<promise>COMPLETE</promise>` when the slab's
   tasks are all done.

## Rules
- ONE task per iteration; `progress.md` is memory between iterations and is always updated.
- **NEVER commit failing code.**
- If a task is `[x]` in `prd.md` but not recorded in `progress.md`, treat it as **not started**.
- If stuck after 3 attempts: log the blocker in `progress.md` and move to the next task.
- All new agent instructions go in **USER** prompts — embedded system prompts stay byte-exact.
- Additive EF migrations only; they apply on startup via `DatabaseInitializer`.
- New `qaloop.*` settings follow the two-layer pattern (.env → Options → RuntimeSettings overlay) +
  `AppSettingsService` registration.

## Build / Test / Dev commands
```
# Backend (from backend/ or with --project backend/src/QaAutomation.Api)
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~AdoGoldenTests"

# Frontend (from client/)
npm run build
npm run lint

# Dev servers for manual UI verification
dotnet run --project backend/src/QaAutomation.Api   # :5250, Swagger /swagger
npm run dev                                          # :5173, proxies /api → :5250
```
> The `dotnet` SDK is not on PATH on this machine — use the wrapper from the `dotnet-build-environment`
> memory before running the backend commands.

## Docs discipline (CLAUDE.md standing rule)
- `docs/features/*` page + `docs/changelog/` entry are written **after** a slab is implemented AND
  verified. `progress.md` is updated **every iteration**.
