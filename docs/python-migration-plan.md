# Backend Migration Plan — .NET → Python

## Context

The backend (`src/QaAutomation.Api`, `src/QaAutomation.Core`, `src/QaAutomation.Infrastructure`) is
.NET 8 Web API + EF Core + Hangfire-less hosted `BackgroundService`. It works end-to-end across
S0–S4-v1 but has friction points:

- Every entity change requires manual `data/` wipe (`EnsureCreated` is one-shot).
- Adding ML / NLP / vector-store / Playwright-driven scraping later is materially easier in Python.
- The user wants fewer paid / proprietary dependencies and more leverage on free, mainstream
  ecosystem tooling.

This document migrates the backend to Python **without changing any user-facing behavior**.
The React/Vite/MUI frontend stays untouched. Endpoint paths, request/response shapes, status enum
spellings, and the `/api/...` contract remain identical so the client requires zero changes (only
the dev-proxy target port might shift).

## Stack — chosen libraries (all free / OSS)

| Concern | Library | Why |
|---|---|---|
| Web framework | **FastAPI** | Async, OpenAPI built-in, Pydantic v2 validation, large ecosystem |
| ASGI server | **Uvicorn** | Standard for FastAPI; `--reload` for dev |
| ORM | **SQLAlchemy 2.0 (async)** | Mature, idiomatic typed async; pairs with Alembic |
| Migrations | **Alembic** | Solves the `EnsureCreated` wipe pain — auto-generates schema diffs |
| SQLite driver | **aiosqlite** | Async SQLite for SQLAlchemy + raw FTS5 queries |
| Config / .env | **pydantic-settings** + **python-dotenv** | Same `.env` file we already use |
| HTTP client | **httpx (async)** | Used by both ADO REST + the OpenAI-compatible LLM client |
| LLM — Anthropic | **`anthropic` SDK (official)** | Replaces `AnthropicLlmClient` |
| LLM — OpenAI / Azure / compatible | **`openai` SDK (official)** | One SDK covers OpenAI, Azure OpenAI, GitHub Models, Groq, OpenRouter, local Ollama (just swap `base_url` + key) |
| Templates (report HTML) | **Jinja2** | Renders the self-contained `report.html` (same dual-mode markup) |
| Scheduler (Thursday 11AM) | **APScheduler (AsyncIOScheduler)** | In-process cron; same constraint as today (only fires while app is running) |
| Background tasks | **FastAPI `BackgroundTasks` + `asyncio.Task`** | Replaces `Task.Run` + `IServiceScopeFactory.CreateScope` pattern |
| Cancellation | **`asyncio.CancelledError` + `dict[int, asyncio.Task]` registry** | Direct replacement for `ConcurrentDictionary<int, CancellationTokenSource>` |
| Logging | **stdlib `logging`** (optional **structlog**) | Trivial swap from `ILogger` |
| Multipart upload | FastAPI `UploadFile` | Replaces `IFormFile` |
| Package manager | **`uv`** (Astral) | Fastest installer; `pyproject.toml`-based. (Fallback: plain `pip` + `requirements.txt`.) |
| Type checking | **pyright / mypy** | Optional but matches .NET-grade discipline |
| Testing | **pytest** + **pytest-asyncio** + **httpx** test client | Parity with .NET test project |

**Out of scope** (no functional change): swapping any frontend, redesigning any UI, adding new
features, changing field names, changing endpoint paths.

## Architecture mapping

```
.NET project                          →   Python module
─────────────────────────────────────     ──────────────────────────────────
QaAutomation.Core/Models/*.cs         →   app/models/*.py        (SQLAlchemy ORM)
QaAutomation.Core/Models/*.cs (DTOs)  →   app/schemas/*.py       (Pydantic)
QaAutomation.Core/Configuration/*.cs  →   app/config.py          (BaseSettings)
QaAutomation.Core/Abstractions/*.cs   →   app/services/.../base.py (Protocol classes)
QaAutomation.Infrastructure/Data      →   app/db.py + alembic/
QaAutomation.Infrastructure/AzureDevOps→  app/services/ado_client.py
QaAutomation.Infrastructure/Llm       →   app/services/llm/{anthropic,openai_compat,azure_openai}.py
QaAutomation.Infrastructure/Knowledgebase → app/services/knowledgebase.py
QaAutomation.Infrastructure/Agents    →   app/services/agents/{analyst,tester,bug_writer}.py
                                          app/services/orchestrators/{agent,bug}.py
QaAutomation.Infrastructure/Reports   →   app/services/reports/{service,html_builder,scheduler}.py
QaAutomation.Api/Controllers/*.cs     →   app/routers/*.py
QaAutomation.Api/Program.cs           →   app/main.py
```

### Endpoint contract (unchanged)

All current routes preserved exactly:

- `GET /api/diagnostics`, `GET /api/diagnostics/workitem/{id}`, `GET /api/diagnostics/llm`
- `GET/PUT /api/settings/provider`
- `GET/POST/DELETE /api/knowledgebase/{projectId}` + `GET /api/knowledgebase/{projectId}/search`
- `GET/POST /api/author-tests/*`, `PATCH /api/author-tests/drafts/{id}`
- `GET/POST/PATCH /api/find-bugs/*`
- `GET/POST /api/reports/*` + `GET /api/reports/html/{id}` + `GET /api/reports/download/{id}`

### Response shape parity rules

- All status enums serialize as **strings** (`"Drafted"`, `"Approved"`, etc.) — Pydantic's
  `use_enum_values = True` on response models.
- `camelCase` JSON keys — Pydantic alias generator (`to_camel`).
- Paged sessions list returns `{ total, items }` — unchanged.
- Error responses: `{ error: "<message>" }` for controlled validation failures; ProblemDetails
  shape for unhandled — FastAPI `HTTPException` covers both via a custom exception handler.

### Concurrency model

- One global `asyncio` event loop. Every request handler is `async def`.
- Long-running pipelines (Analyst, Tester, Bug Writer, Push, Report) launch as
  `asyncio.create_task(...)`. Task is stored in a `dict[int, asyncio.Task]` keyed by session ID.
- Cancel = `task.cancel()` → `CancelledError` caught inside the pipeline.
- Each background task acquires a **fresh DB session** (`async with SessionLocal() as s:`) so
  request-scoped sessions never leak across the response boundary (this was the C# bug we hit).
- Pipeline updates `session.status` / `current_activity` and commits at each milestone — UI polls
  the same endpoints as today.

### LLM provider switcher (persistence-equivalent)

- `LlmProviderState` singleton replaced by a small wrapper around an `AppSetting` row
  (`key='llm.provider'`). Same `Set()` writes to DB; same `Current` reads on first access then
  caches in memory. `GET/PUT /api/settings/provider` shape is identical.
- One `LlmClient` Protocol class. Three implementations:
  - `AnthropicLlmClient` — uses the `anthropic` SDK.
  - `AzureOpenAiLlmClient` — uses the `openai` SDK with `AzureOpenAI()` constructor.
  - `OpenAiCompatibleLlmClient` — uses the `openai` SDK with `OpenAI(base_url=..., api_key=...)`.
  - Default model + max-tokens read from settings, same as today.
- Same secret sanitizer (API key + PAT scrub) wraps error responses before raising.

### ADO client

- One `AzureDevOpsClient` class wrapping `httpx.AsyncClient` configured with Basic auth from
  `ADOACCESS_PAT`.
- Same `ThrowIfFailed` pattern → raises a custom `AdoApiError` with sanitized body.
- Identical methods: `get_work_item`, `search_work_items`, `get_test_plans`, `get_test_suites`,
  `create_test_case`, `add_test_case_to_suite`, `link_tested_by`, `create_bug`, `link_related`,
  `get_tickets_in_range`. JSON shapes for create / link / suite-add stay bit-identical (the
  workItem-wrapper fix and `op: "add"` shape we already proved).

### Knowledgebase / FTS5

- SQLAlchemy ORM for `Project`, `KnowledgebaseDocument` (and the other entities).
- The `doc_chunks` FTS5 table is **not** managed by SQLAlchemy — created via raw SQL at startup
  (`CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)`),
  same as today. Searches run via `await session.execute(text("... MATCH ... ORDER BY bm25(...)"))`.
- File storage layout unchanged: `data/kb/{project_id}/<guid>-<filename>.md`.
- Chunker logic ported verbatim (~1000-char paragraphs).
- `IEmbeddingClient` Protocol stubbed; semantic-retrieval upgrade lands later via the `openai`
  SDK embeddings endpoint (free with Azure OpenAI + GitHub Models).

### Reports module

- HTML builder rewritten as a single Jinja2 template `templates/report.html.j2` that produces
  the **exact same** dual-mode (Present / Explore) document — inline CSS / JS / SVG, zero external
  deps. The donut + bars stay hand-rolled SVG (helper functions in Python).
- `ReportService.generate_async()` follows the same flow: insert row → background task →
  `ado.get_tickets_in_range()` → group stats → render template → write to `data/reports/...html`.
- `WeeklyReportScheduler` becomes an `AsyncIOScheduler` job:
  `scheduler.add_job(fire_weekly, CronTrigger(day_of_week='thu', hour=11, minute=0))`.

### Migrations strategy

- Alembic `init` once; squash existing schema into the **initial migration** that matches the
  current SQLite DB.
- Every entity change after that → `alembic revision --autogenerate -m "..."` → `alembic upgrade head`.
- Bye-bye DB wipes.

## .env additions

None required. The existing keys all map across:

```
ADOACCESS_PAT, ANTHROPIC_API_KEY,
AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT,
OPENAI_BASE_URL / _API_KEY / _MODEL
```

Add `BACKEND_PORT` (default `5249`) so the React dev proxy keeps working without edits.

## Project layout

```
qa-automation-app/
  backend/                          ← NEW
    pyproject.toml                  (uv-managed; deps below)
    .python-version                 (3.12)
    README.md
    alembic.ini
    alembic/versions/
    app/
      main.py                       (FastAPI app factory, lifespan, CORS, scheduler start)
      config.py                     (BaseSettings, .env loading, options for ADO / LLM / Storage)
      db.py                         (engine, session factory, FTS5 init)
      models/                       (SQLAlchemy ORM — Project, KnowledgebaseDocument, TestSession,
                                     TestCaseDraft, BugSession, BugDraft, Report, AppSetting)
      schemas/                      (Pydantic request/response shapes — camelCase aliases)
      services/
        ado_client.py
        knowledgebase.py
        llm/
          base.py (Protocol)
          anthropic_client.py
          openai_compatible_client.py
          azure_openai_client.py
          provider_state.py
          sanitizer.py
        agents/
          analyst.py
          tester.py
          bug_writer.py
          json_utils.py             (lenient list decoder + ```fence stripper)
        orchestrators/
          agent_orchestrator.py     (Analyst → Tester + Push)
          bug_orchestrator.py       (Analyst → Bug Writer + Push)
        reports/
          service.py
          html_builder.py
          scheduler.py
      routers/
        diagnostics.py
        settings.py
        knowledgebase.py
        author_tests.py
        find_bugs.py
        reports.py
      templates/
        report.html.j2
      data/                         (gitignored: qa.db, kb/, reports/)
    tests/                          (pytest)
  client/                           ← unchanged
  src/                              ← .NET kept until parity verified, then deleted
```

`pyproject.toml` dependencies (pin to a known-good set):

```
fastapi, uvicorn[standard],
sqlalchemy[asyncio], aiosqlite, alembic,
pydantic, pydantic-settings, python-dotenv,
httpx,
anthropic, openai,
apscheduler,
jinja2,
python-multipart,           # for FastAPI file uploads
structlog,                  # optional
pytest, pytest-asyncio, mypy  # dev
```

## Migration phases (each ends in a working app)

The .NET backend keeps running on `:5249` for the whole migration. The Python backend stands up
on `:5250` until the cutover step. Vite proxy gets one-line update at cutover.

- **P0 — Scaffold + config + DB.** `uv init`, FastAPI app, Pydantic settings loading
  `.env` (verify env vars resolve identically), SQLAlchemy async engine, Alembic init,
  initial migration creating `Project / KnowledgebaseDocument / TestSession / TestCaseDraft /
  BugSession / BugDraft / Report / AppSetting` (mirrors current schema), startup hook that
  ensures the FTS5 virtual table exists, seed `Project id=1 "Your Project"`. App boots on `:5250`.
- **P1 — ADO client + LLM clients + Diagnostics + Settings.** Port `AzureDevOpsClient` (read only
  first: `get_work_item`, `search_work_items`). Three LLM clients via official SDKs +
  provider-state singleton backed by `AppSetting`. `/api/diagnostics` + `/api/settings/provider`
  endpoints. Verify: `/api/diagnostics/workitem/{id}` returns the same JSON shape as .NET; provider
  switch persists across restarts.
- **P2 — Knowledgebase.** Upload → store to `data/kb/{pid}/<guid>-<name>.md`, chunker, insert into
  `doc_chunks` (FTS5), `MATCH ... ORDER BY bm25(...)` retrieval. `/api/knowledgebase/...` endpoints.
  Verify: upload sample `.md`, search returns same hits as .NET on the same content.
- **P3 — Author Tests.** Port `AnalystAgent` + `TesterAgent` (prompts copied verbatim), plus the
  Tester skill loader if a file is present on disk. Port `AgentOrchestrator` with the
  `dict[int, asyncio.Task]` cancellation registry and the per-stage `current_activity` updates.
  ADO write methods (`create_test_case`, `add_test_case_to_suite`, `link_tested_by`) — keep the
  exact JSON shapes already proven (modern `testplan` API with `workItem` wrapper, `op: "add"`).
  Push controller is fire-and-forget. Verify: full session start → review → push against the same
  ticket used in S2 — Test Cases land in the same Suite, same `Tested By` link.
- **P4 — Find Bugs.** Port `BugWriterAgent` (prompt verbatim, the same `FlexibleStringList` JSON
  tolerance), `BugOrchestrator`, `create_bug` + `link_related`. Verify: run against the same
  ticket used in S4 — Bug work items land linked Related to the story.
- **P5 — Reports.** Port `get_tickets_in_range`, stats + grouping, Jinja2 template producing the
  byte-identical (modulo whitespace) HTML. APScheduler Thursday 11AM job. Verify: generate the
  same date range as a .NET-side report and diff the rendered HTML — only timestamps should differ.
- **P6 — Cutover + decommission.** Change Vite proxy target from `:5249` to `:5250`. Smoke-test
  every page. Delete `src/QaAutomation.*` directories + the `.sln`. The PowerShell run commands
  in any README change from `dotnet run --project src/QaAutomation.Api` to
  `uv run uvicorn app.main:app --reload --port 5249`.

## Critical files (representative)

Code most likely to need careful porting (not exhaustive — the pattern repeats):

- `src/QaAutomation.Infrastructure/AzureDevOps/AzureDevOpsClient.cs` — proven JSON shapes for
  ADO writes (`testplan` workItem wrapper, `op: "add"` JSON-patch, Bug severity values, Related
  link rel name). Port these literals verbatim.
- `src/QaAutomation.Infrastructure/Agents/AnalystAgent.cs`, `TesterAgent.cs`, `BugWriterAgent.cs` —
  prompt strings copy verbatim into Python module constants. The JSON-parsing tolerance code
  (`FlexibleStringListConverter`, `JsonDefaults.Lenient`) becomes a small helper module.
- `src/QaAutomation.Infrastructure/Agents/AgentOrchestrator.cs`, `BugOrchestrator.cs` —
  the **scope-per-background-task** lesson translates to "always open a fresh `SessionLocal()`
  inside the asyncio task". The status-machine and `current_activity` strings stay identical
  (they're what the React UI keys off).
- `src/QaAutomation.Infrastructure/Reports/ReportHtmlBuilder.cs` — port the CSS / JS / SVG
  literals into a Jinja2 template; the data-attr selectors the Explore-mode filters key off
  must remain identical.
- `src/QaAutomation.Infrastructure/Reports/WeeklyReportScheduler.cs` — becomes a 20-line
  APScheduler job.

## Reusable utilities found in current code

These translate cleanly and should not be rewritten from scratch:

- ADO error sanitizer (PAT redaction + body truncation) — port to `services/llm/sanitizer.py` and
  reuse from both ADO + LLM error paths.
- Lenient JSON list decoder — port to `services/agents/json_utils.py`.
- Markdown chunker (~1000 char paragraphs) — port to `services/knowledgebase.py`.

## Verification (end-to-end, per phase)

Run both backends side by side and curl-diff representative endpoints:

- **P0**: `curl http://localhost:5250/api/diagnostics` returns `{ activeProvider, adoPatPresent,
  anthropicKeyPresent, azureOpenAiConfigured }` — same keys, same camelCase, same booleans.
- **P1**: `GET /api/diagnostics/workitem/165999` returns identical JSON to the .NET side.
- **P2**: Upload `sample-domain.md` → list shows 1 doc with same `chunkCount` ± 1 (paragraph split
  is deterministic). Search returns the same chunk content.
- **P3**: Run Analyst → Tester on WI 165999 → Approve all → Push. Test cases appear in the chosen
  Suite, linked TestedBy to the story. Diff the resulting work items' field values vs a .NET
  push on a different sandbox ticket — same fields populated, same step XML shape.
- **P4**: Run Bug Writer with a known actual-behavior input. Bug pushed with same severity mapping,
  same repro/expected/actual layout in `SystemInfo`, Related link to story.
- **P5**: Generate a report for a known date range. Open in browser → all sections render,
  filters work, Present mode toggles, donut + bars match counts. Thursday job: confirm
  `apscheduler.print_jobs()` shows the cron entry.
- **P6**: Every page in the React app behaves identically to before. Sessions / drafts / reports
  created pre-cutover are still readable (DB is the same file path).

## Risks & mitigations

- **JSON property casing drift** — frontend keys are camelCase. Mitigation: enforce
  `alias_generator=to_camel, populate_by_name=True` on the Pydantic base, and assert key parity
  in a single integration test per router.
- **Enum string spellings** — UI status chips key off exact strings. Mitigation: use
  `str, Enum` enums so they JSON-serialize as their `.value` — list those values in one place
  and copy them verbatim from the C# enums.
- **Time-zone behavior in the scheduler** — `BackgroundService` used `DateTime.Now` (local).
  APScheduler defaults to UTC. Mitigation: explicitly pass `timezone='Asia/Karachi'` (or whatever
  the user's local is) to the scheduler so the Thursday 11AM trigger fires at the same wall-clock
  moment.
- **SQLite ORDER BY DateTimeOffset bug we hit before** — Python's `datetime` doesn't carry that
  trap. Use UTC `datetime` columns throughout.
- **ADO `testplan` write JSON shape** — already debugged in C# (`workItem` wrapper, `op: "add"`).
  Carry these literals across verbatim; do not "improve" them.
- **Pipeline scope leak** — the equivalent of "scope disposes before background task finishes"
  is "request DB session closes when handler returns." Mitigation: orchestrator background tasks
  always open `async with SessionLocal()` themselves; never pass a request-bound session into a
  `create_task(...)` call.
- **Long-running httpx calls + cancellation** — `httpx.AsyncClient` honors `asyncio.CancelledError`
  on `await`, so Cancel works exactly like CTS today.
- **GitHub Models endpoint quirks** — the `openai` SDK works against `https://models.inference.ai.azure.com`
  with a GitHub PAT as the key. Confirm by porting the Diagnostics LLM ping first (cheapest call).

## Out-of-scope (deferred — same as before)

- Playwright auto-capture for Bug Writer (S4-v2)
- Multi-project support
- Migrating the React/Vite/MUI client (stays exactly as is)
- New features of any kind
