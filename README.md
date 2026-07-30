# QA Automation

Locally-hosted web app that turns Azure DevOps stories into
test cases and bug reports in a QA author's house style, runs a 3-level **API-test automation loop** (scan a
module → generate Playwright specs + ADO Test Cases → execute impact-selected runs off a watched
pipeline → triage failures into draft bugs → post to Teams), and produces a weekly HTML report of
board activity. Every page — and how it works under the hood — is documented in **[§7 — Features](#7--features-what-the-app-does-and-how-it-works)**.

Stack: **.NET 8 (ASP.NET Core) backend** + **EF Core 8 + SQLite** (FTS5 for knowledgebase
retrieval) + **React 19 + Vite + MUI** frontend (Redux Toolkit + React Query).

> **Re-platformed to .NET.** This was originally a Python FastAPI app. The Python backend has been
> retired and rewritten in .NET 8 — the .NET backend now lives in `backend/` and is the sole
> backend. The migration brief and phase log are in [`docs/`](docs/) and
> [`backend/README.md`](backend/README.md). (Recover the old Python source from git history if a
> parity reference is ever needed.)

```
ADO_Tester/
├── backend/        # .NET 8 ASP.NET Core Web API + EF Core + SQLite     (port 5250)
│   ├── QaAutomation.sln
│   ├── src/
│   │   ├── QaAutomation.Api/             # host: Program.cs, Controllers, Middlewares, SSE
│   │   ├── QaAutomation.Core/            # abstractions, options, models/DTOs
│   │   └── QaAutomation.Infrastructure/  # EF data, ADO client, LLM, agents, KB, reports
│   ├── tests/QaAutomation.Tests/         # xUnit
│   └── data/       # qa.db + KB uploads + report HTML — git-ignored, auto-created
├── client/         # React + Vite + MUI                                 (port 5173)
├── docs/           # migration spec, plan, parity checklist, verification log
├── .env            # secrets — git-ignored, copy from .env.example
└── .env.example
```

You open just **one URL** in the browser: <http://localhost:5173>. The Vite dev server proxies
`/api/*` to the backend on `:5250` automatically.

---

## 1 — Prerequisites

Install these once. Confirm each with the version command.

| Tool                                                        | Version | Confirm with         | Where to get it                                        |
| ----------------------------------------------------------- | ------- | -------------------- | ------------------------------------------------------ |
| .NET SDK                                                    | 8.0+    | `dotnet --version`   | <https://dotnet.microsoft.com/download/dotnet/8.0>     |
| Node.js                                                     | 18+     | `node --version`     | <https://nodejs.org/>                                  |
| Git                                                         | any     | `git --version`      | <https://git-scm.com/>                                 |
| Claude CLI *(recommended — the default LLM provider)*       | any     | `claude --version`   | <https://docs.anthropic.com/en/docs/claude-code/setup> |
| Python *(optional — only for the migration helper scripts)* | 3.12+   | `py -3.12 --version` | <https://www.python.org/downloads/>                    |

You also need at least one of these credentials:

- **Azure DevOps PAT** with scopes **Work Items R/W**, **Test Management R/W**, **Code Read** —
  create at <https://dev.azure.com/{your-org}/_usersSettings/tokens>
- **At least one LLM provider** — pick any:
  - **Claude CLI** *(default — no key, no per-run cost)* — uses your local `claude` CLI login (Pro/Max sub)
  - Anthropic API key (paid) → <https://console.anthropic.com>
  - Azure OpenAI (3 fields)
  - OpenAI-compatible — **GitHub Models is free**: a GitHub PAT works → <https://github.com/settings/tokens>
  - Ollama — native local or cloud API support

---

## 2 — One-time setup (do this once after cloning)

### 2.1 Locate the project

```powershell
cd C:\path\to\ADO_Tester
```

### 2.2 Create the `.env` file (secrets)

The backend loads `.env` from the repo root automatically (it walks up the directory tree).

```powershell
Copy-Item .env.example .env
notepad .env
```

Fill in **at least** these:

```
# REQUIRED — Azure DevOps
ADOACCESS_PAT=<your PAT>

# REQUIRED — at least one LLM. Others can stay blank.
# Claude CLI is the default provider and needs NO env vars — just have `claude` installed + logged in.
# Optionally pin it:
# CLAUDE_CLI_PATH=claude
# CLAUDE_CLI_MODEL=
# CLAUDE_CLI_TIMEOUT_SECONDS=180

# OR Anthropic API (paid)
ANTHROPIC_API_KEY=<key>
# OR Azure OpenAI
AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com
AZURE_OPENAI_API_KEY=<key>
AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini
# OR OpenAI-compatible (GitHub Models = FREE)
OPENAI_BASE_URL=https://models.inference.ai.azure.com
OPENAI_API_KEY=<GitHub PAT or OpenAI key>
OPENAI_MODEL=gpt-4o-mini
# OR native Ollama (cloud)
OLLAMA_BASE_URL=https://ollama.com/api
OLLAMA_API_KEY=<Ollama API key>
OLLAMA_MODEL=llama3.2
```

Set your ADO org and project on the **Settings** page (values saved there override anything in `.env`).

### 2.3 Backend — restore + build

```powershell
cd C:\path\to\ADO_Tester\backend
dotnet restore
dotnet build
```

There is **no separate migration step**. On first run the backend creates the SQLite DB and FTS5
index automatically (it adopts an existing `backend/data/qa.db` if present, otherwise builds fresh)
and seeds the default project.

### 2.4 Frontend — install npm deps

```powershell
cd C:\path\to\ADO_Tester\client
npm install
```

---

## 3 — Running the app (every time)

You need **two terminals** — one for backend, one for frontend.

### Terminal 1 — Backend

```powershell
cd C:\path\to\ADO_Tester\backend
dotnet run --project src\QaAutomation.Api
```

This launches on <http://localhost:5250> (the `http` profile) in Development mode. Wait for the
`Now listening on: http://localhost:5250` line. Swagger UI is at <http://localhost:5250/swagger>.

### Terminal 2 — Frontend

```powershell
cd C:\path\to\ADO_Tester\client
npm run dev
```

Wait for the line:

```
  ➜  Local:   http://localhost:5173/
```

### Open the app

<http://localhost:5173>

The AppBar shows the active LLM provider — switch any time from the dropdown.

### Shut down

`Ctrl+C` in each terminal.

---

## 4 — Verify it works

After both terminals are up, open the app and:

1. **Settings → System diagnostics → Check** — should show ADOACCESS_PAT = Present and your
   chosen LLM provider = Present.
2. **Settings → System diagnostics → Azure DevOps** — type any valid work item ID from your project →
   **Read** → title/description fills.
3. **Settings → System diagnostics → LLM ping → Ping** — should return `[ProviderName] OK`.

If all three pass, you're good. (Quick backend-only smoke test: `GET http://localhost:5250/api/health`
→ `{"status":"ok"}`.)

---

## 5 — Picking / switching the LLM provider

| Provider           | Needs                        | Notes                                                                                 |
| ------------------ | ---------------------------- | ------------------------------------------------------------------------------------- |
| `ClaudeCli`        | local `claude` CLI logged in | **Default.** Uses your Pro/Max sub, no API key, no per-run cost. Slower (~2–5s/call). |
| `Anthropic`        | `ANTHROPIC_API_KEY` (paid)   | Most stable for large generations; ~$0.15–$0.60/ticket.                               |
| `AzureOpenAI`      | endpoint + key + deployment  | Use if procurement requires Azure-native AI.                                          |
| `OpenAICompatible` | base URL + key + model       | **GitHub Models = free**, also Groq / OpenAI.                                         |
| `Ollama`           | native API base URL + key + model | Native API: local `http://localhost:11434/api` or cloud `https://ollama.com/api`. |

Switch any time from the **AppBar dropdown** (`Llm`). The choice persists in SQLite — survives
restart.

### Claude CLI model picker

Settings → LLM Providers → Claude CLI:

- **CLI path** — leave as `claude` for PATH lookup, or set absolute path
- **Model** — Autocomplete with suggestions (`sonnet`/`opus`/`haiku` aliases + versioned IDs like
  `claude-sonnet-4-6`). Blank = whatever the CLI defaults to. **Freetext accepted — type any
  model ID.**
- **Timeout (seconds)** — CLI call timeout

---

## 6 — Common operations

### Run unit tests

```powershell
cd C:\path\to\ADO_Tester\backend
dotnet test
```

### Add or change a DB column

EF Core migrations apply automatically on the next backend start, so you only need to generate one.
Install the EF tool once with `dotnet tool install --global dotnet-ef`, then:

```powershell
cd C:\path\to\ADO_Tester\backend
dotnet ef migrations add DescribeTheChange `
  --project src\QaAutomation.Infrastructure `
  --startup-project src\QaAutomation.Api
# restart the backend to apply (Program.cs migrates on startup)
```

### Wipe local data (fresh start)

Deletes the SQLite DB, all uploaded `.md` knowledgebase files, and all generated `.html` reports.
Keeps `.env`. The DB + FTS index are rebuilt automatically on the next backend start.

```powershell
cd C:\path\to\ADO_Tester
Remove-Item -Recurse -Force backend\data
```

### Build the frontend for production preview

```powershell
cd C:\path\to\ADO_Tester\client
npm run build
npm run preview         # serves the built bundle on http://localhost:4173
```

Point a build at a remote backend by setting `VITE_QA_API` (see `client/.env.example`); empty =
same-origin / Vite dev proxy.

### Build the whole backend solution

```powershell
cd C:\path\to\ADO_Tester\backend
dotnet build QaAutomation.sln
```

---

## 7 — Features (what the app does and how it works)

The app is one React SPA with **nine pages** in the left sidebar, all backed by the flat `/api` .NET
backend. This section covers every feature and the mechanism behind it.

| #   | Page (sidebar)    | Route            | What it does                                                                                             |
| --- | ----------------- | ---------------- | -------------------------------------------------------------------------------------------------------- |
| 1   | **Dashboard**     | `/projects`      | Live snapshot of the workspace — KPI tiles, an outcome donut, a recent-activity feed, quick-launch tiles |
| 2   | **Knowledgebase** | `/knowledgebase` | Upload `.md` files/folders; chunked + indexed into SQLite FTS5; BM25 search to verify retrieval          |
| 3   | **QA Loop**       | `/qa-loop`       | Scan an API module → 3-level coverage matrix → generate Playwright specs + ADO Test Cases                |
| 4   | **QA Runs**       | `/qa-runs`       | Execute specs against a DMS profile → ADO Test Run + AI failure triage → draft bugs + Allure report      |
| 5   | **QA Pipelines**  | `/qa-pipelines`  | Watch an ADO pipeline; each build an AI impact analyzer picks the affected tests and fires a run         |
| 6   | **Author Tests**  | `/author-tests`  | Pick a story → Analyst → Tester → review/edit → push to a Test Plan/Suite                                |
| 7   | **Find Bugs**     | `/find-bugs`     | Pick a story → Analyst → paste actual behavior → Bug Writer → review/edit → push Bug work items          |
| 8   | **Reports**       | `/reports`       | Date-range HTML board-activity report; auto-fires Thursday 11:00 while the app is running                |
| 9   | **Settings**      | `/settings`      | ADO connection, LLM providers (live switch), Author-Tests / Reports / QA-Loop defaults, diagnostics      |

> Two surfaces have **no sidebar entry**: **Batch (Epic) runs** — launched from Author Tests / Find
> Bugs to fan a pipeline across every child of an epic (`/api/batches`) — and **Diagnostics**, folded
> into the Settings page rather than living on its own route.

### 7.0 — How the pipelines work (shared plumbing)

Six of the nine pages drive a long-running LLM pipeline, and they all share the same machinery:

- **ADO integration** — one cached `AzureDevOpsClient` (PAT Basic auth) reads work items, test
  plans/suites, comments/updates, Git diffs/commits/code-search, and pipeline builds, and **writes**
  Test Cases, Bugs, suite membership, Tested-By / Related links, and full ADO Test Run results. Write
  bodies are byte-for-byte frozen so ADO artifacts look identical to the hand-authored originals.
- **LLM agents** — each agent (`Analyst`, `Tester`, `BugWriter`, `CodeVerifier`, `ModuleAnalyzer`,
  the three `*Generator`s, `ImpactAnalyzer`, `FailureTriage`) wraps a runtime-switchable `ILlmClient`
  and streams output. System prompts are embedded resources (byte-exact from the retired Python app).
- **Background + SSE pattern** — an HTTP call seeds a session row **synchronously**, returns its id,
  then runs the pipeline on a background `Task` with its **own DI scope**. Live progress is published
  to an in-memory event bus and streamed to the browser over **Server-Sent Events**; the durable
  narrative is persisted to the session's event log, so a page refresh replays it. Cancellation flows
  through a registry and a global concurrency cap protects the machine.
- **Knowledgebase retrieval** — agents pull relevant `.md` chunks by BM25 to ground their output.
- **Runtime-switchable providers** — the active LLM provider is persisted in SQLite and swapped live
  from the AppBar dropdown; every call builds a fresh client (see §5).

### 7.1 — Dashboard (`/projects`)

**What:** the landing snapshot of the workspace. **How:** it loads recent Author-Tests and Find-Bugs
sessions, reports, and KB documents, then renders KPI tiles (test / bug sessions, reports, KB docs),
a donut of test-session outcomes (Done / Failed / Running), a merged **recent-activity feed** (latest
seven items across all three), and quick-launch tiles. Read-only — a jumping-off point, not a pipeline.

### 7.2 — Knowledgebase (`/knowledgebase`)

**What:** a per-project markdown knowledge base that grounds the agents in domain context. **How:**
upload individual `.md` files or a whole folder → each document is stored under
`backend/data/kb/<projectId>/` and split into ~1000-character chunks that are indexed into the SQLite
**FTS5** `doc_chunks` virtual table. The search box runs a **BM25** `MATCH` and returns ranked
snippets — the exact same lexical retrieval the Analyst calls automatically before analysing a story
(no embeddings, no API calls, fully local). Deleting a document removes its chunks and the file.

### 7.3 — Author Tests (`/author-tests`)

**What:** turn one ADO story into house-style Test Cases and push them to a Test Plan/Suite.
**How (the flow):**

1. Search for and pick a story.
2. **Analyst** reads the title/description/acceptance criteria (plus KB context) → a structured
   analysis (module, feature, happy path, edge & negative cases, business rules, permissions).
3. **Tester** turns that analysis into Test Case drafts (title, preconditions, numbered steps) —
   automatically (auto-handoff) or when you click **Run Tester**.
4. Review and edit drafts inline; editing the analysis can regenerate the tests.
5. **Approve** → choose a Test Plan/Suite → **Push**: each Test Case is created, added to the suite,
   and linked **Tested-By** back to the story.

Progress streams live via SSE. Orchestrated by `AgentOrchestrator`.

### 7.4 — Find Bugs (`/find-bugs`)

**What:** contrast a story's expected behavior against what actually happened and file bugs.
**How (the flow):**

1. Pick a story → **Analyst** produces the expected-behavior analysis.
2. You paste the **actual behavior** and, optionally, code context — commit/PR ids the backend
   resolves into real Git diffs.
3. **CodeVerifier** inspects the linked code, then **BugWriter** contrasts expected vs. actual vs.
   code evidence → prioritized bug drafts (severity, repro steps, expected / actual / suggested fix).
4. Review/edit → **Approve** → **Push**: each Bug work item is created (frozen ReproSteps / AC /
   SystemInfo HTML) and linked **Related** to the story.

Also supports "bug from a failed test verdict" (instant, or agent-reviewed). Orchestrated by `BugOrchestrator`.

### 7.5 — QA Loop: module scan & author (`/qa-loop`)

**What:** the API-testing brain. Scan a backend module and author a full **3-level** suite —
**Contract**, **Endpoint**, **Workflow** — as Playwright specs *and* ADO Test Cases in the house style.
**How (the flow):**

1. **Scan** — supply a module, repo/branch, a target Plan/Suite, and optionally a story + source
   directories. `ModuleAnalyzerAgent` reads the source over the ADO Git API and emits an **inventory**
   (routes, DTOs, workflows) plus a **coverage matrix** reconciled against the story's existing Test
   Cases — every row is marked *gap* or *covered*.
2. **Review** — tick the surfaces to author and save edits to the matrix (auto-handoff skips this).
3. **Generate** — three agents run **in parallel** over the selected rows:
   - **Contract** → a Zod schema + a `.spec.ts` asserting response shape/types.
   - **Endpoint** → one spec with `200` / `401` (no bearer) / `400` (invalid body) cases.
   - **Workflow** → a chained multi-step spec with idempotent cleanup.

   Specs are written to disk under `backend/data/.../scripts/<module>/…`, instrumented for Allure, and
   each carries a `[<tcId-pending>]` title tag.
4. **Push** — creates the ADO Test Cases, rewrites the pending tag to the real id (in both title and
   spec), adds them to the suite, and links Tested-By. Re-pushing is idempotent. Orchestrated by
   `QaLoopOrchestrator`.

### 7.6 — QA Runs: execute, triage, file bugs (`/qa-runs`)

**What:** run the generated specs and turn failures into bugs. **How (the flow):**

1. **Start** — pick a module (optionally filtered by level/tag) and a target **DMS profile** (one
   profile is credentialed for the MVP; the others are reserved). Runs can also be fired for you by a
   pipeline watcher (§7.7).
2. **Execute** — `PlaywrightRunner` provisions a Node runtime once, then runs the selected specs
   against the profile's base URL + bearer token; results are parsed from JUnit XML into
   pass/fail/skipped counts.
3. **ADO Test Run** — a fresh ADO Test Run is created, each Test Case is marked Pass/Fail, and the run
   is completed (visible in ADO's Test hub).
4. **Triage** — `FailureTriageAgent` classifies each failure as **real_bug / flaky / test_issue /
   stale_test** with a confidence and reasoning; real bugs get a pre-built bug draft (no second LLM call).
5. **File bugs** — select failures → ADO Bugs are created and linked to the Test Case + story.
6. **Report + notify** — a single-file **Allure** HTML report (with per-`(module, pipeline)` trend
   history) is generated and served in-app, and a **Microsoft Teams** card posts the summary.

Orchestrated by `RunExecutionService` + `ResultsIngestService`, with `TeamsNotifier` for the cards.

### 7.7 — QA Pipelines: watch & impact-analyze (`/qa-pipelines`)

**What:** keep tests running automatically as the product changes. **How:**

- Add a **watch**: an ADO pipeline id + display name + the module whose tests it maps to.
- A background hosted service **polls** every few minutes (configurable). On the first poll it
  fast-forwards to the newest build; afterwards it processes new builds oldest-first, backing off on error.
- Per build it collects the **changed files**, projects the module's pushed drafts to a token-cheap
  inventory (no spec code), and asks `ImpactAnalyzerAgent` which tests are affected (impacted ids +
  reasoning + confidence). If analysis fails repeatedly it **falls back** to running the whole module.
- It then fires a QA Run (trigger = *PipelineWatcher*, tagged with the build/commit) down the same
  execution path as a manual run. **Run latest** and **Skip to latest** give manual control; the
  enable switch pauses a watch.

### 7.8 — Reports (`/reports`)

**What:** a self-contained, themed HTML slide-deck of a week's board activity. **How:** choose a date
range + boards + theme → the backend pulls the tickets, an LLM summarizer groups them into
themes / wins / risks, and `ReportHtmlBuilder` renders a fully self-contained `.html` file (Executive /
Standup / Branded themes) saved under `backend/data/reports/` and viewable or downloadable in-app. A
hosted scheduler auto-fires it **Thursday 11:00 (local time)** (toggle + defaults live in Settings)
whenever the app is running — there is no catch-up for a missed slot.

### 7.9 — Settings & Diagnostics (`/settings`)

**What:** everything configurable, plus one-click health checks. **How:**

- **ADO** — org / project / PAT, with pickers that auto-discover orgs, projects, area paths, repos and branches.
- **LLM providers** — configure all four and switch the active one **live** from the AppBar (the choice persists in SQLite; see §5).
- **Defaults** — Author-Tests plan/suite, Reports boards/schedule/theme, and a **QA Loop** tab (Teams
  webhook + template, watcher poll interval, Allure report provider, verbose-activity toggle).
- **Two-layer settings** — `.env` seeds strongly-typed options; a DB-persisted overlay edited here
  **wins over** `.env` (applied at startup and on save).
- **Diagnostics** — one-click probes: required secrets present, an ADO read of a work item, and an LLM
  ping to the active provider (the same checks used in §4).

---

## 8 — Troubleshooting

| Symptom                                                                     | Fix                                                                                                                                                           |
| --------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet : command not found`                                                | Install the .NET 8 SDK (see Prerequisites) and reopen the terminal.                                                                                           |
| Backend exits immediately / `No connection could be made` from the frontend | Backend isn't running. Check Terminal 1 — `dotnet run` must be live and listening on `:5250`.                                                                 |
| Frontend shows "Network Error" everywhere                                   | Same as above — the backend on `:5250` is down.                                                                                                               |
| CORS errors in the browser console                                          | The frontend must run on `:5173` or `:5174` (the only allowed dev origins).                                                                                   |
| `SQLite Error ... no such table: ...`                                       | The DB didn't initialize. Stop the backend, delete `backend\data`, restart — it rebuilds.                                                                     |
| `ADO 401`                                                                   | PAT expired or wrong scopes. Re-issue with **Work Items R/W + Test Management R/W + Code Read**, paste new value into Settings → Azure DevOps.                |
| `x-api-key header is required` / Anthropic 401                              | You selected Anthropic in the dropdown but `ANTHROPIC_API_KEY` is empty. Either fill it, or switch to a different provider (e.g. Claude CLI) in the AppBar.   |
| `Claude CLI '...' not found on PATH`                                        | Either install Claude Code and log in (`claude --version` should work in a fresh terminal), or set an absolute path in Settings → LLM Providers → Claude CLI. |
| LLM ping times out                                                          | Increase **Timeout** in Settings, or switch provider. CLI calls take 2–5s by nature.                                                                          |
| Thursday auto-report didn't fire                                            | The backend must be running at 11:00 (local time) on Thursday. If the machine was asleep, just hit **Generate** on the Reports page for the past 7 days.      |
| Port 5250 / 5173 already in use                                             | `Get-NetTCPConnection -LocalPort 5250 -State Listen \| Select-Object -ExpandProperty OwningProcess \| Stop-Process -Force`                                    |

---

## 9 — File reference

- **Root README** — you're reading it
- **[`backend/README.md`](backend/README.md)** — .NET backend internals (project layout, services, phase log)
- **[`docs/dotnet-migration-spec.md`](docs/dotnet-migration-spec.md)** — the authoritative migration brief (locked decisions)
- **[`docs/dotnet-migration-plan.md`](docs/dotnet-migration-plan.md)** — Python → .NET re-platform plan
- **[`docs/dotnet-migration-parity-checklist.md`](docs/dotnet-migration-parity-checklist.md)** — per-item definition-of-done
- **[`docs/manual-verification-and-open-issues.md`](docs/manual-verification-and-open-issues.md)** — living verification log + known issues
- **`.env.example`** — every env var the backend knows about; **`client/.env.example`** — `VITE_QA_API`
- **`golden_capture.py` / `write_prompts.py`** — migration helper scripts (capture live ADO golden files; extract agent prompts)

---

Re-platformed from the original Python FastAPI app (built across S0 foundation → S1 knowledgebase →
S2 Analyst+Tester → S3 Reports → S4 Bug Writer) to the .NET 8 backend that lives here now, with the
React frontend re-architected onto Redux Toolkit + React Query.
