# Technical Design Document — Scripts Board Module

| **Project Title**    | Scripts Board — Inventory · Execution · Trends · Flakiness |
| -------------------- | ----------------------------------------------------------- |
| **Document Version** | 1.0                                                         |
| **Date**             | July 30, 2026                                               |
| **Prepared By**      | Mahmood Ahmad (BA / QA) + Claude                            |
| **Companion docs**   | `Ralph loop.md` (ruleset) · `blueprint.md` (file-level guide) · `prd.md` (task backlog) · `progress.md` (execution memory) |
| **Parent design**    | `docs/progress/RalphLoop/qa-loop-v2/solution.md` — Slab 3  |

# Revision History

| **Version** | **Date**      | **Author**       | **Description of Change** |
| ----------- | ------------- | ---------------- | ------------------------- |
| 1.0         | July 30, 2026 | Mahmood + Claude | Extracted and expanded from qa-loop-v2 solution.md Slab 3. Locked design; three tasks already verified (SB-301/302/303). |

---

# Purpose

QA Loop v2 (Slabs 1 and 2) gave QA a two-phase authoring-and-execution run that produces both API and
UI Playwright specs, pushes ADO Test Cases, and links test results. After a run there may be dozens of
generated `.spec.ts` files scattered under `backend/data/scripts/` (API) and `{PlaywrightRoot}/generated/ui/`
(UI). There is currently no place to:

- **Browse** the full inventory of generated specs across modules, levels, and buckets.
- **Run** an arbitrary selection (single spec, a bucket, a whole module, all levels) ad-hoc without
  starting a new QA-Loop session.
- **Watch** execution live with real terminal output (ANSI colours, Playwright's native reporter lines).
- **Track** history, trends, and flakiness across repeated runs of the same spec set.

**Scripts Board** is a new top-level module that fills this gap. It coexists with QA Runs (which
remains the session-scoped + pipeline-driven run + triage surface) and shares the underlying
`PlaywrightRunner` + report backend. Scripts Board owns the **framework-wide** view.

---

# Scope

## In scope (v1)

1. **`@qa-meta` header convention** — a `// @qa-meta {…}` JSON comment baked into line 1 of every
   generated spec so the board can reconcile disk↔DB and never lose track of a spec's identity.
2. **Script inventory + reconcile** — scan both spec roots, parse `@qa-meta`, cross-reference DB
   draft rows, produce `linked` / `orphan_disk` / `orphan_db` / `no_meta` states.
3. **Flexible execution** — run by single spec / bucket / level / type / module / tag / "all"; headed
   vs headless; configurable worker count. Spawned through `PlaywrightRunner` (reused unchanged).
4. **Live xterm.js terminal** — a raw-ANSI streaming path (`FORCE_COLOR=1`) over a new `term` SSE
   event kind into an `xterm.js` terminal component in the browser.
5. **History + trends** — `TrendPoint`s from Allure `history.jsonl` (pass/fail/duration over time).
6. **Flakiness detection** — pass-rate / retry deltas across runs (flip-rate per test title).
7. **Frontend module** — 4-file add (`paths.ts`, `api/scriptsBoard.ts`, `pages/ScriptsBoardPage.tsx`,
   `layout/Layout.tsx`) following the Schema Explorer / QaRunsPage archetype.

## Out of scope (v1 follow-ups)

- ADO Test Run creation from Scripts Board ad-hoc runs (board links out to existing TC IDs via the
  inventory join; it doesn't create new ADO runs).
- Relocating Slab-2 UI scripts from `{PlaywrightRoot}/generated/ui/…` to `scripts/{module}/ui/…`
  (the scanner reads both roots as-is; relocation is a follow-up).
- Run/artifact retention policy.
- Page-Object layer (locators-as-consts v1 is from Slab 2; PO layer is a follow-up).

---

# What already exists (grounding)

## Prerequisites (Slabs 1 + 2 — COMPLETE)

- **`@qa-meta` header** (`QaMeta`/`QaMetaHeader` — `Core/Models/QaMeta.cs`): `Build`/`TryParse`/
  `Upsert`/`ComputeHash`; baked into `QaLoopOrchestrator.PersistDraft`, `RefineDraftsInnerAsync`,
  `GenerateUiScriptsInnerAsync`, `HealSpecsInnerAsync`, and `RewriteSpecOnDisk`. *(SB-301 — DONE)*
- **`ScriptBoardRun` entity + migration** (`Core/Models/ScriptBoardRun.cs`); `IScriptInventoryService` /
  `ScriptInventoryService` scanning both roots, classifying all four reconcile states.
  *(SB-302 — DONE)*
- **Execution service + controller**: `PlaywrightRunSpec.Headed`/`Workers`; `IScriptBoardExecutionService`/
  `ScriptBoardExecutionService` (filter → resolve specs → `ScriptBoardRun` row → `PlaywrightRunner.RunAsync`
  with **`RunId = -runId`** negative namespace); `ScriptsBoardController` (`GET inventory`, `POST runs`,
  `GET runs`, `GET runs/{id}`, `GET runs/{id}/stream`, `POST runs/{id}/cancel`). *(SB-303 — DONE)*

## Existing infrastructure (shared)

- **`PlaywrightRunner`** (`Infrastructure/QaRuns/PlaywrightRunner.cs`): `RunAsync(PlaywrightRunSpec,
  eventKey, ct)` — line-buffered via `BeginOutputReadLine()`. Reused unchanged for Scripts Board ad-hoc
  runs (no session required — `SpecFilePaths` + `Module` are enough).
- **`RunCancellationRegistry`** (singleton, int-keyed dictionary): reused as-is; Scripts Board uses
  negative run IDs (`-runId`), zero changes needed.
- **`IEventBus` + SSE**: `SseWriter.StreamAsync` handles the SSE fan-out; `ConsoleEvent.Kind`
  discriminates event types (`phase`/`system`/`result`/`error`/`token`). Adding `term` is purely
  additive.
- **`QaDbContext`**: `ApiTestDraft` rows carry `Level`, `SpecFilePath`, `DraftKind`, `Bucket`,
  `Tags`, `AdoTestCaseId`, `Status` — the DB identity source for the inventory reconcile.
- **Allure history**: file-based at
  `{DataDirectory}/allure-history/{module|_all}/{pipelineId|manual}/history.jsonl`; keyed by
  module+pipeline; Scripts Board ad-hoc runs land under `.../manual/` (comparable to QA-Runs manual
  runs for the same module).
- **`QaRunsPage.tsx`**: the list+drawer archetype Scripts Board mirrors.

---

# Component designs

## SB-304 — Raw-ANSI live terminal stream

### Problem
`PlaywrightRunner.SpawnAsync` uses `Process.BeginOutputReadLine()` (line-buffered events). This is the
golden-tested QA-Runs path; it must not be touched. Scripts Board needs raw ANSI (colors, progress
bars) which requires reading `StandardOutput.BaseStream` as raw bytes.

### Solution
A **separate, additive** spawn path in a new `ScriptsBoardRunner`:
- Extract `PlaywrightRunner`'s private config-writing and result-parsing into `internal static` helper
  classes (`PlaywrightConfigWriter`, `PlaywrightResultParser`) — pure refactor, existing
  `PlaywrightRunnerTests` prove no behavior change.
- `ScriptsBoardRunner.RunWithLiveTermAsync` writes config via the shared helpers, then spawns
  `npx playwright test` with `FORCE_COLOR=1` added, reads `StandardOutput.BaseStream` as raw byte
  chunks, publishes each chunk as a `ConsoleEvent { Kind = "term", Text = <utf8 chunk> }` on the bus.
- `ScriptBoardExecutionService` calls `IScriptsBoardRunner.RunWithLiveTermAsync` instead of
  `IPlaywrightRunner.RunAsync`.
- New `EventKinds.Term = "term"` constant (additive; deliberately excluded from `Narrative` since raw
  ANSI dumps are live-only, like `Token`/`Thinking`).

### Key invariant
`PlaywrightRunner.SpawnAsync` is **never modified**. QA Runs golden tests stay green by construction.

---

## SB-305 — Trends + flakiness service

### Data source
Allure `history.jsonl` at `{DataDirectory}/allure-history/{module|_all}/manual/history.jsonl`.
Scripts Board ad-hoc runs (no `TriggerPipelineId`) land under the `manual/` bucket automatically.

### Trends
`GetTrendsAsync(module?)` → reads+parses `history.jsonl` → `TrendPoint[]` (timestamp, passed, failed,
durationMs). Returns `[]` when the file doesn't exist (no runs yet, not an error).

### Flakiness
`GetFlakinessAsync(module?)` → for specs with ≥2 runs: count pass→fail / fail→pass transitions per
test title from the Allure per-test history → `FlakinessItem` (spec title, runs, flips, flipRate).
FlipRate = flips / (runs − 1).

### Exposure
`PlaywrightRunner.GetHistoryFilePath(module, pipelineId)` already computes the path (private field
`HistoryFile`); expose as a public method on `IPlaywrightRunner` so the trends service doesn't need
write access.

---

## SB-306 — Frontend module scaffold

### Four-file add (same pattern as Schema Explorer)

| File | Purpose |
| ---- | ------- |
| `client/src/routes/paths.ts` | Add `scriptsBoard: '/scripts-board'` |
| `client/src/api/scriptsBoard.ts` | Axios wrappers for all Scripts Board endpoints |
| `client/src/pages/ScriptsBoardPage.tsx` | Main page — filter bar + inventory grid + runs table + tabs |
| `client/src/layout/Layout.tsx` | Nav item + route registration |

### Page structure
- **Filter bar**: module / level / type / bucket / tag dropdowns (sourced from inventory); headed
  toggle + workers input; **Run selection** button → `POST /api/scripts-board/runs`.
- **Inventory grid**: one row per `ScriptInventoryItem`; columns: path, module, type, level, bucket,
  ADO TC link (if `AdoTestCaseId`), reconcile-state chip (`linked` / `orphan-disk` / `orphan-db` /
  `no-meta`). Per-row quick "Run this one" action; click-through drawer with raw spec viewer.
- **Runs history table**: `ScriptBoardRun` rows; click → drawer with Console tab (QLV2-307 terminal) +
  pass/fail/skipped + report/junit links.
- **Trends/Flakiness tab** (SB-308).

---

## SB-307 — xterm.js live terminal component

### Approach
`npm install xterm xterm-addon-fit` (in `client/`).

New `client/src/components/TermConsole.tsx`:
- `useRef` + `xterm.Terminal` + `FitAddon`; mounts into a div, disposes on unmount.
- Subscribes to the run's SSE stream via `hooks/useEventStream.ts`; calls `term.write(event.text)`
  for every `kind === 'term'` event (raw ANSI passthrough — xterm.js renders escape codes natively).

Wired into `ScriptsBoardPage`'s run drawer as the Console tab.

---

## SB-308 — Trends + flakiness UI

- **Trends**: simple line/bar rendering of `GetTrendsAsync` — passed/failed counts + duration over
  time. Prefer a CSS-bar or table approach in v1 to avoid a new chart dependency; if `client/`
  already has a charting library (check `Reports` page), reuse it.
- **Flakiness**: table of `GetFlakinessAsync` results — spec title, runs, flips, flip-rate; sort by
  flip-rate descending; highlight > 20% as "flaky" (e.g. a MUI `Chip color="warning"`).

---

# Cross-cutting invariants

- **Byte-exact ADO parity stays green** — `AdoGoldenTests` must pass; no new ADO write paths in
  Scripts Board (it only links *out* to existing TC IDs via inventory).
- **Background-task-with-own-DI-scope** for every long-running action; SSE via `IEventBus`;
  cancellation via `CancellationRegistry` (same singleton, negative key); concurrency via
  `PipelineConcurrency`.
- **Negative RunId namespace**: `PlaywrightRunner` receives `RunId = -scriptBoardRun.Id` for all
  Scripts Board executions — disk artifacts land in `{runsDir}/-{id}/` — disjoint from `TestRun`
  positive IDs. **Do not change this.**
- **No modification of `PlaywrightRunner.SpawnAsync`** — the separate `ScriptsBoardRunner` path
  prevents any regression to the golden-tested QA-Runs behavior.

---

# Framework disk layout (reference)

```
backend/data/
  scripts/
    {module}/
      contracts/   *.spec.ts
      endpoints/   *.spec.ts
      workflows/   *.spec.ts
      ui/          *.ui.spec.ts     (if relocated in a follow-up; currently under PlaywrightRoot)
  runs/
    {TestRun.Id}/      positive IDs — QA Runs
    -{ScriptBoardRun.Id}/  negative IDs — Scripts Board
  allure-history/
    {module|_all}/
      {pipelineId|manual}/
        history.jsonl

{PlaywrightRoot}/
  generated/
    ui/
      {bucket}/    *.ui.spec.ts     (Slab 2 output — scanned by inventory)
  .claude/agents/  planner.md, generator.md, healer.md
  .mcp.json
  fixtures/
    storageState.json
    auth.setup.ts
```

---

# Verification plan (per task)

| Task | Verification |
| ---- | ------------ |
| SB-301 | `dotnet build`/`dotnet test`; fresh API + UI spec starts with valid `// @qa-meta`. **DONE** |
| SB-302 | `dotnet build`/`dotnet test`; all 4 reconcile states pass. **DONE** |
| SB-303 | `dotnet build`/`dotnet test`; `POST /api/scripts-board/runs` round-trips end-to-end. **DONE** |
| SB-304 | B/T; existing `PlaywrightRunnerTests` unchanged + green; raw-SSE `curl -N .../stream` shows colored `term` events. |
| SB-305 | B/T; trends/flakiness populate after ≥2 runs on the same module (manual check story 177540). |
| SB-306 | F (`npm run build` + `npm run lint`); page renders inventory + empty runs list. |
| SB-307 | F; manual check — Scripts Board run shows live colored Playwright output in xterm panel. |
| SB-308 | F; Trends tab renders real data after ≥2 runs on the same module. |
| SB-309 | Full B/T/F; end-to-end: scan → push → inventory `linked` → run by module → live terminal → run again → trends/flakiness show 2 points → stray spec flagged `orphan_disk`; feature doc + changelog written. **SCRIPTS BOARD COMPLETE.** |

---

# Reference documents

- `docs/progress/RalphLoop/qa-loop-v2/solution.md` — parent three-slab design; Slab 3 section is the
  authoritative source this document expands.
- `docs/progress/RalphLoop/qa-loop-v2/blueprint.md` → "SLAB 3 (detailed)" — file-level guide this
  `blueprint.md` mirrors and keeps in sync.
- `docs/qa-loop-reshape/solution.md` — the API-phase QA Loop design Scripts Board builds on top of.
- `CLAUDE.md` — architecture invariants (DI root, background-task DI scope, byte-exact parity,
  settings two-layer pattern).
- Reference dashboard: `github.com/alaintruong77/playwright-dashboard` (design DNA for Scripts Board;
  Scripts Board adds history/trends/flakiness/filtering that it lacks).
