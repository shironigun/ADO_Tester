# QA Automation — Python Backend

FastAPI + SQLAlchemy 2.0 (async) + Alembic + httpx + Anthropic SDK + OpenAI SDK + APScheduler +
Jinja2. Replaces the original .NET backend; React/Vite/MUI frontend in `../client/` is untouched.

## Run

```powershell
# from repo root
cd backend
py -3.12 -m uv run uvicorn app.main:app --port 5250 --host 127.0.0.1
```

```powershell
cd client
npm run dev
```

Open http://localhost:5173 — Vite proxies `/api` to the Python backend on `:5250`.

## Layout

```
app/
  config.py          Pydantic Settings (loads ../.env)
  db.py              async engine + SessionLocal + FTS5 bootstrap
  models/            SQLAlchemy ORM (1-for-1 with the old .NET entities — same table/column names)
  schemas/           Pydantic request/response, camelCase aliases
  services/
    ado_client.py    Azure DevOps REST (PAT/Basic). Read + Test Plan/Suite/Case writes + Bug writes
    knowledgebase.py FTS5/BM25 .md retrieval
    llm/             4 providers (Anthropic / AzureOpenAI / OpenAICompatible / ClaudeCli) + runtime switch
    agents/          AnalystAgent, TesterAgent, BugWriterAgent
    orchestrators/   AgentOrchestrator (Author Tests), BugOrchestrator (Find Bugs)
    reports/         html_builder.py + service.py + scheduler.py (APScheduler Thu 11:00 Asia/Karachi)
  routers/           diagnostics, settings, knowledgebase, author_tests, find_bugs, reports
alembic/             versioned migrations (no more EnsureCreated wipes)
data/                git-ignored — qa.db + kb/<project_id>/*.md + reports/*.html
```

## Migrations

```powershell
# After editing any ORM model:
py -3.12 -m uv run alembic revision --autogenerate -m "describe change"
py -3.12 -m uv run alembic upgrade head
```

## Secrets (loaded from repo-root `.env`)

`ADOACCESS_PAT`, `ANTHROPIC_API_KEY`,
`AZURE_OPENAI_ENDPOINT` / `_API_KEY` / `_DEPLOYMENT`,
`OPENAI_BASE_URL` / `_API_KEY` / `_MODEL` (GitHub Models free tier supported).

## Endpoint contract (unchanged from .NET — frontend required no edits)

- `GET  /api/diagnostics` · `GET /api/diagnostics/workitem/{id}` · `GET /api/diagnostics/llm`
- `GET/PUT /api/settings/provider`
- `GET/POST /api/knowledgebase/{projectId}` · `DELETE /api/knowledgebase/doc/{id}` · `GET .../search?q&k`
- `/api/author-tests/*` (search, plans, suites, start, run-tester, cancel, sessions list/detail,
  drafts PATCH, approve-all, push)
- `/api/find-bugs/*` (start, run, cancel, push, approve-all, sessions list/detail, drafts PATCH)
- `/api/reports/*` (generate, list, detail, html, download)
