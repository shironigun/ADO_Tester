# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Locally-hosted QA-automation tool ("ADO_Tester"): turns Azure DevOps stories into **test cases**
and **bug reports** in a specific QA author's house style, plus a weekly HTML board-activity report.
Backend is the "brain" that reads ADO work items, runs LLM agents, and writes artifacts back to ADO.

**Stack:** .NET 8 (ASP.NET Core) + EF Core 8 + PostgreSQL (`tsvector` FTS for KB retrieval) backend in `backend/`
(port **5250**); React 19 + Vite + MUI + Redux Toolkit + React Query frontend in `client/` (port **5173**).
You open one URL — `http://localhost:5173` — and Vite proxies `/api/*` to `:5250`.

> Re-platformed from a Python FastAPI app (retired 2026-06-21). The .NET backend is now the sole
> backend. Recover the old Python source from git history if a parity reference is ever needed.

## Commands

All backend commands run from `backend/` (or pass `--project backend\src\QaAutomation.Api` from root).

```powershell
# Backend
dotnet run --project src\QaAutomation.Api    # serve on http://localhost:5250 (Swagger at /swagger)
dotnet build                                  # or: dotnet build QaAutomation.sln
dotnet test                                   # all xUnit tests
dotnet test --filter "FullyQualifiedName~AgentOrchestratorTests"   # one class

# EF migrations (auto-APPLY on next backend start — you only generate them)
dotnet ef migrations add DescribeTheChange `
  --project src\QaAutomation.Migrations.Npgsql --startup-project src\QaAutomation.Api

# Frontend (from client/)
npm install
npm run dev        # Vite dev server on :5173, proxies /api → :5250
npm run build      # tsc -b && vite build
npm run lint       # eslint .
npm run preview    # serve the built bundle on :4173

# Wipe local artifacts (KB uploads + reports + Playwright artifacts). Keeps .env and PostgreSQL.
Remove-Item -Recurse -Force backend\data
# Wipe the Docker PostgreSQL database too (or drop the database manually):
docker compose down -v
```

There is **no separate DB migration/seed step** for running — on first start `DatabaseInitializer`
applies PostgreSQL migrations, ensures the `tsvector` full-text schema, and seeds Project id=1.

> On this machine the `dotnet` SDK is not on PATH — see the `dotnet-build-environment` memory for the
> wrapper/invocation to use before running the commands above.

## Architecture (the parts that span files)

**Four projects, clean layering** (`backend/QaAutomation.sln`):
- `QaAutomation.Core` — abstractions (`Abstractions/I*.cs`), options (`Configuration/`), entities + DTOs
  (`Models/`), request/response contracts (`Contracts/`), `Agents/JsonUtils`. No implementation deps.
- `QaAutomation.Infrastructure` — implements everything: EF `Data/`, ADO client, LLM providers, agents,
  orchestrators, events, KB, reports, batches, settings, logging. **DI composition root is
  `Infrastructure/DependencyInjection.cs` (`AddInfrastructure`)** — start there to see how it's wired.
- `QaAutomation.Api` — thin host. `Program.cs` is deliberately just `AddCustom*`/`Configure*` one-liners
  (house style; real wiring lives in `Api/Configurations/ConfigurationExtensions.cs` + the Infra root).
  Flat `/api` controllers, `Middlewares/`, SSE in `Streaming/`.
- `QaAutomation.Tests` — xUnit (golden-file + parity + unit tests).

**Two agent pipelines, driven by singleton orchestrators:**
- `AgentOrchestrator` (test authoring): `Analyst` → `Tester` → `TestCaseDraft`s → human review → push
  to an ADO Test Plan/Suite (creates Test Case, adds to suite, links Tested-By to the story).
- `BugOrchestrator` (bug finding): `Analyst` → `BugWriter` → reviewed Bug work items.
- Agents (`Infrastructure/Agents/`: `AnalystAgent`, `TesterAgent`, `BugWriterAgent`, `CodeVerifierAgent`)
  each wrap an `ILlmClient` and stream output. System prompts are **embedded resources extracted
  byte-exact** from the Python original (`AgentPrompts`; regenerate via `write_prompts.py`).

**Background-work pattern (important):** orchestrators are singletons; an HTTP call seeds a `TestSession`/
`BugSession` synchronously, returns its id, then runs the pipeline on a **background `Task` with its OWN
DI scope** — never the request scope (which disposes when the handler returns). Live progress is pumped
to `IEventBus` (in-memory pub/sub) and streamed to the browser via **SSE**; the durable narrative is
persisted to the session's `EventLogJson`. Cancellation flows through `CancellationRegistry`; a global
concurrency cap lives in `PipelineConcurrency`.

**LLM layer (runtime-switchable):** `ILlmClientFactory` builds a **fresh client per call** (no caching).
The active provider is persisted in PostgreSQL's `Settings` table and switched live from the AppBar.
Providers in `Infrastructure/Llm/`: **`ClaudeCli` (default** — spawns the local `claude` process and
streams stdout; no API key/cost), `CopilotCli`, `CursorCli` (local `agent` CLI; see
`docs/setup/cursor-cli.md`), `Anthropic` (raw `HttpClient`), `AzureOpenAi`, `OpenAiCompatible`
(GitHub Models = free). The AppBar shows display-only model/effort/context chips for the active
provider (edit in Settings). Known limitation: the Claude CLI subscription hits usage limits and fails on
very large generations (`manual-verification-and-open-issues.md`, Open Issue #1) — for big tickets prefer
a paid API provider or chunk the work.

**ADO client:** `AzureDevOps/AzureDevOpsClient.cs` (singleton; PAT Basic auth via `AdoAuthHandler`;
60s work-item cache). `IAzureDevOpsClient` is the full surface: reads (work items, children/epics, test
plans/suites, comments/updates, Git refs/diffs/commits, code search), and **writes** (create Test Case,
add to suite, link Tested-By, create Bug, link Related). It already has the Git/code-read methods
(`GetLinkedChangesAsync`, `GetPullRequestDiffAsync`, `SearchCodeAsync`, `GetFileContentAsync`, …) used
for code-aware analysis.

**Settings (two layers):** `.env` (flat Python-style names like `ADOACCESS_PAT`) bridges into strongly-typed
`Options` via `PostConfigure` in `DependencyInjection.cs`; then a **`RuntimeSettings` overlay**
(DB-persisted, UI-edited) `PostConfigure`s *after* it, so a saved DB value wins over `.env`.
`AppSettingsService.ApplyAsync()` applies overrides at startup. Add a new setting in both places.

**Data:** `QaDbContext` (EF Core 8 / PostgreSQL). `DatabaseInitializer` applies migrations, calls the
PostgreSQL FTS provider's `EnsureSchema` for the `tsvector`/GIN indexes, and seeds Project id=1.
Migrations apply on startup. The git-ignored `backend/data/` holds KB `.md` uploads, generated report
HTML, and Playwright artifacts; database data lives in PostgreSQL.

**Frontend:** `client/src` → `api/` (axios client per controller, shared `client.ts`), `app/` (Redux
`store`, `colorModeSlice`, React Query `queryClient`, typed `hooks`), `pages/`, `routes/` (`AppRoutes` +
central `paths.ts`), `session/configs`, `theme/`. Pages map 1:1 to controllers: Settings, Knowledgebase,
Author Tests, Find Bugs, Reports, Diagnostics.

## Conventions & gotchas

- **Byte-exact parity is load-bearing.** ADO write bodies (Steps XML, Bug ReproSteps/AC/SystemInfo HTML,
  json-patch op order, integer Priority), per-provider LLM error prefixes, and agent prompts were verified
  byte-for-byte against the retired Python via golden-file diffs (`AdoGoldenTests`, `tests/golden/`) and
  adversarial audits. **Don't casually edit these** — run the golden tests. Intentional, documented
  deviations are listed in `backend/README.md` (B2–B4 sections).
- `ExceptionHandlingMiddleware` is registered **first** so it wraps everything; errors return ProblemDetails.
- Keep `Program.cs` as one-liners; put real setup in `ConfigurationExtensions` / the Infra DI root.
- Secrets load from the repo-root `.env` (DotNetEnv walks up the tree). Never commit `.env` or personal
  data. CORS allows only `:5173`/`:5174`.
- For manual ADO checks, use a dedicated test work item in your own ADO project.

## Docs

- `README.md` (root) — full setup/run guide and troubleshooting.
- `backend/README.md` — backend internals, migration phase log (B0–B5+), and accepted parity deviations.
- `docs/dotnet-migration-spec.md` — authoritative brief + locked decisions (referenced as "spec §N" in code).
- `docs/manual-verification-and-open-issues.md` — living verification log + known open issues.

## Documentation discipline (standing rule)

Every feature or non-trivial change is tracked under `/docs` so future work needs little searching.

- **Structure** (predictable subfolders): `docs/features/` (one page per feature: what/why + data flow +
  key files), `docs/architecture/` (cross-cutting design notes), `docs/changelog/` (dated entries, newest
  first), `docs/progress/` (live per-effort task trackers for in-flight work). Existing top-level docs stay.
- **Update *after* verification, not before.** A feature's `docs/features/*` page and a `docs/changelog/`
  entry are written/updated only once the change is implemented **and** verified (build + tests, or the
  agreed verification for that unit). In-flight trackers under `docs/progress/` are updated every iteration.
- **Keep it current.** When behavior changes, update the relevant doc in the same change — never leave a
  feature doc describing the old behavior.

## Ralph loop execution (standing rule)

When executing a **Ralph loop** (self-continuing, disk-tracked iterative build), follow the ruleset in
`docs/progress/RalphLoop/<feature>/Ralph loop.md` **every iteration** — it is the source of truth, not
memory. Each feature run gets its own subfolder under `docs/progress/RalphLoop/` containing that
ruleset plus the standard doc set.

- **Doc creation sequence:** `solution.md` (design) → `blueprint.md` (file-by-file implementation guide)
  → `prd.md` (divide into `ID-###` tasks with acceptance criteria) → `progress.md` (track execution).
  Design first, then derive the task backlog, then track.
- **Per-iteration cycle:** read `progress.md` for the resume point → pick the highest-priority
  incomplete `[ ]` task from `prd.md` → read existing code → implement **one** task → test + verify
  (`dotnet build`/`dotnet test`, `npm run build`/`npm run lint`) → flip `[ ]`→`[x]` in `prd.md` →
  append what/why to `progress.md` → commit only verified chunks → loop.
- **Rules:** ONE task per iteration; `progress.md` is memory between iterations and is always updated;
  **never commit failing code**; if a task is `[x]` in `prd.md` but not-started in `progress.md`, treat
  it as **not started**; if stuck after 3 attempts, log the blocker in `progress.md` and move on.
- **Reference example:** `docs/qa-loop-reshape/` (a completed run's full doc set + `Ralph loop.md`).
