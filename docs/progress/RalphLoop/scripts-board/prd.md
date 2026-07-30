# PRD — Scripts Board Module (task backlog)

Tasks `SB-3##`, built in order. These map 1:1 to `QLV2-3##` in the parent qa-loop-v2 PRD
(`SB-301` ≡ `QLV2-301`, etc.). One task per Ralph-loop iteration; flip `[ ]`→`[x]` here
**and** log in `progress.md` only after the task is implemented **and** verified.

Acceptance-criteria shorthand: **B** = `dotnet build` clean · **T** = `dotnet test` green (incl.
`AdoGoldenTests`) · **F** = `npm run build` + `npm run lint` clean on touched files.

---

## Completed tasks (DONE + VERIFIED)

- [x] **SB-301** — `QaMeta` / `QaMetaHeader` (`Core/Models/QaMeta.cs`): `Build` / `TryParse` /
  `StripHeader` / `Upsert` / `ComputeHash`. Baked into `QaLoopOrchestrator`: `PersistDraft` (initial
  API spec write), `RefineDraftsInnerAsync` disk rewrite, `GenerateUiScriptsInnerAsync` (stamps
  Claude-CLI `.ui.spec.ts`), `HealSpecsInnerAsync` (re-stamps carrying prior fields), and
  `RewriteSpecOnDisk` (push-time — fills in `TcId` on already-stamped spec, does not backfill
  legacy specs). New `QaMetaHeaderTests.cs` (7 tests: round-trip, upsert-prepend,
  upsert-replace-in-place, legacy → null not throw, strip-is-noop, hash stability).
  **AC:** B/T; fresh API and UI specs both start with a valid `// @qa-meta` line; legacy specs parse
  as `null`; 170/170 tests pass. ✔

- [x] **SB-302** — `ScriptBoardRun` entity (`Core/Models/ScriptBoardRun.cs`) + `QaDbContext` mapping +
  migration `AddScriptBoardRuns` (both SQLite + Npgsql providers); `IScriptInventoryService` /
  `ScriptInventoryService` (`Infrastructure/ScriptsBoard/`): scans `{DataDirectory}/scripts/**/*.spec.ts`
  (API) + `{PlaywrightRoot}/generated/ui/**/*.ui.spec.ts` (UI, skipped gracefully when `PlaywrightRoot`
  unset), parses `@qa-meta`, cross-references `ApiTestDraft` rows by normalized absolute path,
  classifies into `linked` / `no_meta` / `orphan_disk` / `orphan_db`. Registered scoped.
  New `ScriptInventoryServiceTests.cs` (6 tests, one per reconcile state + two `PlaywrightRoot` edge
  cases).
  **AC:** B/T; all 4 reconcile states covered; UI-root scan no-ops gracefully; 176/176 tests pass. ✔

- [x] **SB-303** — `PlaywrightRunSpec.Headed` / `.Workers` (additive, zero change for QA Runs);
  `ScriptBoardRun.Module` column (migration `AddScriptBoardRunModule`, both providers);
  `IScriptBoardExecutionService` / `ScriptBoardExecutionService` (`Infrastructure/ScriptsBoard/`):
  filter → resolve specs (explicit `SpecFilePaths` wins; else inventory filter excluding `orphan_db`) →
  `ArgumentException` on zero matches → `Queued` `ScriptBoardRun`; `ExecuteAsync`: `Running` →
  `PlaywrightRunSpec { RunId = -runId }` → `IPlaywrightRunner.RunAsync` → update row → `Passed` /
  `Failed` / `Cancelled` / `Error`. New `ScriptsBoardContracts.cs` + `ScriptsBoardController`
  (`GET inventory`, `POST runs`, `GET runs`, `GET runs/{id}`, `GET runs/{id}/stream`,
  `POST runs/{id}/cancel`) — background-task-with-own-scope mirrors `QaRunsController.Start`;
  `RunCancellationRegistry` reused (same singleton, negative key). DI: scoped service.
  New `ScriptBoardExecutionServiceTests.cs` (6 tests).
  **AC:** B/T; `POST /api/scripts-board/runs` round-trips end-to-end; negative `RunId` non-colliding;
  182/182 tests pass. ✔

---

## Active tasks (build in order from here)

- [ ] **SB-304** — Extract `PlaywrightRunner`'s config-writing + result-parsing into shared
  `internal static` helpers (`PlaywrightConfigWriter`, `PlaywrightResultParser`) — pure refactor,
  existing `PlaywrightRunnerTests` must stay green. Add `EventKinds.Term = "term"` (live-only, NOT
  in `NarrativeKinds`). New `IScriptsBoardRunner` / `ScriptsBoardRunner` — separate raw-byte-stream
  spawn path (`FORCE_COLOR=1`, reads `StandardOutput.BaseStream` as raw byte chunks → publishes
  `EventKinds.Term` events via `IEventBus`) used **only** by Scripts Board; `PlaywrightRunner.SpawnAsync`
  is never touched. Update `ScriptBoardExecutionService.ExecuteAsync` to call
  `IScriptsBoardRunner.RunWithLiveTermAsync` instead of `IPlaywrightRunner.RunAsync`. Register as
  singleton. New `ScriptsBoardRunnerTests.cs` (3 tests: env vars + FORCE_COLOR set; short-lived
  process emits at least one `term` event; extracted helpers produce same config as original).
  **AC:** B/T; existing `PlaywrightRunnerTests` unchanged and green (proves extraction didn't regress
  QA Runs); manual raw-SSE `curl -N .../stream` shows colored ANSI `term` events.

- [ ] **SB-305** — `IPlaywrightRunner.GetHistoryFilePath(string? module, int? pipelineId)` public
  wrapper (expose existing private path logic); `IScriptTrendsService` / `ScriptTrendsService`
  (`GetTrendsAsync` → `TrendPoint[]` from `history.jsonl`; `GetFlakinessAsync` → `FlakinessItem[]`
  from per-test pass/fail transitions); two controller endpoints `GET /api/scripts-board/trends?module=`
  + `GET /api/scripts-board/flakiness?module=`; scoped DI. New `ScriptTrendsServiceTests.cs` (4 tests:
  seeded history → ordered TrendPoints; flip detection → correct FlipRate; missing file → empty no
  throw; single-run spec excluded from flakiness).
  **AC:** B/T; trends/flakiness populate after ≥2 Scripts Board runs against the same module (manual
  check against story 177540 — run twice; if ADO PAT expired flag live check as pending, unit tests
  suffice for `[x]`).

- [ ] **SB-306** — Frontend module scaffold: `paths.scriptsBoard` in `routes/paths.ts`;
  `client/src/api/scriptsBoard.ts` (new — Axios wrappers: `getInventory`, `startRun`, `listRuns`,
  `getRun`, `cancelRun`, `getTrends`, `getFlakiness`, `runStreamUrl`; all DTOs);
  `client/src/pages/ScriptsBoardPage.tsx` (new — filter bar + inventory grid w/ reconcile-state chips
  + per-spec "Run this" quick action + spec-code viewer drawer + runs history table + run drawer with
  `TermConsole` stub + Trends/Flakiness tab stubs); nav item (`TerminalIcon`) + route in `Layout.tsx`.
  **AC:** F (`npm run build` + `npm run lint`); page renders inventory + empty runs list against a
  live backend with zero Scripts Board runs yet.

- [ ] **SB-307** — `npm install xterm xterm-addon-fit` (in `client/`); new
  `client/src/components/TermConsole.tsx` (raw-ANSI passthrough: `xterm.Terminal` + `FitAddon` + SSE
  subscription for `kind === 'term'` events → `term.write(chunk)`); replace stub in Scripts Board run
  drawer with `<TermConsole streamUrl={runStreamUrl(run.id)} running={run.status === 'Running'} />`.
  **AC:** F; manual check — Scripts Board run shows live colored Playwright output in the xterm panel
  (describe or screenshot in `progress.md`).

- [ ] **SB-308** — Trends/Flakiness tab UI: check `client/package.json` for an existing chart
  library before adding one; render `GetTrendsAsync` data (passed/failed over time + duration bar);
  flakiness table sorted by `flipRate` desc with `flipRate > 0.2` highlighted.
  **AC:** F; Trends tab renders real data after ≥2 Scripts Board runs on the same module.

- [ ] **SB-309** — Full `dotnet test` + `npm run build`/`lint` sweep; end-to-end manual pass against
  story 177540 (scan → push → inventory `linked` w/ `@qa-meta` → filter by module → run → live
  terminal → run again → trends/flakiness show 2 data points → hand-added stray spec flagged
  `orphan_disk`); feature doc `docs/features/scripts-board.md` (what/why, data model, data flow, key
  files, config, limitations) + `docs/changelog/` entry; PRD all `[x]`. **AC:** all green; docs
  written; `progress.md` final entry: **SCRIPTS BOARD COMPLETE.**
