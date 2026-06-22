# QA Automation — TargetCRM / Ideal Agile

Locally-hosted web app for Mahmood (BA, ConstellationDealer) that turns Azure DevOps stories into
test cases and bug reports in his QA style, plus a weekly HTML report of board activity.

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

| Tool | Version | Confirm with | Where to get it |
|---|---|---|---|
| .NET SDK | 8.0+ | `dotnet --version` | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| Node.js | 18+ | `node --version` | <https://nodejs.org/> |
| Git | any | `git --version` | <https://git-scm.com/> |
| Claude CLI *(recommended — the default LLM provider)* | any | `claude --version` | <https://docs.anthropic.com/en/docs/claude-code/setup> |
| Python *(optional — only for the migration helper scripts)* | 3.12+ | `py -3.12 --version` | <https://www.python.org/downloads/> |

You also need at least one of these credentials:

- **Azure DevOps PAT** with scopes **Work Items R/W**, **Test Management R/W**, **Code Read** —
  create at <https://dev.azure.com/ConstellationDealer/_usersSettings/tokens>
- **At least one LLM provider** — pick any:
  - **Claude CLI** *(default — no key, no per-run cost)* — uses your local `claude` CLI login (Pro/Max sub)
  - Anthropic API key (paid) → <https://console.anthropic.com>
  - Azure OpenAI (3 fields)
  - OpenAI-compatible — **GitHub Models is free**: a GitHub PAT works → <https://github.com/settings/tokens>

---

## 2 — One-time setup (do this once after cloning)

### 2.1 Locate the project

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester
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
```

ADO org/project default to `ConstellationDealer` / `Ideal Agile`. Override in **Settings** page later.

### 2.3 Backend — restore + build

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\backend
dotnet restore
dotnet build
```

There is **no separate migration step**. On first run the backend creates the SQLite DB and FTS5
index automatically (it adopts an existing `backend/data/qa.db` if present, otherwise builds fresh)
and seeds the default project.

### 2.4 Frontend — install npm deps

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\client
npm install
```

---

## 3 — Running the app (every time)

You need **two terminals** — one for backend, one for frontend.

### Terminal 1 — Backend

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\backend
dotnet run --project src\QaAutomation.Api
```

This launches on <http://localhost:5250> (the `http` profile) in Development mode. Wait for the
`Now listening on: http://localhost:5250` line. Swagger UI is at <http://localhost:5250/swagger>.

### Terminal 2 — Frontend

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\client
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
2. **Settings → System diagnostics → Azure DevOps** — type any work item ID (e.g. `171740`) →
   **Read** → title/description fills.
3. **Settings → System diagnostics → LLM ping → Ping** — should return `[ProviderName] OK`.

If all three pass, you're good. (Quick backend-only smoke test: `GET http://localhost:5250/api/health`
→ `{"status":"ok"}`.)

---

## 5 — Picking / switching the LLM provider

| Provider | Needs | Notes |
|---|---|---|
| `ClaudeCli`        | local `claude` CLI logged in | **Default.** Uses your Pro/Max sub, no API key, no per-run cost. Slower (~2–5s/call). |
| `Anthropic`        | `ANTHROPIC_API_KEY` (paid) | Most stable for large generations; ~$0.15–$0.60/ticket. |
| `AzureOpenAI`      | endpoint + key + deployment | Use if procurement requires Azure-native AI. |
| `OpenAICompatible` | base URL + key + model | **GitHub Models = free**, also Groq / OpenAI / Ollama. |

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
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\backend
dotnet test
```

### Add or change a DB column

EF Core migrations apply automatically on the next backend start, so you only need to generate one.
Install the EF tool once with `dotnet tool install --global dotnet-ef`, then:

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\backend
dotnet ef migrations add DescribeTheChange `
  --project src\QaAutomation.Infrastructure `
  --startup-project src\QaAutomation.Api
# restart the backend to apply (Program.cs migrates on startup)
```

### Wipe local data (fresh start)

Deletes the SQLite DB, all uploaded `.md` knowledgebase files, and all generated `.html` reports.
Keeps `.env`. The DB + FTS index are rebuilt automatically on the next backend start.

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester
Remove-Item -Recurse -Force backend\data
```

### Build the frontend for production preview

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\client
npm run build
npm run preview         # serves the built bundle on http://localhost:4173
```

Point a build at a remote backend by setting `VITE_QA_API` (see `client/.env.example`); empty =
same-origin / Vite dev proxy.

### Build the whole backend solution

```powershell
cd C:\Users\ahmadmah\Downloads\Projects\ADO_Tester\backend
dotnet build QaAutomation.sln
```

---

## 7 — Where the features live

| Page | Path | What it does |
|---|---|---|
| Settings        | `/`               | Configure ADO, all 4 LLM providers, Author Tests defaults, Reports config + diagnostics |
| Knowledgebase   | `/knowledgebase`  | Upload `.md` files or whole folders; BM25 search to verify retrieval |
| Author Tests    | `/author-tests`   | Pick story → Analyst → Tester → review/edit → push to Test Plan/Suite |
| Find Bugs       | `/find-bugs`      | Pick story → Analyst → paste actual behavior → Bug Writer → review/edit → push Bug WIs |
| Reports         | `/reports`        | Date-range HTML report; Thursday 11:00 auto-fire while app is running |
| Diagnostics     | `/diagnostics`    | Same checks as Settings page diagnostics card, standalone |

---

## 8 — Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet : command not found` | Install the .NET 8 SDK (see Prerequisites) and reopen the terminal. |
| Backend exits immediately / `No connection could be made` from the frontend | Backend isn't running. Check Terminal 1 — `dotnet run` must be live and listening on `:5250`. |
| Frontend shows "Network Error" everywhere | Same as above — the backend on `:5250` is down. |
| CORS errors in the browser console | The frontend must run on `:5173` or `:5174` (the only allowed dev origins). |
| `SQLite Error ... no such table: ...` | The DB didn't initialize. Stop the backend, delete `backend\data`, restart — it rebuilds. |
| `ADO 401` | PAT expired or wrong scopes. Re-issue with **Work Items R/W + Test Management R/W + Code Read**, paste new value into Settings → Azure DevOps. |
| `x-api-key header is required` / Anthropic 401 | You selected Anthropic in the dropdown but `ANTHROPIC_API_KEY` is empty. Either fill it, or switch to a different provider (e.g. Claude CLI) in the AppBar. |
| `Claude CLI '...' not found on PATH` | Either install Claude Code and log in (`claude --version` should work in a fresh terminal), or set an absolute path in Settings → LLM Providers → Claude CLI. |
| LLM ping times out | Increase **Timeout** in Settings, or switch provider. CLI calls take 2–5s by nature. |
| Thursday auto-report didn't fire | The backend must be running at 11:00 Asia/Karachi on Thursday. If the machine was asleep, just hit **Generate** on the Reports page for the past 7 days. |
| Port 5250 / 5173 already in use | `Get-NetTCPConnection -LocalPort 5250 -State Listen \| Select-Object -ExpandProperty OwningProcess \| Stop-Process -Force` |

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
