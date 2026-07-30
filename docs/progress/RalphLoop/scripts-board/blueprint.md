# Blueprint — Scripts Board Module (file-by-file implementation guide)

Tasks `SB-301..SB-309`. SB-301/302/303 are **DONE** (verified); their sections below are kept as
implementation records. The active build starts at **SB-304**.

All work respects the invariants in `solution.md` → "Cross-cutting invariants": byte-exact ADO parity,
negative `RunId` namespace, separate `ScriptsBoardRunner` spawn path (never touch `PlaywrightRunner.SpawnAsync`),
background-task-with-own-DI-scope, additive migrations.

---

## SB-301 — `@qa-meta` header convention  *(DONE — verified 2026-07-29)*

### Files changed
- `backend/src/QaAutomation.Core/Models/QaMeta.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs` (4 call-sites)
- `backend/src/QaAutomation.Tests/QaMetaHeaderTests.cs` *(new, 7 tests)*

### What was built
`QaMeta` sealed record + `QaMetaHeader` static class: `Build` / `TryParse` / `StripHeader` / `Upsert` /
`ComputeHash`. Baked into:
- `PersistDraft` — initial API spec write (Contract/Endpoint/Workflow).
- `RefineDraftsInnerAsync` — rewrite on refine (Slab 1 flow).
- `GenerateUiScriptsInnerAsync` — stamps Claude-CLI-generated `.ui.spec.ts` before persisting.
- `HealSpecsInnerAsync` — re-stamps carrying forward prior header fields, or mints fresh one.
- `RewriteSpecOnDisk` (push-time) — opportunistically fills in `TcId` on an already-stamped spec.

`TryParse` returns `null` (not an error) for legacy/plain specs. 170/170 tests pass.

---

## SB-302 — Data + inventory/reconcile service  *(DONE — verified 2026-07-29)*

### Files changed
- `backend/src/QaAutomation.Core/Models/ScriptBoardRun.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs` (DbSet + entity config)
- `backend/src/QaAutomation.Infrastructure/Migrations/` (AddScriptBoardRuns + Npgsql equivalent)
- `backend/src/QaAutomation.Core/Abstractions/IScriptInventoryService.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/ScriptsBoard/ScriptInventoryService.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs` (scoped registration)
- `backend/src/QaAutomation.Tests/ScriptInventoryServiceTests.cs` *(new, 6 tests)*

### Key decisions
- `ScriptBoardRun` has no `AdoTestRunId`/`TriggerSource` — Scripts Board is ADO-optional by design.
- Inventory reconcile: disk scan (API + UI roots) → `QaMetaHeader.TryParse` → cross-reference
  `ApiTestDraft` by normalized absolute path → four states: `linked` / `no_meta` / `orphan_disk` / `orphan_db`.
- UI root scan skips gracefully when `PlaywrightRoot` is unset.
- 176/176 tests pass.

---

## SB-303 — Execution service + controller  *(DONE — verified 2026-07-29)*

### Files changed
- `backend/src/QaAutomation.Core/Abstractions/IPlaywrightRunner.cs` (`PlaywrightRunSpec.Headed` / `.Workers`)
- `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs` (`WriteRunConfig` branches)
- `backend/src/QaAutomation.Core/Models/ScriptBoardRun.cs` (+ `Module` column)
- `backend/src/QaAutomation.Infrastructure/Migrations/` (AddScriptBoardRunModule, both providers)
- `backend/src/QaAutomation.Core/Abstractions/IScriptBoardExecutionService.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/ScriptsBoard/ScriptBoardExecutionService.cs` *(new)*
- `backend/src/QaAutomation.Core/Contracts/ScriptsBoardContracts.cs` *(new)*
- `backend/src/QaAutomation.Api/Controllers/ScriptsBoardController.cs` *(new)*
- `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs` (scoped registration)
- `backend/src/QaAutomation.Tests/ScriptBoardExecutionServiceTests.cs` *(new, 6 tests)*

### Key decisions
- `RunId = -scriptBoardRun.Id` passed to `PlaywrightRunner` — negative namespace disjoint from `TestRun`.
- `RunCancellationRegistry` reused as-is (same singleton, negative key).
- `ScriptsBoardController.Start` is a direct copy of `QaRunsController.Start`'s `Task.Run` block.
- `Module` resolved at `StartAsync` time (explicit filter → single distinct inventory module → `"_all"`).
- 182/182 tests pass.

---

## SB-304 — Raw-ANSI live terminal stream  *(NEXT — resume here)*

Pattern source: `PlaywrightRunner.SpawnAsync` (`:521-610`); `ConsoleEvent` (`Core/Models/ConsoleEvent.cs`);
`ScriptBoardExecutionService.ExecuteAsync` (the caller to update).

### Step 1 — Add `EventKinds.Term`

**File:** `backend/src/QaAutomation.Core/Models/ConsoleEvent.cs`

Add after the existing kind constants:
```csharp
public const string Term = "term";
// Deliberately excluded from the NarrativeKinds set — live-only, like Token/Thinking.
// Raw ANSI dumps are not useful in a replayed EventLogJson narrative.
```

Do NOT add `Term` to the `NarrativeKinds` array (wherever it is defined — check and confirm).

---

### Step 2 — Extract shared helpers from `PlaywrightRunner`

**File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`
**New file:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightConfigWriter.cs`

This is a **pure refactor** — move code, zero behavior change, existing `PlaywrightRunnerTests` must
stay green.

Extract from `PlaywrightRunner`:
1. **`PlaywrightConfigWriter`** (`internal static class`): the private config-writing logic currently
   in `WriteRunConfig`/`WritePlaywrightConfig` (whichever private method(s) build the `.config.ts`
   temp file + testDir + testMatch glob). Both `PlaywrightRunner` and `ScriptsBoardRunner` will call it.
2. **`PlaywrightResultParser`** (`internal static class`): the private result-parsing logic currently
   reading `junit.xml` + `results.json` after the process exits.

After the extraction:
- `PlaywrightRunner`'s existing methods call the extracted helpers — identical behavior.
- Run `dotnet test --filter "FullyQualifiedName~PlaywrightRunner"` to confirm all existing tests pass.

> **Read `PlaywrightRunner.cs` in full before starting this step** — the exact method names/signatures
> may differ from the blueprint's guess. The goal is extracting config-writing and result-parsing;
> adapt to what you find.

---

### Step 3 — New `IScriptsBoardRunner` + `ScriptsBoardRunner`

**New file:** `backend/src/QaAutomation.Core/Abstractions/IScriptsBoardRunner.cs`

```csharp
public interface IScriptsBoardRunner
{
    Task<PlaywrightRunResult> RunWithLiveTermAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct);
}
```

(Reuse `PlaywrightRunResult` — the same return type `IPlaywrightRunner.RunAsync` already returns, if
one exists; otherwise use the same tuple/struct `PlaywrightRunner` returns internally.)

**New file:** `backend/src/QaAutomation.Infrastructure/ScriptsBoard/ScriptsBoardRunner.cs`

```csharp
internal sealed class ScriptsBoardRunner : IScriptsBoardRunner
{
    // inject: IEventBus, IOptions<QaLoopOptions>, ILogger<ScriptsBoardRunner>
    // (same deps as PlaywrightRunner except no IAdoTestRunService)

    public async Task<PlaywrightRunResult> RunWithLiveTermAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct)
    {
        // 1. Write config via PlaywrightConfigWriter (extracted in Step 2).
        // 2. Build process start info: same npx playwright test args as PlaywrightRunner.
        // 3. Add: psi.Environment["FORCE_COLOR"] = "1"
        //    (on top of the existing PlaywrightRunner.ApplyAutomationEnv call — reuse it as internal).
        // 4. Start process; do NOT call BeginOutputReadLine.
        //    Instead: read StandardOutput.BaseStream + StandardError.BaseStream directly
        //    (e.g. two async Tasks, each reading a byte[4096] buffer in a loop).
        //    For each chunk: UTF8-decode → Pub(eventKey, EventKinds.Term, phase: null, chunk, EventLevels.Info).
        //    Also accumulate a stripped plain-text buffer (strip ANSI via a simple regex
        //    \x1b\[[0-9;]*[a-zA-Z]) for the crash-fallback log.
        // 5. await process.WaitForExitAsync(ct).
        // 6. Parse results via PlaywrightResultParser (extracted in Step 2).
        // 7. Return PlaywrightRunResult with Passed/Failed/Skipped/DurationMs/JunitPath/ReportPath.
    }
}
```

> Read `PlaywrightRunner.SpawnAsync` in full first — mirror its process-start setup, env injection,
> and exit handling. The only differences are: `FORCE_COLOR=1`, raw-byte read instead of
> `BeginOutputReadLine`, and `EventKinds.Term` instead of line-parsed `EventKinds.*`.

---

### Step 4 — `ScriptBoardExecutionService` calls `IScriptsBoardRunner`

**File:** `backend/src/QaAutomation.Infrastructure/ScriptsBoard/ScriptBoardExecutionService.cs`

In `ExecuteAsync`: replace the `IPlaywrightRunner.RunAsync(…)` call with
`IScriptsBoardRunner.RunWithLiveTermAsync(spec, $"scripts-board-run:{runId}", ct)`.

Update the constructor to inject `IScriptsBoardRunner` (add next to `IPlaywrightRunner` if it was
already there, or replace it if `IPlaywrightRunner` was the only runner dependency — check the
current implementation first).

---

### Step 5 — DI registration

**File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`

```csharp
services.AddSingleton<IScriptsBoardRunner, ScriptsBoardRunner>();
```

(Singleton mirrors `PlaywrightRunner`'s registration.)

---

### Step 6 — Tests

**New file:** `backend/src/QaAutomation.Tests/ScriptsBoardRunnerTests.cs`

Tests:
1. `ApplyAutomationEnv_and_FORCE_COLOR_are_both_set`: build a `ProcessStartInfo` via the runner's
   internal helper and assert both the standard automation env vars AND `FORCE_COLOR=1` are set.
2. `RunWithLiveTermAsync_short_lived_process_emits_Term_events`: start a very short process
   (`cmd /c echo hello` on Windows) through the runner; capture events published to a stub
   `IEventBus`; assert at least one `Kind == EventKinds.Term` event was published and the return
   value has no exception.
3. `Extracted_PlaywrightConfigWriter_produces_same_config_as_PlaywrightRunner`: call both the
   extracted helper and the original (if still accessible) with the same `PlaywrightRunSpec` and
   assert the written config file content is identical. (Skip this test if the extraction
   completely replaces the original code with no duplication.)

Ensure all existing `PlaywrightRunnerTests` still pass (proves the extraction didn't regress QA Runs).

---

### SB-304 acceptance criteria

- `dotnet build` clean (0 warnings/errors).
- `dotnet test` green — all existing `PlaywrightRunnerTests` pass unchanged.
- New `ScriptsBoardRunnerTests` pass.
- Manual check: `curl -N http://localhost:5250/api/scripts-board/runs/{id}/stream` after starting a
  Scripts Board run shows raw `term` SSE events with colored ANSI text before the frontend terminal
  component exists (SB-307 adds the UI).

---

## SB-305 — Trends + flakiness service

### Step 1 — Expose history file path

**File:** `backend/src/QaAutomation.Core/Abstractions/IPlaywrightRunner.cs`

Add to the interface:
```csharp
string GetHistoryFilePath(string? module, int? pipelineId);
```

**File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`

Expose the private `HistoryFile`/`GetHistoryPath` logic as the public method implementing the above.
(Read the file first — find the exact private field/method that computes
`{DataDirectory}/allure-history/{module|_all}/{pipelineId|manual}/history.jsonl`.)

---

### Step 2 — `IScriptTrendsService`

**New file:** `backend/src/QaAutomation.Core/Abstractions/IScriptTrendsService.cs`

```csharp
public sealed record TrendPoint(DateTime Ts, int Passed, int Failed, long DurationMs);
public sealed record FlakinessItem(string SpecTitle, int Runs, int Flips, double FlipRate);

public interface IScriptTrendsService
{
    Task<IReadOnlyList<TrendPoint>> GetTrendsAsync(string? module, CancellationToken ct = default);
    Task<IReadOnlyList<FlakinessItem>> GetFlakinessAsync(string? module, CancellationToken ct = default);
}
```

---

### Step 3 — `ScriptTrendsService`

**New file:** `backend/src/QaAutomation.Infrastructure/ScriptsBoard/ScriptTrendsService.cs`

- `GetTrendsAsync`: call `IPlaywrightRunner.GetHistoryFilePath(module, null)` (the `null` pipelineId
  maps to the `"manual"` bucket — ad-hoc Scripts Board runs land there by default). Read+parse
  `history.jsonl` (Allure NDJSON). Map each entry → `TrendPoint(Ts, Passed, Failed, DurationMs)`.
  Return `[]` if the file does not exist (no runs yet — not an error, not a log statement).
- `GetFlakinessAsync`: read the same history file. Group per-test-title entries across run timestamps.
  For specs appearing in ≥2 runs: count result transitions (Pass→Fail or Fail→Pass) →
  `FlakinessItem(title, runs, flips, flipRate = flips / (runs - 1))`.

> Inspect the actual Allure `history.jsonl` schema produced by the existing runner before writing
> the parser — the field names may be `passed`/`failed`/`broken`/`skipped` (Allure 2 format) rather
> than a flat pass/fail. Adapt accordingly and document any schema notes here.

---

### Step 4 — Controller endpoints

**File:** `backend/src/QaAutomation.Api/Controllers/ScriptsBoardController.cs`

Add two new endpoints:
```
GET /api/scripts-board/trends?module=     → IScriptTrendsService.GetTrendsAsync(module)  → 200 TrendPoint[]
GET /api/scripts-board/flakiness?module=  → IScriptTrendsService.GetFlakinessAsync(module) → 200 FlakinessItem[]
```

---

### Step 5 — DI + tests

**File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`

```csharp
services.AddScoped<IScriptTrendsService, ScriptTrendsService>();
```

**New file:** `backend/src/QaAutomation.Tests/ScriptTrendsServiceTests.cs`

Tests:
1. `GetTrendsAsync_reads_seeded_history_file_returns_ordered_TrendPoints`: write a temp
   `history.jsonl` with 2 entries; assert 2 `TrendPoint`s returned, ordered by `Ts`.
2. `GetFlakinessAsync_detects_flip_across_3_entries`: seed spec "Foo" with pass/fail/pass across
   3 runs; assert `FlipRate ≈ 2/2 = 1.0` (or whatever the correct formula gives).
3. `GetTrendsAsync_missing_history_file_returns_empty_no_throw`: path doesn't exist → `[]`.
4. `GetFlakinessAsync_single_run_spec_excluded`: spec with only 1 run → not in results.

---

### SB-305 acceptance criteria

- `dotnet build` + `dotnet test` green.
- Trends/flakiness endpoints return real data after ≥2 Scripts Board runs against the same module
  (manual check against story 177540 — run twice, then call both GET endpoints via Swagger or curl).
- If ADO PAT is expired, flag this live check as pending in `progress.md`.

---

## SB-306 — Frontend module scaffold

Pattern source: Schema Explorer (`client/src/pages/SchemaExplorerPage.tsx` + its nav entry in
`Layout.tsx`); `QaRunsPage.tsx` for the list+drawer archetype.

### Step 1 — `client/src/routes/paths.ts`

```typescript
scriptsBoard: '/scripts-board',
```

### Step 2 — `client/src/api/scriptsBoard.ts`  *(new)*

```typescript
import { client } from './client';

// -- types (mirrors ScriptsBoardContracts.cs) --

export interface ScriptInventoryItem {
  specFilePath: string;
  module: string;
  type: string;
  level: string;
  bucket: string | null;
  adoTestCaseId: number | null;
  draftId: number | null;
  tags: string | null;
  reconcileState: 'linked' | 'orphan_disk' | 'orphan_db' | 'no_meta';
}

export interface ScriptBoardRunFilter {
  module?: string;
  level?: string;
  type?: string;
  bucket?: string;
  tag?: string;
  specFilePaths?: string[];
  headed?: boolean;
  workers?: number;
}

export interface ScriptBoardRunDto {
  id: number;
  selectionSummary: string;
  headed: boolean;
  workers: number | null;
  status: string;
  passed: number;
  failed: number;
  skipped: number;
  durationMs: number;
  junitPath: string | null;
  reportPath: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  createdAt: string;
}

export interface TrendPoint {
  ts: string;
  passed: number;
  failed: number;
  durationMs: number;
}

export interface FlakinessItem {
  specTitle: string;
  runs: number;
  flips: number;
  flipRate: number;
}

// -- API calls --
export const getInventory = () =>
  client.get<ScriptInventoryItem[]>('/api/scripts-board/inventory').then(r => r.data);

export const startRun = (filter: ScriptBoardRunFilter) =>
  client.post<{ runId: number }>('/api/scripts-board/runs', filter).then(r => r.data);

export const listRuns = (skip = 0, take = 50) =>
  client.get<ScriptBoardRunDto[]>('/api/scripts-board/runs', { params: { skip, take } }).then(r => r.data);

export const getRun = (id: number) =>
  client.get<ScriptBoardRunDto>(`/api/scripts-board/runs/${id}`).then(r => r.data);

export const cancelRun = (id: number) =>
  client.post(`/api/scripts-board/runs/${id}/cancel`);

export const getTrends = (module?: string) =>
  client.get<TrendPoint[]>('/api/scripts-board/trends', { params: { module } }).then(r => r.data);

export const getFlakiness = (module?: string) =>
  client.get<FlakinessItem[]>('/api/scripts-board/flakiness', { params: { module } }).then(r => r.data);

export const runStreamUrl = (id: number) =>
  `/api/scripts-board/runs/${id}/stream`;
```

### Step 3 — `client/src/pages/ScriptsBoardPage.tsx`  *(new)*

Model on `QaRunsPage.tsx`. Rough structure:

```tsx
// Tabs: Inventory | Runs | Trends
// Inventory tab:
//   FilterBar: module/level/type/bucket/tag <Select>s + headed <Switch> + workers <TextField>
//   + "Run selection" <Button> → startRun(filter) → refetch runs list
//   InventoryGrid: MUI DataGrid or Table
//     columns: specFilePath (truncated), module, type, level, bucket, ADO TC (link), reconcileState (Chip)
//     row click → InventoryDrawer (spec viewer via <pre><code>)
//     row action: "Run this one" → startRun({ specFilePaths: [row.specFilePath] })
// Runs tab:
//   RunsTable: list ScriptBoardRunDto rows
//   row click → RunDrawer: Console tab (TermConsole — SB-307) + stats + report link
// Trends tab (SB-308):
//   TrendsSection: line/bar chart of TrendPoint[]
//   FlakinessTable: FlakinessItem[] sorted by flipRate desc
```

In SB-306 stub out the `TermConsole` placeholder with a simple `<Box>Live terminal (coming in SB-307)</Box>`
so the build stays clean before xterm is installed.

Similarly stub out the Trends/Flakiness tab content with `<Box>Trends (SB-308)</Box>` placeholder.

### Step 4 — `client/src/layout/Layout.tsx`

Read the file first to find the nav items array. Add:
```tsx
{ label: 'Scripts Board', path: paths.scriptsBoard, icon: <TerminalIcon /> }
```
And add the route:
```tsx
<Route path={paths.scriptsBoard} element={<ScriptsBoardPage />} />
```

Import `TerminalIcon` from `@mui/icons-material/Terminal` (already available — MUI is a dependency).

---

### SB-306 acceptance criteria

- `npm run build` + `npm run lint` clean.
- Nav item "Scripts Board" appears in the sidebar; navigating to `/scripts-board` renders the page
  (inventory table visible, runs table empty, no console errors) against a live backend.

---

## SB-307 — xterm.js live terminal component

### Step 1 — Install xterm

```powershell
npm install xterm xterm-addon-fit
```
(Run from `client/`.)

Verify `client/package.json` now lists both packages.

---

### Step 2 — `client/src/components/TermConsole.tsx`  *(new)*

```tsx
import { useEffect, useRef } from 'react';
import { Terminal } from 'xterm';
import { FitAddon } from 'xterm-addon-fit';
import 'xterm/css/xterm.css';

interface Props {
  streamUrl: string;
  running: boolean;   // pass false when run is complete to close EventSource
}

export function TermConsole({ streamUrl, running }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);

  useEffect(() => {
    const term = new Terminal({ convertEol: true });
    const fit = new FitAddon();
    term.loadAddon(fit);
    if (containerRef.current) {
      term.open(containerRef.current);
      fit.fit();
    }
    termRef.current = term;
    fitRef.current = fit;
    return () => { term.dispose(); };
  }, []);

  useEffect(() => {
    if (!running) return;
    const es = new EventSource(streamUrl);
    es.onmessage = (e) => {
      try {
        const event = JSON.parse(e.data) as { kind: string; text: string };
        if (event.kind === 'term' && termRef.current) {
          termRef.current.write(event.text);
        }
      } catch { /* ignore non-JSON */ }
    };
    return () => { es.close(); };
  }, [streamUrl, running]);

  return (
    <div
      ref={containerRef}
      style={{ width: '100%', height: 400, background: '#1e1e1e', borderRadius: 4 }}
    />
  );
}
```

> Adapt to the project's actual `useEventStream` hook if one exists — check `hooks/useEventStream.ts`
> and reuse it rather than creating a raw `EventSource` if it already handles reconnect/close.
> If reusing the hook, extend its `ConsoleEvent` union type to include `kind: 'term'`.

---

### Step 3 — Wire into `ScriptsBoardPage`

In the RunDrawer's Console tab, replace the `<Box>Live terminal (coming in SB-307)</Box>` stub with:

```tsx
<TermConsole
  streamUrl={runStreamUrl(selectedRun.id)}
  running={selectedRun.status === 'Running'}
/>
```

---

### SB-307 acceptance criteria

- `npm run build` + `npm run lint` clean.
- Manual check: start a Scripts Board run against a local spec (or story 177540's generated specs
  if the ADO PAT is valid); open the run drawer in the browser; the terminal panel shows live colored
  Playwright output as the run progresses. Screenshot or description in `progress.md`.

---

## SB-308 — Trends + flakiness UI

### Step 1 — Check existing chart library

Before adding a new dependency, check `client/package.json` for any existing charting library
(e.g. `recharts`, `chart.js`, `@mui/x-charts`). If one exists, use it. If not, implement with
CSS progress-bar divs or a simple SVG bar — avoid adding a new charting dependency for v1.

---

### Step 2 — Replace Trends tab placeholder in `ScriptsBoardPage.tsx`

Replace the `<Box>Trends (SB-308)</Box>` stub with:

```tsx
// TrendsSection:
//   useQuery(['sb-trends', moduleFilter], () => getTrends(moduleFilter))
//   Render a simple bar for each TrendPoint: date label + pass/fail counts + duration
//   Or a line chart if a charting library is already available.

// FlakinessTable:
//   useQuery(['sb-flakiness', moduleFilter], () => getFlakiness(moduleFilter))
//   MUI Table: SpecTitle | Runs | Flips | FlipRate%
//   sort by flipRate desc
//   highlight rows where flipRate > 0.2 with a warning color or Chip
```

---

### SB-308 acceptance criteria

- `npm run build` + `npm run lint` clean.
- Trends tab renders real data after ≥2 Scripts Board runs against the same module (verify manually
  against story 177540 — run twice, then check the Trends tab).

---

## SB-309 — Tests + docs (close-out)

### Step 1 — Full test sweep

```powershell
dotnet test   # from backend/
npm run build  # from client/
npm run lint   # from client/
```

All must be green. Fix any test or lint regressions introduced by SB-304..308.

---

### Step 2 — End-to-end manual verification (story 177540)

Walk the following sequence and note results in `progress.md`:
1. **Scan + push** — run a QA Loop scan on any module; approve drafts; push (creates ADO TCs +
   stamps `@qa-meta` in the generated `.spec.ts` files on disk).
2. **Inventory** — open Scripts Board; confirm specs appear with reconcile state `linked` and a
   valid `@qa-meta` header visible in the spec viewer.
3. **Run by module** — use the filter bar to select the scanned module; click "Run selection";
   open the run drawer; confirm the live xterm.js terminal shows colored Playwright output.
4. **Run again** — run the same module a second time.
5. **Trends/Flakiness** — open the Trends tab; confirm at least 2 `TrendPoint`s appear; check the
   Flakiness table (a flaky spec would need genuine instability — confirm it renders even if empty).
6. **Stray spec** — hand-add a `.spec.ts` file to `backend/data/scripts/{module}/contracts/stray.spec.ts`
   (no `@qa-meta` header, no matching DB draft row); refresh the inventory; confirm it appears as
   `orphan_disk`.

Log each step's outcome (pass / partial / blocked-by-PAT) in `progress.md`.

---

### Step 3 — Feature doc + changelog

**New file:** `docs/features/scripts-board.md`

Structure (same shape as `docs/features/qa-loop-ui-phase.md`):
- What / Why (problem + solution summary)
- Data model (`ScriptBoardRun`, `ScriptInventoryItem`, `QaMeta`)
- Data flow (scan → inventory reconcile → filter → execute → live terminal → allure history → trends)
- Key files (backend + frontend, listed by layer)
- Configuration (any new settings keys, e.g. `PlaywrightRoot`)
- Limitations / known follow-ups (v1 gaps: ADO Test Run creation, PO layer, spec relocation, etc.)

**New file:** `docs/changelog/YYYY-MM-DD-scripts-board.md` (use the actual date of completion).

---

### Step 4 — Flip all PRD tasks

Flip any remaining `[ ]` → `[x]` in `prd.md`. Append **SCRIPTS BOARD COMPLETE** to `progress.md`.

---

### SB-309 acceptance criteria

- All `dotnet test` + `npm run build` + `npm run lint` green.
- End-to-end manual pass documented in `progress.md`.
- `docs/features/scripts-board.md` + `docs/changelog/` entry written and accurate.
- All SB-3## tasks `[x]` in `prd.md`.
