# QA Automation — Migration Plan: Python → .NET backend + target-crm-aligned frontend

**Goal.** Re-platform the QA Automation app (`ADO_Tester`) so it conforms to the YourOrg / *Your Project* org stack and can be hosted as its **own Azure DevOps project**:

- **Backend:** rewrite the Python FastAPI/agent backend in **.NET**, following the CRM monorepo's .NET conventions.
- **Frontend:** keep the existing React client's newer versions, but adopt **target-crm's architecture** (Redux Toolkit + React Query + feature-module folders + centralized axios client + config layer). *Architecture only* — no tooling/i18n/Formik/multi-env parity.
- **Constraint:** preserve all user-facing behavior and the frozen contracts (endpoints, camelCase JSON, enum spellings, table/column names, ADO REST JSON literals, agent prompts). This is the inverse of `docs/python-migration-plan.md`.

This document is the companion/inverse of `docs/python-migration-plan.md`. Read that for the full behavior contract; this plan focuses on the .NET target shape and sequencing.

> **Completeness gate.** A divide-and-conquer audit inventoried 367 feature/contract items of the live app and gap-checked them against this plan (29 covered, 258 under-specified, 80 missing). The per-item, frozen-literal **execution checklist** lives in **`docs/dotnet-migration-parity-checklist.md`** — treat its `- [ ]` items as the definition-of-done for each phase below. The corrections the audit forced are folded into this plan and flagged **[audit]**.

---

## 1. Current vs. target stack

### Backend
| | Current (Python) | Target (.NET, org-aligned) |
|---|---|---|
| Language/runtime | Python 3.12 | **C# / .NET 8** (ASP.NET Core) |
| Web framework | FastAPI + Uvicorn (:5250) | **ASP.NET Core 8 Web API** (App Service) |
| ORM / DB | SQLAlchemy async + aiosqlite + Alembic | **EF Core 8 + SQLite** (EF Migrations) |
| Full-text | SQLite FTS5 (raw SQL, bm25) | SQLite FTS5 (raw SQL via EF `FromSqlRaw`) |
| HTTP client | httpx | `HttpClient` (typed, `IHttpClientFactory`) |
| LLM SDKs | anthropic, openai | OpenAI .NET SDK / `Azure.AI.OpenAI`; `Anthropic.SDK` (or raw `HttpClient`) |
| Scheduler | APScheduler (AsyncIOScheduler) | `BackgroundService` (IHostedService) + Cronos |
| Streaming | SSE (text/event-stream) | SSE (ASP.NET Core response streaming) |
| Background work | `asyncio.create_task` + `session_scope()` | `Task.Run` + `IServiceScopeFactory.CreateScope()` |
| Concurrency gate | `asyncio.Semaphore(4)` | `SemaphoreSlim(4)` |
| Cancellation | `dict[int, asyncio.Task]` | `ConcurrentDictionary<int, CancellationTokenSource>` |
| Config | pydantic-settings (.env) + Settings table | Options pattern (`IOptions<T>`) + `appsettings.{Env}.json` + Settings table |

### Frontend
| | Current (`client/`) | Target architecture (target-crm) |
|---|---|---|
| Build / lang | Vite 8, TS 6, React 19 | **keep** (target uses Vite 6/TS 5.9 — do not downgrade) |
| UI | MUI 9 + Emotion + Framer Motion | **keep** MUI 9 (target is MUI 7 — do not downgrade) |
| Global state | local hooks + 1 ColorMode context | **add Redux Toolkit** (`src/app/store.ts`, typed hooks) |
| Server state | hand-rolled polling hooks + SSE | **add React Query** (`useQuery`/`refetchInterval`/`useMutation`); keep SSE hook |
| API access | inline `axios.get('/api/...')` | **centralized `src/api/axios-client.ts`** + per-feature `api/*` modules |
| Config | none (relative `/api`, Vite proxy) | **`src/config`** singleton over a `VITE_QA_API` base URL |
| Folders | `pages/ + session/ + components/` | **feature modules** `components/<feature>/{api,components,*Slice,index.ts}` + `app/`, `api/`, `routes/`, `common/`, `theme/` |

---

## 2. Key architectural decisions (confirm before build)

### D1 — Hosting model: **ASP.NET Core 8 on Azure App Service** (recommended) vs. Azure Functions
The CRM *microservices* (customer, lead, …) are **Azure Functions v4 in-process (.NET 6)**. But this app's defining traits — minutes-long background pipelines, an **SSE live console**, an in-memory **EventBus ring buffer**, an in-memory **cancellation registry** + **`Semaphore(4)`**, and a **weekly scheduler** — are fundamentally incompatible with Functions-on-consumption (≈230s request cap, no durable in-memory state across scale-in, no native long-lived streaming responses).

> **Recommendation: build as an ASP.NET Core 8 Web API on Azure App Service** (Always On, single instance). This is *already an org pattern* — `YourProduct.WebAPI` is net8 ASP.NET Core on App Service — and it is what the **original** QaAutomation .NET solution was (net8 ASP.NET Core). It preserves SSE + background + in-memory state with the least semantic change and lets us recover the original `QaAutomation.*` code.

**Alternative (only if strict microservice parity is required):** Azure Functions v4 + **Durable Functions** (orchestration/fan-out) + **Azure SignalR Service** (replacing SSE) + a **Timer trigger** (scheduler), with all in-memory state externalized (Durable entities / table storage). This is a materially larger rewrite and changes the live-update transport on the frontend. *Not recommended.*

### D2 — Database: **SQLite + EF Core Migrations** (recommended) vs. Azure SQL
The app is single-user with a tiny schema; SQLite matches the original and the current Python app. Use **EF Core Migrations** (not the original's `EnsureCreated`, and not raw Alembic) so schema changes don't wipe data — this was the exact pain the Python port fixed. **Forbid `EnsureCreated`/`EnsureDeleted`; run `Migrate()` on boot.** Mount SQLite on App Service persistent storage (`/home`). Store enums **as strings** via `.HasConversion<string>()` (EF defaults to int). **[audit] Existing-DB adoption:** the live `qa.db` is Alembic-managed, so its `alembic_version` won't match EF's `__EFMigrationsHistory` — either (a) point EF at the existing file and record a single baseline migration as already-applied (insert into `__EFMigrationsHistory`, no DDL), or (b) start a fresh DB and accept data loss. Decide this in B1. **Org-aligned alternative:** Azure SQL via `UseSqlServer(..., o => o.EnableRetryOnFailure())` (matches CRM convention) — required only if you ever run multi-instance. Recommend SQLite now, keep the provider swappable.

### D3 — LLM clients
Port the provider-agnostic layer behind `ILlmClient.Complete(system, user, model?, progress?, emit?)`:
- **Anthropic** → community `Anthropic.SDK` NuGet, or raw `HttpClient` (the original .NET did raw HttpClient). Model `claude-sonnet-4-6`, max_tokens 4096.
- **AzureOpenAI** → `Azure.AI.OpenAI` (api-version `2024-08-01-preview`).
- **OpenAICompatible** → `OpenAI` .NET SDK with custom `base_url` (default GitHub Models, `gpt-4o-mini`).
- **ClaudeCli** → `System.Diagnostics.Process` with stdin piping + idle timeout. **Note:** the Python Windows "Selector-loop NotImplementedError" workaround is *not needed in .NET* — async process I/O works normally, so this provider is simpler in .NET.

### D4 — Live updates: **keep SSE**
ASP.NET Core streams `text/event-stream` cleanly on App Service. Reuse the exact frame format (`id: <seq>\ndata: <json>\n\n`, `: connected` preamble, `: keepalive` every 15s, `Last-Event-ID` replay from a ring buffer). **[audit] App Service and Front Door buffer responses by default — you MUST set `X-Accel-Buffering: no` and `Cache-Control: no-transform`, or the live console silently never updates in production** (highest-likelihood prod breakage; add a buffering test in M5). The frontend `useEventStream` hook stays as-is. (Switch to SignalR only under the D1 Functions alternative.)

### D5 — Repo & ADO layout: **new standalone repo + ADO project**
Greenfield repo (e.g. `qa-automation`) under a new ADO project, with its own service connection, build definitions, Key Vault, and Bicep `infra/`. Do **not** add it into the CRM monorepo/solution.

---

## 3. Target .NET solution structure

Mirror the **original recoverable Clean-ish layout** (proven, matches the org's `Api`/`Service` split spirit) and layer in org conventions (`AddCustom*` DI extensions, options pattern, exception middleware → Logging API, Swagger, API versioning).

```
qa-automation/
  QaAutomation.sln
  src/
    QaAutomation.Api/                      # ASP.NET Core 8 Web API host
      Program.cs                           # WebApplication + AddCustom* extensions (mirror YourProduct.WebAPI)
      Configurations/
        ConfigurationExtensions.cs         # AddCustomSwaggerGen / CorsPolicy / Versioning / DbContext / Services
        *Options.cs                        # strongly-typed [Required] options (bound + ValidateDataAnnotations)
      Middlewares/ExceptionHandlingMiddleware.cs   # global catch → (optional) Logging API + ProblemDetails
      Controllers/                         # 7 controllers (see §4)
        DiagnosticsController.cs  SettingsController.cs  KnowledgebaseController.cs
        AuthorTestsController.cs  FindBugsController.cs  ReportsController.cs  BatchesController.cs
      Streaming/SseWriter.cs               # text/event-stream helper
      appsettings.json + appsettings.{Development,QA,Staging,Production}.json
    QaAutomation.Core/                     # contracts, domain, options, DTOs
      Abstractions/  ILlmClient ILlmProviderState IAzureDevOpsClient IKnowledgebaseService
                     IAgentOrchestrator IBugOrchestrator IReportService IEventBus
      Configuration/ AzureDevOpsOptions AnthropicOptions AzureOpenAiOptions OpenAiCompatibleOptions
                     ClaudeCliOptions StorageOptions SchedulerOptions
      Models/        EF entities (Project, AppSetting, Document, TestSession, TestCaseDraft,
                     BugSession, BugDraft, Report, Batch) + enums + FlexibleStringListConverter
      Contracts/     request/response DTOs (camelCase) + CamelCase base + {total,items} paging
    QaAutomation.Infrastructure/
      Data/          QaDbContext (EF Core 8 + SQLite) + Migrations/ + FtsBootstrapper (raw FTS5)
      AzureDevOps/   AzureDevOpsClient.cs  (frozen REST + JSON shapes) + AdoApiError + Sanitizer
      Llm/           AnthropicLlmClient AzureOpenAiLlmClient OpenAiCompatibleLlmClient
                     ClaudeCliLlmClient LlmProviderState LlmClientFactory
      Agents/        AnalystAgent TesterAgent BugWriterAgent CodeVerifierAgent + JsonUtils
      Orchestrators/ AgentOrchestrator BugOrchestrator + PipelineConcurrency(Semaphore) + CancellationRegistry
      Knowledgebase/ KnowledgebaseService (chunker + FTS5 + bm25)
      Reports/       ReportService ReportHtmlBuilder ReportSummarizer WeeklyReportScheduler(BackgroundService) MockReport
      Events/        EventBus (Channels + ring buffer) + ConsoleEvent
      DependencyInjection.cs               # AddInfrastructure() registers all of the above
    QaAutomation.Tests/                    # xUnit + Moq + FluentAssertions (org test stack)
  client/                                  # restructured React app (see §5)
  infra/                                   # Bicep (see §6)
  build/                                   # build-branch/develop/qa/main.yml (see §6)
```

**Org conventions to adopt** (from `YourProduct.WebAPI` + `YourProduct.Shared`): `Program.cs` reads as a list of `AddCustom*`/`Configure*` one-liners; strongly-typed `required`/`[Required]` options with `ValidateDataAnnotations()`; `nameof`-based config-key constants; `ExceptionHandlingMiddleware` registered first; Swagger w/ Bearer; EF `EnableRetryOnFailure` (if SQL Server). **[audit] Do NOT adopt the org's URL-segment API versioning** (`api/v{version}/...`) — routes must stay **flat under `/api/<router>`** to match the contract the React client hardcodes; versioning here 404s every call. Use `IOptionsMonitor`/a mutable settings snapshot (not immutable `IOptions`) so UI settings edits rebuild the ADO/LLM clients at runtime, and re-read the PAT per request (IHttpClientFactory pools handlers → stale-PAT 401s otherwise). **Skip** the org's anti-patterns: committed plaintext secrets (use Key Vault / env vars), and the obsolete in-process `FunctionBase`. The centralized **Logging API** integration is optional for a standalone tool (recommend local `ILogger` + App Insights; wire the Logging API only if required for org parity).

---

## 4. Backend migration — phased, with recover-vs-new map

The original `QaAutomation.*` tree was deleted at the Python cutover; **recover it from git** (`git show 7b16806:src/...`) as scaffolding, then bring it to feature-parity with today's Python app.

**Already existed in .NET (recover & modernize):** `AzureDevOpsClient` (get/search work items, test plans/suites, create_test_case, add_to_suite, link_tested_by, create_bug, link_related, get_tickets_in_range + frozen JSON), `Analyst`/`Tester`/`BugWriter` agents + lenient JSON converter, `AgentOrchestrator` + `BugOrchestrator` (sans code-verify), `KnowledgebaseService` (chunker+FTS5+bm25), 3 LLM clients + `LlmProviderState`, `ReportService`+`ReportHtmlBuilder`+`WeeklyReportScheduler`, the 6 controllers, the sanitizer, all base entities, ProblemDetails + `{total,items}`.

**New in the Python era (green-field, no .NET predecessor):** `CodeVerifierAgent` + the entire anchor-on-change code-discovery (imports/callers/triage, byte budgets, pass-1/pass-2 verdicts) + `Verifying` status + `bug-from-verdict`; **Batches** (Epic/Feature fan-out, `BatchesController`, derived rollup, AUTO/Manual); **Reports v2** (Theme, AI Summarizer, spotlight enrichment, preview/mock); **SSE EventBus** + `EventLogJson` columns + `/stream` endpoints; **ClaudeCli** 4th provider; **AppSettingsService** UI-editable layer + ADO discovery (orgs/projects/area-paths); the new ADO Git/Code-Search reads.

### Phases
- **B0 — Solution skeleton.** Create solution + 4 projects; `Program.cs` with `AddCustom*` extensions, Swagger, versioning, CORS (`:5173`,`:5174`), ProblemDetails, exception middleware, `GET /api/health`. Recover original `QaAutomation.Core` options/models as the starting point.
- **B1 — Data layer.** EF Core 8 `QaDbContext` + entities with **frozen PascalCase table/column names** (the initial Alembic schema `1e39a44b971c` is the baseline) + enums with **exact string spellings**. First EF Migration. `FtsBootstrapper` creates the `doc_chunks` FTS5 vtable at startup. Seed `Project Id=1 'Your Project'`.
- **B2 — ADO client (contract-frozen).** Port `AzureDevOpsClient` verbatim re: URLs, api-version 7.1, Basic auth, Steps XML (byte-identical), json-patch bodies, severity map (`1 - Critical`…), standing house-style bug fields (AreaPath, Custom.QA, Custom.FoundWith, etc.), WIQL date-only, sanitizer. Include the new Git/Code-Search reads.
- **B3 — LLM layer.** `ILlmClient` + 4 providers + factory + `LlmProviderState` (persist `llm.provider` in Settings). Streaming → `progress`/`emit` callbacks.
- **B4 — Agents + JsonUtils.** Port the **verbatim** Analyst/Tester/BugWriter/CodeVerifier system prompts (house style — character-for-character). **[audit] The live Python prompts are authoritative — diff the recovered .NET (`7b16806`) against them, never trust it (it predates today's house style).** Also freeze the two orchestrator-embedded prompts (code-discovery **triage** + verify_steps `_ACTUAL_RULE_FULL`/`_LITE`) and the **Reports "Insights" summarizer** prompt. Lenient JSON parse (fence strip, **string-aware** bracket extract, 3× retry, string coercion) applies to **Analyst/Tester/BugWriter only** — **CodeVerifier `verify()` is single-shot and raises on bad JSON; `verify_steps()` never raises** (degrades to all-inconclusive). See checklist §3.
- **B5 — Orchestrators + EventBus + concurrency.** Background pipeline pattern (`Task.Run` + scoped DbContext — **never** the request scope), `ConcurrentDictionary` cancellation registry, `SemaphoreSlim(4)`, status-machine + `CurrentActivity` strings (load-bearing), `EventLogJson` persistence, EventBus (Channels + ring buffer), SSE endpoints. Author-Tests + Find-Bugs (incl. code-verify) + bug-from-verdict.
- **B6 — Knowledgebase.** Upload (.md only, 10MB cap), chunker (~1000 chars), FTS5 bm25 search, delete (idempotent).
- **B7 — Reports + scheduler.** Generation flow, themes, AI summarizer, HTML builder (hand-rolled SVG donut/bars), preview/mock; `WeeklyReportScheduler` as a `BackgroundService` firing **Thu 11:00 Asia/Karachi** (explicit `TimeZoneInfo`; Cronos for the cron), honoring settings; no missed-slot catch-up.
- **B8 — Batches.** `children-expand` BFS, fan-out start (`Task.WhenAll`), derived status rollup, AUTO/Manual.
- **B9 — Settings/Diagnostics + AppSettings layer.** UI-editable settings over options (mask secrets, empty=unchanged, rebuild ADO/LLM clients on change), ADO discovery endpoints, diagnostics (provider/key presence, work-item read, LLM ping, claude-cli availability).
- **B10 — Tests + parity verification.** xUnit/Moq/FluentAssertions; **golden-file tests** for the frozen literals (Steps XML, bug ReproSteps HTML, json-patch bodies, WIQL) compared against the Python output; endpoint contract tests (paths, status codes, camelCase, enum spellings).

---

## 5. Frontend migration — architecture alignment (architecture only)

Keep MUI 9 / Vite 8 / TS 6 / React 19 / Framer Motion. Restructure to target-crm's layering and introduce RTK + React Query. The current `session/` config-driven engine maps cleanly onto a feature module.

**Target `client/src` shape:**
```
src/
  app/        store.ts (configureStore, typed reducers) · hooks.ts (useAppDispatch/useAppSelector) · index.ts barrel
  api/        axios-client.ts (single instance + interceptors) · index.ts (get/post/put/patch/del)
  config/     config.ts (typed object over import.meta.env.VITE_QA_API) · index.ts
  routes/     Routes.tsx (route table + layout wrappers) · index.ts
  common/     components/ hooks/ models/ utils/  (shared, feature-agnostic — incl. useEventStream, motion primitives)
  theme/      index.ts (createAppTheme) · tokens/palette/components · ColorModeContext
  components/
    author-tests/  { api/author-tests-api.ts, components/{AuthorTests.tsx, StartForm, DetailBody}, authorTestsSlice.ts, index.ts }
    find-bugs/     { api/find-bugs-api.ts, components/..., findBugsSlice.ts, index.ts }
    batches/       { api/batches-api.ts, components/..., index.ts }
    knowledgebase/ { api/..., components/..., index.ts }
    reports/       { api/..., components/..., index.ts }
    settings/      { api/..., components/..., index.ts }
    layouts/       { Layout (AppBar + Drawer) }
```

**Mapping rules (from target-crm conventions):**
- **API layer = pure, typed, no UI.** Each `components/<feature>/api/<feature>-api.ts` exports a named object of `async` methods that call `{ get, post, put, patch, del }` from `api/`, build the URL from `config.get('api.qa')`, and return typed data. No auth handling in feature APIs (interceptors do it). No data transforms in components.
- **React Query owns server state.** Replace `useSessionsLive`/`useSessionDetail` (`setInterval` polling) with `useQuery` + `refetchInterval` (gated while a session is in-flight) + `useMutation` + `queryClient.invalidateQueries`. Array query keys scoped by id (e.g. `['author-tests','sessions',projectId]`).
- **Redux owns global UI state** (selection, dialogs, color mode, cross-feature flags). Never store server data that React Query can own. Keep the config-driven `SessionModuleConfig` idea but feed it RTK/RQ instead of bespoke hooks.
- **SSE stays custom** (`common/hooks/useEventStream.ts`) — no React Query equivalent; optionally push frames into `queryClient.setQueryData`.
- **Provider shell** in `app/` mirrors target-crm: `ErrorBoundary > QueryClientProvider > Redux Provider > ThemeProvider > BrowserRouter > Routes`.
- **Base URL via config.** Introduce `VITE_QA_API` so the client can address the new App Service backend per environment (replaces the hardcoded relative `/api` + dev proxy; keep the Vite proxy for local dev only).

### Frontend phases
- **F0** — add deps (`@reduxjs/toolkit`, `react-redux`, `@tanstack/react-query`), scaffold `app/`, `api/`, `config/`, `common/`, `routes/`; mount providers in `main.tsx`.
- **F1** — centralized `axios-client` + `config`; migrate all inline `axios('/api/...')` calls into per-feature `api/*` modules (behavior-preserving).
- **F2** — introduce React Query; replace polling hooks feature-by-feature (Author Tests first, then Find Bugs, Batches, Reports, KB, Settings); keep `useEventStream`.
- **F3** — introduce Redux slices for cross-cutting UI state; move `ColorMode` into the store or keep as context (allowed by target-crm).
- **F4** — relocate `pages/` + `session/` into `components/<feature>/` modules with barrels; central `routes/Routes.tsx`.
- **F5** — point `config` at the new backend; verify against the .NET API.

*(Per your scope: no Prettier/ESLint-rule parity, no `.env.<mode>` 7-mode scheme, no `.github` copilot-instructions, no i18n/Formik.)*

---

## 6. Hosting & CI/CD on Azure DevOps (separate project)

Mirror the CRM's **no-container** approach (zip deploy + Bicep + Front Door; no Docker/K8s).

**Backend (App Service)** — pipeline modeled on `YourProduct.WebAPI/BuildPipelines/azure-pipelines-qa.yml`:
`UseDotNet@2 (8.x)` → `DotNetCoreCLI@2` restore/build/test → `publish --configuration Release --output ... -p:` → zip → deploy via **`AzureWebApp@1`/`AzureRmWebAppDeployment@4`** to an App Service (instead of `AzureFunctionApp@2`). One YAML per branch (`build-branch/develop/qa/main.yml`) with path-filtered triggers.

**Frontend (Storage static website)** — modeled on `web-app/build/build-branch.yml`: `NodeTool@0 (18.x)` → `npm ci` → `vite build` (with `VITE_QA_API` per env) → `AzureCLI@2 az storage blob upload-batch --destination '$web'`. **[audit] Set the static-website 404 document to `index.html`** (SPA fallback) or every BrowserRouter deep link/refresh 404s.

**Infra (`infra/` Bicep)** — copy the CRM module pattern: `appServicePlan.bicep` (B1/Always On), `webApp.bicep` (`kind=app`, `netFrameworkVersion v8.0`), `storageAccount.bicep` (static site), `appInsights.bicep`, secrets via **Key Vault references + User-Assigned Managed Identity**. `frontDoorRoutes.bicep`: route `/qa-api/*` → App Service, `/*` → static site (or stand up a small dedicated Front Door / use App Service default hostname for an internal tool).

**ADO project prerequisites** (one-time): new repo + project, ARM **service connection**, build definitions (one per `build-*.yml`), a **Key Vault** seeded with `ADOACCESS_PAT`, `ANTHROPIC_API_KEY`, Azure OpenAI + OpenAI-compatible keys, UAMI with KV + RG access, and a PAT for any pipeline-trigger scripts. GitVersion optional (the CRM computes it but doesn't consume it — wire `$(GitVersion.SemVer)` into publish args only if you want real versioning).

> For an internal single-user tool, a single App Service + App Insights + Key Vault (no Front Door, no per-branch ephemeral envs) is sufficient; scale up to the full branch-env + Front Door pattern only if org parity demands it.

---

## 7. Contract-freeze rules (do NOT "improve")

The Python app is the authoritative contract. The .NET rewrite must reproduce **verbatim**:
1. **Endpoint paths, HTTP methods, status codes** (incl. `202` for async starts) under `/api`.
2. **camelCase JSON** (System.Text.Json camelCase policy) + `{ total, items }` paging + `{ detail }`/ProblemDetails errors. **[audit] Carve-outs:** Settings `GET/PUT /config`, ADO-discovery, and `claude-cli/models` return **literal dotted-key dicts** (`ado.organization`) and must bypass the camelCase policy; ADO json-patch bodies are serialized **raw** (never via the global STJ options, which would corrupt `/fields/...` paths).
3. **Enum string spellings** (`SessionStatus`, `BugSessionStatus` incl. `AwaitingActual`/`Verifying`, `DraftStatus`, **`BugDraftStatus`** (distinct type), `BugSeverity`, `ReportStatus`/`Theme`, `LlmProvider` incl. `ClaudeCli`, `BatchModule`=`author-tests`/`find-bugs`, `BatchMode`=`auto`/`manual`). **[audit] Serialize via `JsonStringEnumConverter` as the exact NAME — NOT int and NOT camelCased; use `[JsonStringEnumMemberName]` for the kebab/lowercase values. Verdict statuses `pass|fail|inconclusive` are also frozen.**
4. **Table/column names** (PascalCase EF defaults = original schema).
5. **ADO REST JSON literals** — Steps XML (byte-identical), json-patch op bodies, testplan `workItem` wrapper, rel names (`Microsoft.VSTS.Common.TestedBy-Reverse`, `System.LinkTypes.Related`), severity map, standing bug fields, bug ReproSteps HTML (incl. `OBSERVATION` block), WIQL **date-only** on `[System.ChangedDate]`, `System.ClosedDate` **omitted** (TF51535).
6. **Agent system prompts** — character-for-character (house style; mirrors the `targetcrm-bug-suite` gold reference).
7. **`CurrentActivity` strings + SSE frame shape** (UI keys off them).
8. Scheduler at **Thu 11:00 Asia/Karachi**; **UTC** datetime columns throughout; background tasks open their **own** DB scope; ports backend `5250` / client `5173`.

A golden-file test suite (B10) should diff .NET output against the Python output for items 4–7.

---

## 8. Sequencing & milestones

The backend and frontend tracks are largely independent and can run in parallel; the frontend's F5 (point at .NET) gates on backend B0–B9.

1. **M1 — Backend skeleton + data + ADO + LLM + agents** (B0–B4): can author/analyze a story to JSON; no persistence-of-pipelines yet.
2. **M2 — Author Tests end-to-end** (B5 author half + B6 KB + B9 settings/diagnostics): parity with the original .NET surface; push test cases to ADO.
3. **M3 — Find Bugs + code verify + reports + batches + SSE** (B5 bug half, B7, B8): full feature parity with today's Python app.
4. **M4 — Frontend architecture alignment** (F0–F4) against the still-running Python backend.
5. **M5 — Cutover**: point frontend at .NET (F5), golden-file/contract verification (B10), deploy to ADO (App Service + static site).

**Rough effort (one engineer, indicative):** B0–B4 ~1.5–2 wks · B5 ~1.5 wks · B6–B9 ~2 wks · B10 ~1 wk → backend ≈ 6–7 wks. Frontend F0–F5 ≈ 2–3 wks (parallelizable). CI/CD + infra ≈ 1 wk. The code-verify discovery (B5) and the frozen ADO literals (B2) are the highest-risk items.

---

## 9. Risks & mitigations
- **Hosting mismatch (D1).** Forcing Functions breaks SSE/background/in-memory state. → ASP.NET Core on App Service; confirm D1 first.
- **Frozen-literal drift.** Any change to Steps XML / json-patch / prompts silently breaks ADO writes or house style. → golden-file tests vs Python output; copy literals, don't retype.
- **LLM streaming parity.** .NET SDK streaming differs from Python. → wrap each provider behind `ILlmClient` with `emit`/`progress`; test token deltas reach SSE.
- **SQLite on App Service.** Local disk persistence caveats. → use `/home` persistent storage; keep provider swappable to Azure SQL (D2).
- **Scheduler timezone.** APScheduler vs `BackgroundService` defaults differ (UTC). → explicit `TimeZoneInfo.FindSystemTimeZoneById` for Asia/Karachi; Cronos for cron.
- **Secrets.** Don't copy the CRM's committed-plaintext appsettings. → Key Vault + env vars only.

---

## 10. Open decisions to confirm
- **D1:** ASP.NET Core 8 / App Service (**recommended**) vs. Azure Functions + Durable + SignalR (strict microservice parity). *This gates the whole backend shape.*
- **D2:** SQLite (**recommended**) vs. Azure SQL.
- **D3:** Anthropic via `Anthropic.SDK` NuGet vs. raw `HttpClient` (original approach).
- **D5/CI:** full org hosting parity (Front Door + per-branch ephemeral envs) vs. a single App Service for an internal tool (**recommended**).
- **D6 [audit]:** existing-DB adoption — point EF at the live `qa.db` with a baseline migration recorded as applied (**recommended**, preserves data) vs. fresh DB (data loss).
- **Logging API:** wire the org's centralized Logging API, or use local `ILogger` + App Insights (**recommended** for a standalone tool)?

> **Binding contract source.** The complete endpoint list and every frozen literal are enumerated in `docs/dotnet-migration-parity-checklist.md` + `docs/python-migration-plan.md`. Where any §1–§9 statement here conflicts with the live Python behavior, **the Python app wins** and the checklist is the tie-breaker.
