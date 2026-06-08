# QA Automation — TargetCRM / Ideal Agile

Locally-hosted web app for Mahmood (BA, ConstellationDealer) that turns Azure DevOps stories into
test cases and bug reports in his QA style, plus a weekly HTML report of board activity.

Stack: **Python FastAPI** backend + **React + Vite + MUI** frontend + **SQLite** (FTS5 for
knowledgebase retrieval).

```
qa-automation-app/
├── backend/       # FastAPI + SQLAlchemy + Alembic + APScheduler   (port 5250)
├── client/        # React + Vite + MUI                              (port 5173)
├── docs/
├── .env           # secrets — git-ignored, copy from .env.example
└── .env.example
```

You open just **one URL** in the browser: <http://localhost:5173>. The Vite dev server proxies
`/api/*` to the backend on `:5250` automatically.

---

## 1 — Prerequisites

Install these once. Confirm each with the version command.

| Tool | Version | Confirm with | Where to get it |
|---|---|---|---|
| Python | 3.12+ | `py -3.12 --version` | <https://www.python.org/downloads/> |
| Node.js | 18+ | `node --version` | <https://nodejs.org/> |
| Git | any | `git --version` | <https://git-scm.com/> |
| Claude CLI *(optional — only for `ClaudeCli` LLM provider)* | any | `claude --version` | <https://docs.anthropic.com/en/docs/claude-code/setup> |

You also need at least one of these credentials:

- **Azure DevOps PAT** with scopes **Work Items R/W**, **Test Management R/W**, **Code Read** —
  create at <https://dev.azure.com/ConstellationDealer/_usersSettings/tokens>
- **At least one LLM provider** — pick any:
  - Anthropic API key (paid) → <https://console.anthropic.com>
  - Azure OpenAI (3 fields)
  - OpenAI-compatible — **GitHub Models is free**: a GitHub PAT works → <https://github.com/settings/tokens>
  - Claude CLI — uses your local `claude` CLI login (Pro/Max sub), no key

---

## 2 — One-time setup (do this once after cloning)

### 2.1 Locate the project

```powershell
cd C:\Users\ahmadmah\qa-automation-app
```

### 2.2 Create the `.env` file (secrets)

```powershell
Copy-Item .env.example .env
notepad .env
```

Fill in **at least** these:

```
# REQUIRED — Azure DevOps
ADOACCESS_PAT=<your PAT>

# REQUIRED — at least one LLM. Others can stay blank.
ANTHROPIC_API_KEY=<key>                  # if using Anthropic
# OR
AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com
AZURE_OPENAI_API_KEY=<key>
AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini
# OR
OPENAI_BASE_URL=https://models.inference.ai.azure.com    # GitHub Models = FREE
OPENAI_API_KEY=<GitHub PAT or OpenAI key>
OPENAI_MODEL=gpt-4o-mini
# OR
# (no env vars needed — just have the `claude` CLI installed + logged in)
```

ADO org/project default to `ConstellationDealer` / `Ideal Agile`. Override in **Settings** page later.

### 2.3 Backend — create venv + install deps + create DB schema

```powershell
cd C:\Users\ahmadmah\qa-automation-app\backend

# create venv
py -3.12 -m venv .venv

# activate it (PowerShell)
.\.venv\Scripts\Activate.ps1
# if PowerShell blocks the script: run once -> Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

# install backend dependencies
pip install -e .

# create / migrate SQLite schema
alembic upgrade head
```

After this, your prompt should read `(qa-automation-backend) PS ...>` — that means the venv is
active.

### 2.4 Frontend — install npm deps

```powershell
cd C:\Users\ahmadmah\qa-automation-app\client
npm install
```

---

## 3 — Running the app (every time)

You need **two terminals** — one for backend, one for frontend.

### Terminal 1 — Backend

```powershell
cd C:\Users\ahmadmah\qa-automation-app\backend
.\.venv\Scripts\Activate.ps1
uvicorn app.main:app --port 5250 --host 127.0.0.1 --reload
```

Wait for the line:

```
Uvicorn running on http://127.0.0.1:5250 (Press CTRL+C to quit)
```

### Terminal 2 — Frontend

```powershell
cd C:\Users\ahmadmah\qa-automation-app\client
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
2. **Settings → System diagnostics → Azure DevOps** — type any work item ID (e.g. `165999`) →
   **Read** → title/description fills.
3. **Settings → System diagnostics → LLM ping → Ping** — should return `[ProviderName] OK`.

If all three pass, you're good.

---

## 5 — Picking / switching the LLM provider

| Provider | Needs | Notes |
|---|---|---|
| `Anthropic`        | `ANTHROPIC_API_KEY` (paid) | Fastest. |
| `AzureOpenAI`      | endpoint + key + deployment | Use if procurement requires Azure-native AI. |
| `OpenAICompatible` | base URL + key + model | **GitHub Models = free**, also Groq / OpenAI / Ollama. |
| `ClaudeCli`        | local `claude` CLI logged in | Uses your Pro/Max sub, no API key. Slower (~2–5s/call). |

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
cd C:\Users\ahmadmah\qa-automation-app\backend
.\.venv\Scripts\Activate.ps1
pytest
```

### Add or change a DB column

```powershell
cd C:\Users\ahmadmah\qa-automation-app\backend
.\.venv\Scripts\Activate.ps1
alembic revision --autogenerate -m "describe the change"
alembic upgrade head
```

### Wipe local data (fresh start)

Deletes the SQLite DB, all uploaded `.md` knowledgebase files, all generated `.html` reports.
Keeps `.env`.

```powershell
cd C:\Users\ahmadmah\qa-automation-app
Remove-Item -Recurse -Force backend\data
cd backend
.\.venv\Scripts\Activate.ps1
alembic upgrade head
```

### Build the frontend for production preview

```powershell
cd C:\Users\ahmadmah\qa-automation-app\client
npm run build
npm run preview         # serves the built bundle on http://localhost:4173
```

### Update Python dependencies

```powershell
cd C:\Users\ahmadmah\qa-automation-app\backend
.\.venv\Scripts\Activate.ps1
pip install -e . --upgrade
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
| `No module named uv` when running uvicorn | You don't need `uv`. Activate the venv (`.\.venv\Scripts\Activate.ps1`) and run `uvicorn app.main:app --port 5250 ...` directly. |
| `Activate.ps1 ... cannot be loaded ... execution policies` | One-time fix: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned -Force` |
| `ModuleNotFoundError: No module named 'app'` | You're not in `backend/`. `cd backend` then run uvicorn. |
| `SQLite Error 1: no such table: ...` | A migration hasn't run. From `backend/` with venv active: `alembic upgrade head` |
| `ADO 401` | PAT expired or wrong scopes. Re-issue with **Work Items R/W + Test Management R/W + Code Read**, paste new value into Settings → Azure DevOps. |
| `Anthropic API returned 401: x-api-key header is required` | You selected Anthropic in the dropdown but `ANTHROPIC_API_KEY` is empty. Either fill it, or switch to a different provider in the AppBar. |
| `Claude CLI '...' not found on PATH` | Either install Claude Code and log in (`claude --version` should work in a fresh terminal), or set an absolute path in Settings → LLM Providers → Claude CLI. |
| LLM ping times out | Increase **Timeout** in Settings, or switch provider. CLI calls take 2–5s by nature. |
| Thursday auto-report didn't fire | The backend must be running at 11:00 Asia/Karachi on Thursday. If the machine was asleep, just hit **Generate** on the Reports page for the past 7 days. |
| Port 5250 / 5173 already in use | `Get-NetTCPConnection -LocalPort 5250 -State Listen \| Select-Object -ExpandProperty OwningProcess \| Stop-Process -Force` |
| Frontend shows "Network Error" everywhere | Backend isn't running. Check Terminal 1 — uvicorn must be live. |

---

## 9 — File reference

- **Root README** — you're reading it
- **`backend/README.md`** — deeper backend internals (layout, services, schemas)
- **`.env.example`** — every env var the backend knows about
- **`backend/alembic/`** — DB migration history
- **`client/vite.config.ts`** — frontend port + `/api` proxy target

---

Built across S0 (foundation) → S1 (knowledgebase) → S2 (Analyst+Tester) → S3 (Reports) → S4
(Bug Writer). All four phases live in v1.
