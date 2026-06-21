# Manual Verification & Open Issues

Living doc for the .NET backend (`dotnet/`) migration. Tracks what has been verified working,
what still needs **manual** verification, and known issues to work on. Update as items are
confirmed or fixed.

_Last updated: 2026-06-21._

---

## Repository layout (renamed 2026-06-21)

- **`backend/`** — the .NET backend (was `dotnet/`). Owns the live data at **`backend/data/qa.db`** (the DB, KB uploads, and report HTML all live under `backend/data/`).
- **`legacy-backend/`** — **REMOVED 2026-06-21.** The original Python FastAPI backend has been retired
  (the .NET `backend/` is now the sole backend). Recover from git history if its source — or a Python
  parity reference for a future golden-diff — is ever needed. Any "run the Python backend" step in the
  open issues below is now obsolete.
- **`client/`** — React frontend (unchanged). Vite dev proxy `/api → http://localhost:5250`.

## Error logging (free, DB-only)

Errors + warnings from both ends are persisted to the **`ErrorLogs`** table in `backend/data/qa.db`. No external service, no viewer UI — query the table directly to debug.

- **Backend:** a `DbLogger` provider captures all `Warning`/`Error`/`Critical` `ILogger` output app-wide (e.g. the orchestrator's "Pipeline failed … Claude CLI exited 1"), writing async via a background queue. EF Core's own log categories are excluded to avoid noise/recursion.
- **Frontend:** `client/src/logger.ts` captures uncaught errors, unhandled rejections, React render errors (`ErrorBoundary`), and failed axios calls (4xx → warning, 5xx/network → error), batched to **`POST /api/logs`** (stored with `Source="frontend"`).
- **Query example:** `python -c "import sqlite3;c=sqlite3.connect(r'backend\data\qa.db');[print(r) for r in c.execute('select CreatedAt,Source,Level,Category,Message from ErrorLogs order by Id desc limit 50')]"`

---

## Decisions in force

- **LLM provider: stay on the `claude` CLI** (provider `ClaudeCli`), which is the same approach as
  the Python backend (`backend/app/services/llm/claude_cli_client.py`). This uses the local Claude
  Pro/Max subscription (no API key, no per-run cost). The Python approach "was working" and will be
  re-verified manually — see Open Issue #1.
- **Not adopting the Anthropic API provider for now.** It is the more stable path for large
  generations but (a) costs ~$0.15–$0.60 per ticket on `claude-sonnet-4-6` ($3/$15 per 1M tokens)
  and (b) defaults to `max_tokens: 4096`, which would truncate large Tester outputs unless
  `ANTHROPIC_MAX_TOKENS` is raised. Revisit only if the CLI path proves unworkable.

---

## Verified working (2026-06-21, automated this session)

| Area | Result |
|------|--------|
| Backend API | Up on http://localhost:5250 ; Swagger 200 |
| Frontend (Vite) | Up on http://localhost:5173 ; 200 |
| Database | SQLite `backend/data/qa.db` adopted (Alembic baseline), FTS5 `doc_chunks` created, scheduler started |
| ADO read | `GET /api/diagnostics/workitem/171740` → 200 (live `dev.azure.com/your-org`) |
| LLM ping | `GET /api/diagnostics/llm` → 200, provider `ClaudeCli` replied `OK` |
| Diagnostics status | ADO PAT ✔, Anthropic key ✔, Azure OpenAI ✔, Claude CLI ✔ (v2.1.104) |
| Author-Tests **Analyst** phase | ✅ produced full analysis on 171740 (44 ACs normalized, business rules, edge/negative cases, permissions, cross-flow, open questions) |

---

## Frontend re-architecture F0–F5 (DONE, `dev` branch)

Re-architected `client/` onto a centralized API + state layer. All steps are behaviour-preserving,
each its own commit, `tsc + vite build` clean, and the whole stack was smoke-tested live against the
.NET backend (see "Runtime verification" below).

- **F0** — Redux Toolkit + React Query providers mounted; `config/env.ts` (`apiUrl`, central `PROJECT_ID`).
- **F1** — every feature calls through one axios instance (`api/client.ts`) via per-feature modules
  (`api/{knowledgebase,settings,diagnostics,reports,authorTests,findBugs,batches}.ts`); shared
  `common/errors.ts`. All transports (axios, SSE `EventSource`, report iframe/download, Settings
  preview, error-logger `fetch`) build URLs from `config/env` → honour `VITE_QA_API`. Legacy
  global-axios interceptor removed. Only `client.ts`/`common/errors.ts` still import `axios`.
- **F2** — polling moved to React Query: `useSessionDetail`/`useSessionsLive`, the Reports list, and
  the Epic-runs list/detail now use `useQuery` + status-gated `refetchInterval` (off when terminal).
  SSE keeps using `useEventStream`.
- **F3** — colour mode is a Redux slice (`app/colorModeSlice`); active LLM provider is React Query
  (server state). Redux = client UI state only; server state stays in React Query.
- **F4** — central route table `routes/AppRoutes.tsx` + `routes/paths.ts` (path builders); nav, page
  routes, and all `navigate()` deep-links source paths from there. (Optional page→`components/<feature>/`
  folder reshuffle was intentionally skipped — cosmetic, high-churn.)
- **F5** — `VITE_QA_API` mechanism documented in `client/.env.example`; empty in dev (Vite proxy),
  set to an absolute origin to point a build at a remote backend.

### Runtime verification (2026-06-21, this session, live browser smoke test)

Backend healthy on :5250 (`ClaudeCli`, PAT ✔). API contracts confirmed to match the `api/*` modules
(author-tests/find-bugs sessions, reports, knowledgebase, batches, settings/provider all 200 with the
expected shapes). Every page rendered with live data and **zero console errors / zero failed
requests**: Settings, Author Tests (list + detail drawer = React Query hooks), Find Bugs, Reports
(87 area paths discovered), Dashboard (aggregates), Knowledgebase. Colour-mode toggle flips palette +
persists to `localStorage`; full-screen session route renders outside the shell.

---

## Open issues — verify / fix manually

### 1. Author-Tests **Tester** phase fails on large tickets — `Claude CLI exited 1 (no stderr)`  ⛔ blocking
- **Symptom:** Running Author-Tests on ticket **171740** (44 ACs), the Analyst succeeds and auto-hands
  off to the Tester; the Tester streams ~28,577 chars of test-case JSON, stalls, then the `claude`
  CLI subprocess exits 1 with empty stderr at ~492s. Session status → `Failed`.
- **Reproducible:** Yes — failed on the initial run and on a Tester-only retry.
- **Not an app bug:** The .NET `ClaudeCliLlmClient` is a faithful port of the Python
  `claude_cli_client.py`; both only capture the `result` string and surface the CLI's exit code
  verbatim. The error path is `AgentOrchestrator` → `TesterAgent.cs:22` → `ClaudeCliLlmClient.cs:200`.
- **Likely cause (unconfirmed):** the CLI subprocess hitting a Pro/Max usage/turn limit or an internal
  limit on a very large single-shot generation. A controlled direct `claude` repro of a large
  generation did **not** reproduce exit-1 (it streamed 539 KB+), so it is **not** a simple
  output-length cap.
- **To verify manually:**
  1. Run the **Python backend's** Tester on 171740 and confirm it completes ("was working"). If it
     also fails, the issue is environmental (CLI/subscription), not the .NET port.
  2. Watch for a Claude Code usage-limit / rate-limit message in the CLI's own output when run
     interactively with the same prompt.
- **Candidate fixes (if reproducible everywhere):** chunk the Tester generation (fewer test cases per
  CLI call / batch the 44 ACs) so no single generation is huge; or switch this agent to the Anthropic
  API with a raised `max_tokens`.
- **Diagnostic note:** the app discards the CLI's stream-json `result` event's `subtype`/`is_error`,
  so "exited 1: (no stderr)" hides *why* the CLI ended. Surfacing the result `subtype` would make this
  class of failure self-explaining — consider as a follow-up improvement.

### 2. Flows — partially verified (2026-06-21)
Verified this session (read/wiring surfaces, no large LLM generation):
- ✅ **Reports HTML builder** — `GET /api/reports/preview` returns a full HTML report from mock data
  (the riskiest ~1500-line B7 component; no LLM/ADO).
- ✅ **List endpoints** — reports / batches / find-bugs sessions / author-tests sessions all 200 with data.
- ✅ **Find-Bugs start → cancel** on 171740 — `{sessionId}` then `{cancelled:true}` (start validates the
  work item, creates the session, kicks off the analyst; cancel works).
- ✅ **Reports full `generate`** — generated a real report end-to-end: **181 tickets** queried from ADO
  (board `Your Project\Your Team`), summarizer + HTML builder produced a **1.17 MB** report, persisted to
  `backend/data/reports/` and served via `GET /api/reports/html/{id}` (200). **B7 verified end-to-end.**
  (Note: pass the *full* area path as the board, e.g. `Your Project\Your Team`, not the bare `Your Team`.)
- ✅ **Error logger proven on real backend errors** — the failed first generate surfaced automatically as a
  `backend/Error` (`ReportService :: Report 2 generation failed`) plus an operational `backend/Warning`
  (`AzureDevOpsClient :: Report enrichment capped at 150 of 181`) in the `ErrorLogs` table.

Still NOT exercised end-to-end (LLM-completion paths + external writes):
- **Push-to-ADO** (`POST .../sessions/{id}/push`) — **deliberately not run**; writes test cases/bugs to
  Azure DevOps. Verify manually against a safe Test Plan/Suite.
- **Batches / Epic runs** — fan-out start not run (each child is an Author-Tests/Find-Bugs run → inherits
  Issue #1's risk).

### 3. Find-Bugs run — deep-dive (2026-06-21)
Exercising Find-Bugs on 171740 surfaced that the **analyst phase works** (Analyzing → AwaitingActual,
~3 min, like the test analyst), but the `/run` (code-verify + bug-writer) phase had bugs **before** it
ever reaches the bug-writer LLM. Two are fixed; one remains:
- ✅ **FIXED — serialization** (commit `c6a8b48`): `SearchCodeAsync` → `SendDocAsync` → `body.ToJsonString(RawJson)`
  threw "must specify a TypeInfoResolver" because `RawJson` had no resolver and the body held a POCO-backed
  `JsonValue`. Added `DefaultJsonTypeInfoResolver`. Verified in-situ: discovery now gets past code search.
- ✅ **FIXED — cancellation misclassification** (commit `5877fab`): a non-registry `OperationCanceledException`
  (e.g. the ADO `HttpClient` 30s timeout = `TaskCanceledException`) was caught by bare
  `catch (OperationCanceledException)` and mislabelled "Cancelled by user", hiding the real error. Guarded
  all 8 catches in both orchestrators with `when (ct.IsCancellationRequested)`. Unit-tested (94 pass);
  **in-situ effect not re-confirmed** (re-running `/run` on a terminal session is a no-op — needs a fresh session).
- ✅ **FIXED — discovery degrade-never-raise** (commit `f554d69`): `DiscoverAnchoredContextAsync` caught only
  `AdoApiError`, so an ADO HTTP timeout aborted the run. Broadened its per-call catches to
  `catch (Exception) when (!ct.IsCancellationRequested) { … }`.
- ✅ **FIXED — code-verify duplicate-key crash** (commit `41b69c7`): pass-2 built a title→case map via
  `ToDictionary`, which throws when two sourced cases (plan + drafts) normalize to the same title. Switched
  to a last-wins dictionary.
- ✅ **VERIFIED (2026-06-21)**: with all four fixes, a fresh run progressed analyst → discovery →
  code-verify pass-1 → pass-2 (`72/195` step-cases) with **no crash**; cancelled cleanly per the
  cap-at-10-then-cancel convention. Cancel still marks "Cancelled by user" correctly (OCE guard intact).
  The bug-writer LLM itself wasn't reached in this run (pass-2 over 195 cases is slow), but the run path is
  healthy — its only remaining risk is the shared Issue #1 large-generation ceiling, mitigated by the
  cap-and-cancel convention.
- Note: re-running `/run` on a Cancelled/terminal session is a no-op; always start a fresh session to test.

---

## Environment & run notes

- **`dotnet` is not on PATH.** Prefix each PowerShell command:
  `$env:Path = "$env:ProgramFiles\dotnet;$env:USERPROFILE\.dotnet\tools;$env:Path"`
- **Smart App Control was turned OFF (2026-06-21)** so freshly-built binaries run. (Before that, SAC
  enforce-mode blocked launching/loading unsigned build output — `0x800711C7`.) Note: SAC Off is
  one-way without a Windows reset.
- **Run the backend:** `dotnet run --project backend\src\QaAutomation.Api` (HTTP profile → :5250).
- **Run the frontend:** from `client/`, `npm run dev` (→ :5173).
- **Default test ticket: 171740** (User Story, "Customer Import via CSV / Excel File…").
- **Local project id in the UI/API is `1`** (frontend `PROJECT_ID = 1`).
- **Configured LLM:** provider `ClaudeCli`, model `claude-sonnet-4-6`, idle timeout 180s, absolute cap
  1800s. CLI at `C:\Users\temp\.local\bin\claude.exe`.

---

## Quick test recipe (Author-Tests on 171740, no ADO writes)

```powershell
$env:Path = "$env:ProgramFiles\dotnet;$env:USERPROFILE\.dotnet\tools;$env:Path"
# 1. Diagnostics
Invoke-WebRequest http://localhost:5250/api/diagnostics -UseBasicParsing
Invoke-WebRequest http://localhost:5250/api/diagnostics/workitem/171740 -UseBasicParsing
# 2. Start an Author-Tests session (project 1), auto-handoff to Tester
$body = '{ "workItemId": 171740, "autoHandoff": true }'
Invoke-WebRequest http://localhost:5250/api/author-tests/1/start -Method Post -ContentType application/json -Body $body -UseBasicParsing
# 3. Poll session detail (use the returned sessionId); do NOT call /push (writes to ADO)
Invoke-WebRequest http://localhost:5250/api/author-tests/sessions/detail/<id> -UseBasicParsing
```
