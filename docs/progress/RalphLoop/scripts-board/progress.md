# Progress: Scripts Board Module Implementation

## Overview
- **Design:** `solution.md` (authoritative) · **Ruleset:** `Ralph loop.md`
- **Backlog:** `prd.md` (tasks `SB-3##`) · **Guide:** `blueprint.md`
- **Started:** 2026-07-29 (as part of qa-loop-v2 Slab 3)
- **Dedicated folder created:** 2026-07-30
- **Status:** SB-301 / SB-302 / SB-303 **VERIFIED**; SB-304 implementation is complete and
  automated verification is green (183/183 tests), but its required live raw-SSE check is pending.
  **Resume point: SB-304 verification.**

## Known blockers

| Blocker | Impact | Resolution |
| ------- | ------ | ---------- |
| `ADOACCESS_PAT` in `.env` is expired (every live ADO call returns 401 — confirmed via `GET /api/diagnostics/workitem/177540`). | Live ADO-touching verification blocked (Author Tests push, QA Loop push, Find Bugs). Scripts Board itself is ADO-optional, but any task that requires pushing test cases to verify `@qa-meta` linking end-to-end cannot be fully live-verified. | Refresh the PAT in `.env`. Until then: rely on `dotnet test` + `npm run build`/`lint` and flag live ADO checks as pending in this log rather than skipping the task. |
| Docker Desktop (`com.docker.backend.exe`) intermittently holds port 5250 on this machine (WSL2 networking proxy). | `dotnet run --project src\QaAutomation.Api` may fail to bind :5250 if Docker is running. | Stop the conflicting Docker container or free the port before starting the backend dev server. |
| `Shell` tool requires `required_permissions: ["all"]` on every call on this machine (no sandbox backend available). | Every shell command silently fails to spawn without it. | Always include it up front — see `.cursor/rules/ralph-loop-ops.mdc`. |

## Slab status

| Task | Name | Status |
| ---- | ---- | ------ |
| SB-301 | `@qa-meta` header convention | **VERIFIED** ✔ |
| SB-302 | Data + inventory/reconcile service | **VERIFIED** ✔ |
| SB-303 | Execution service + controller | **VERIFIED** ✔ |
| SB-304 | Raw-ANSI live terminal stream | 🟡 Implemented — live SSE verification pending |
| SB-305 | Trends + flakiness service | ⬜ Pending |
| SB-306 | Frontend module scaffold | ⬜ Pending |
| SB-307 | xterm.js live terminal component | ⬜ Pending |
| SB-308 | Trends + flakiness UI | ⬜ Pending |
| SB-309 | Tests + docs (close-out) | ⬜ Pending |

---

## Log

### 2026-07-29 (SB-301 — @qa-meta header)

**Task:** SB-301 · **Files:** `Core/Models/QaMeta.cs` (new), `QaLoopOrchestrator.cs` (4 call-sites),
`QaMetaHeaderTests.cs` (new, 7 tests).

**What:** `QaMeta` sealed record + `QaMetaHeader` static class with `Build`/`TryParse`/`StripHeader`/
`Upsert`/`ComputeHash`. Baked into `QaLoopOrchestrator` at every spec-write site:
- `PersistDraft` → initial API spec write (Contract/Endpoint/Workflow).
- `RefineDraftsInnerAsync` → disk rewrite on refine.
- `GenerateUiScriptsInnerAsync` → stamps Claude-CLI-generated `.ui.spec.ts` before persisting.
- `HealSpecsInnerAsync` → re-stamps carrying forward prior header fields (or mints fresh).
- `RewriteSpecOnDisk` → push-time — opportunistically fills in `TcId`, does not backfill legacy specs.

**Why:** Spec identity without a DB query — the inventory service needs to reconcile disk↔DB without
reading every spec's DB row upfront; the `@qa-meta` line makes each spec self-describing.

**Verification:** 170/170 tests pass (`dotnet build` clean). Live ADO-touching check blocked (PAT
expired) — flagged in blockers table above. SB-301 has zero ADO-write surface so automated tests
are sufficient for `[x]`.

---

### 2026-07-29 (SB-302 — inventory/reconcile service)

**Task:** SB-302 · **Files:** `Core/Models/ScriptBoardRun.cs` (new), `Data/QaDbContext.cs`,
`Migrations/AddScriptBoardRuns` (SQLite + Npgsql), `Core/Abstractions/IScriptInventoryService.cs` (new),
`Infrastructure/ScriptsBoard/ScriptInventoryService.cs` (new), `DependencyInjection.cs`,
`Tests/ScriptInventoryServiceTests.cs` (new, 6 tests).

**What:** `ScriptBoardRun` entity (deliberately no ADO fields — ADO-optional by design). Additive
`CREATE TABLE` migration for both providers. `ScriptInventoryService` scans `{DataDirectory}/scripts/**/*.spec.ts`
(API) + `{PlaywrightRoot}/generated/ui/**/*.ui.spec.ts` (UI), parses `@qa-meta` per file, cross-references
`ApiTestDraft` DB rows by normalized absolute path, classifies: `linked` (DB row + `Meta` present) /
`no_meta` (DB row but no header — legacy pre-SB-301) / `orphan_disk` (no DB row) / `orphan_db`
(DB row exists but file gone). UI root skipped gracefully when `PlaywrightRoot` unset.

**Why:** Inventory reconcile is the heart of Scripts Board — it tells the user exactly which specs
exist, where they came from, and whether anything drifted.

**Note (implementation deviation):** Slab-2 UI scripts live at `{PlaywrightRoot}/generated/ui/{bucket}/…`
(not at the aspirational `scripts/{module}/ui/{bucket}/…` from `solution.md`). Slab 3 v1 scans both
roots as they exist today and does not relocate Slab-2 output. Documented as a known v1 limitation.

**Verification:** 176/176 tests pass (`dotnet build` clean). Live boot-check attempted but port 5250
was held by Docker (see blockers) — migration shape verified by test-harness `DatabaseInitializer`
spin-up. Flagged in blockers; user should restart backend to pick up the new table.

---

### 2026-07-29 (SB-303 — execution service + controller)

**Task:** SB-303 · **Files:** `Core/Abstractions/IPlaywrightRunner.cs` (`PlaywrightRunSpec.Headed`/
`.Workers`), `Infrastructure/QaRuns/PlaywrightRunner.cs` (`WriteRunConfig` branches),
`Core/Models/ScriptBoardRun.cs` (`Module` column), `Migrations/AddScriptBoardRunModule` (both providers),
`Core/Abstractions/IScriptBoardExecutionService.cs` (new), `Infrastructure/ScriptsBoard/ScriptBoardExecutionService.cs`
(new), `Core/Contracts/ScriptsBoardContracts.cs` (new), `Api/Controllers/ScriptsBoardController.cs` (new),
`DependencyInjection.cs`, `Tests/ScriptBoardExecutionServiceTests.cs` (new, 6 tests).

**What:** `PlaywrightRunSpec.Headed`/`.Workers` additive (zero behavior change for existing QA Runs —
`PlaywrightRunner.WriteRunConfig` only branches when set). `ScriptBoardExecutionService.StartAsync`
resolves filter → specs (explicit paths win; else inventory filter excluding `orphan_db` items with
no file to run) → `ArgumentException` on zero matches → inserts `Queued ScriptBoardRun` → returns
run ID immediately. `ExecuteAsync` sets `Running` → `PlaywrightRunSpec { RunId = -runId }` (negative
namespace — disk artifacts at `{runsDir}/-{id}/`, disjoint from `TestRun`'s positive IDs) →
`IPlaywrightRunner.RunAsync` → maps result → `Completed`/`Failed`/`Cancelled`. `ScriptsBoardController`
background-task shape copied from `QaRunsController.Start` (own DI scope, same `RunCancellationRegistry`
singleton, negative key for cancel/remove). Six endpoints wired.

**Why:** Enables ad-hoc spec execution without a QA-Loop session. The negative RunId trick avoids
any schema or path changes to the existing QA-Runs artifact directory scheme.

**Verification:** 182/182 tests pass (`dotnet build` clean). `POST /api/scripts-board/runs` round-trip
verified locally (Swagger UI). Negative RunId confirmed non-colliding with test-harness positive IDs
in `ScriptBoardExecutionServiceTests`. Live ADO PAT check blocked — flagged above.

---

### 2026-07-30 (dedicated folder created)

Created `docs/progress/RalphLoop/scripts-board/` as a standalone Ralph loop folder for Scripts Board
(previously tracked within qa-loop-v2). Contains:
- `Ralph loop.md` — ruleset adapted for Scripts Board.
- `solution.md` — full Scripts Board design (extracted + expanded from qa-loop-v2 Slab 3).
- `blueprint.md` — file-by-file guide for SB-301..309 (SB-301/302/303 as implementation records;
  SB-304..309 as active build specs).
- `prd.md` — task backlog (SB-301..309; 301-303 marked `[x]`).
- `progress.md` — this file.

No code changes. Parent qa-loop-v2 docs (`prd.md`, `progress.md`) remain authoritative for cross-slab
tracking; this folder provides a focused home for Scripts Board only.

**Resume point: SB-304** (raw-ANSI live terminal stream).

---

### 2026-07-30 (SB-304 — implementation complete; live verification pending)

**Task:** SB-304 · **Files:** `Core/Models/ConsoleEvent.cs`,
`Core/Abstractions/IScriptsBoardRunner.cs` (new), `Infrastructure/QaRuns/PlaywrightRunner.cs`,
`Infrastructure/QaRuns/PlaywrightConfigWriter.cs` (new),
`Infrastructure/QaRuns/PlaywrightResultParser.cs` (new),
`Infrastructure/ScriptsBoard/ScriptsBoardRunner.cs` (new),
`Infrastructure/ScriptsBoard/ScriptBoardExecutionService.cs`, `DependencyInjection.cs`,
`Tests/ScriptBoardExecutionServiceTests.cs`, `Tests/ScriptsBoardRunnerTests.cs` (new).

**What:** Added the live-only `term` event kind (it is intentionally excluded from the durable
`EventKinds.Narrative` set). Extracted the configuration writer and JUnit result parser from
`PlaywrightRunner` into shared internal helpers. Added a separate `IScriptsBoardRunner` /
`ScriptsBoardRunner` raw-terminal path: it shares the established runner's provisioned runtime,
automation authentication, concurrency gate, report generation, and result parsing, but reads child
stdout/stderr as raw byte chunks with `FORCE_COLOR=1` and publishes raw ANSI chunks as `term` events.
`ScriptBoardExecutionService` now uses this new runner. The existing QA Runs `SpawnAsync` remains
line-buffered and unmodified. DI now maps both interfaces to the same `PlaywrightRunner` singleton
where appropriate, with `ScriptsBoardRunner` registered separately.

**Why:** A real terminal needs unmodified ANSI chunks. Isolating the new path protects the
golden-tested QA Runs execution behavior while still sharing provisioning and Allure/report behavior.

**Automated verification:** `dotnet build src\QaAutomation.Infrastructure\QaAutomation.Infrastructure.csproj`
passed with 0 warnings/errors; `dotnet test tests\QaAutomation.Tests\QaAutomation.Tests.csproj` passed
**183/183**. The new `ScriptsBoardRunnerTests.Term_event_is_streamed_but_excluded_from_narrative`
asserts ANSI terminal chunks are delivered live and never persisted; existing Scripts Board execution
tests now verify the service calls `IScriptsBoardRunner` while preserving the negative RunId contract.

**Live verification pending:** Full solution build cannot complete while the user-owned
`QaAutomation.Api` process (PID 48580) locks its output DLLs. It was not stopped. The required
`curl -N /api/scripts-board/runs/{id}/stream` raw-SSE validation must run after restarting that
backend against these binaries. The standing ADO work-item lookup for story 177540 also timed out via
the ADO integration, so no live ADO-dependent verification was attempted.
