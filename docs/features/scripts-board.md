# Feature: Scripts Board

**Status:** Implemented — v1 (SB-301..309) + **Power Console** (SB-401..407)  
**Ralph loop:** `docs/progress/RalphLoop/scripts-board/`  
**Design lock:** `docs/progress/RalphLoop/scripts-board/power-console.md`

---

## What it is

The **sole Playwright launch surface** — a power console over inventory reconcile, ad-hoc
execution, live SSE terminal, and run profile selection.

**Product split:** Scripts Board = launch. **QA Runs** = history / triage / reports for recorded
`TestRun`s (pipeline + legacy) — no Start form on that page.

Layout DNA from [playwright-dashboard](https://github.com/alaintruong77/playwright-dashboard);
output stays **in-app TermConsole** (no PowerShell windows).

## UI layout (Power Console)

```
Header: Ready/Running · totals · Refresh · Open fullscreen
Modules | Specs (filters · checkboxes · Run) | Launch (profile · headed · workers)
Live terminal (full width)
Runs rail (cancel · re-run · report)
```

- **Fullscreen:** `/scripts-board/fullscreen` via `window.open` (no app chrome).
- **Dropdowns:** empty filter value displays the label **All** (`AllSelect`).
- **Spec preview:** click a row → drawer with reconcile chips, ADO TC link, `@qa-meta`, source.

## Data flow

```
Disk specs (+ @qa-meta)
  → ScriptInventoryService reconcile (linked / no_meta / orphan_disk / orphan_db)
  → UI: filter + select → POST /api/scripts-board/runs { profile?, headed, workers?, … }
  → ScriptBoardExecutionService → PlaywrightRunSpec.Profile → ScriptsBoardRunner
  → SSE scripts-board-run:{id} → TermConsole (xterm)
```

**RunId namespace:** Scripts Board uses `RunId = -scriptBoardRun.Id` so disk artifacts under
`{runsDir}/-{id}/` never collide with QA Runs positive IDs.

## Key endpoints

| Method | Path | Notes |
|--------|------|--------|
| GET | `/api/scripts-board/inventory` | Reconciled inventory |
| GET | `/api/scripts-board/spec-content?path=` | Read-only spec body (inventory-gated) |
| POST | `/api/scripts-board/runs` | Start ad-hoc run (202 `{ runId }`); body includes `profile` |
| GET | `/api/scripts-board/runs` | Paginated history |
| GET | `/api/scripts-board/runs/{id}` | Detail |
| GET | `/api/scripts-board/runs/{id}/stream` | SSE (incl. `term` chunks) |
| GET | `/api/scripts-board/runs/{id}/report/**` | Playwright/Allure HTML (+ `?dl=true` zip) |
| POST | `/api/scripts-board/runs/{id}/cancel` | Cancel via `-runId` registry key |

## Key files

| Area | Path |
|------|------|
| Meta | `Core/Models/QaMeta.cs` |
| Inventory | `Infrastructure/ScriptsBoard/ScriptInventoryService.cs` |
| Execution | `ScriptBoardExecutionService.cs`, `ScriptsBoardRunner.cs` |
| API | `Api/Controllers/ScriptsBoardController.cs` |
| SPA | `client/src/pages/scriptsBoard/*`, `components/TermConsole.tsx`, `api/scriptsBoard.ts` |

## Limitations

- UI specs scanned from `{PlaywrightRoot}/generated/ui/**` (not relocated into `scripts/{module}/ui/`).
- Live SSE needs a logged-in SPA JWT (`apiUrl` appends `access_token` for EventSource).
- ADO Test Run creation is out of scope — board links out to existing TC IDs via inventory only.
