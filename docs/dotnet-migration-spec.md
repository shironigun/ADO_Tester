# QA Automation — Python→.NET + Frontend-Realign Migration Spec (single source)

> **Status — migration executed (as of 2026-06-22).** This spec has been carried out: the .NET solution scaffolded under `dotnet/` now lives in **`backend/`** and is the sole backend; the Python FastAPI app was **retired 2026-06-21** (recover from git history for a parity reference). References below to "the live Python is the source of truth" are now historical — the contract they protect is preserved by the golden-file tests captured from Python and the frozen literals already ported. Live runtime status + open issues: [`manual-verification-and-open-issues.md`](manual-verification-and-open-issues.md).

> **This is a self-contained execution spec.** Hand it to an implementing agent as the authoritative brief. It merges the migration strategy and a divide-and-conquer parity audit (367 contract items: 29 covered / 258 under-specified / 80 missing), and was then **adversarially re-verified against the live code** (a second pass; ~47 corrections folded in). It supersedes `docs/dotnet-migration-plan.md` and `docs/dotnet-migration-parity-checklist.md` (kept only as provenance).
>
> **Deep rendering/HTML literals** (report-deck slide headings, modal markup, CSS, etc.) are intentionally summarized, not transcribed in full — Ground Rule #1 governs them: **copy them verbatim from the live Python and golden-diff (§8), never from prose.**

---

## 0. Mission & non-negotiable ground rules

**Mission.** Re-platform the QA Automation app (`ADO_Tester`) to the YourOrg / *Your Project* org stack and host it as its **own Azure DevOps project**:
- **Backend:** rewrite the Python FastAPI/agent backend in **.NET** (recommended: **ASP.NET Core 8 Web API on Azure App Service**).
- **Frontend:** keep the React client's *newer* versions; adopt **target-crm's architecture only** (Redux Toolkit + React Query + feature-module folders + centralized axios client + config layer). No tooling/i18n/Formik/multi-env/.github parity.

**Ground rules (read before writing any code):**
1. **The live Python backend is the authoritative source of truth.** Where this spec and the Python code disagree, the Python code wins. The recovered .NET at git `7b16806` is *scaffolding only* — it predates today's house style and ~60% of features; **diff it against Python, never trust it**.
2. **Preserve all user-facing behavior and every frozen contract** verbatim: endpoint paths, HTTP methods + status codes, camelCase JSON, enum string spellings, table/column names, ADO REST JSON literals, agent prompts, `CurrentActivity`/SSE strings, scheduler timing. Do **not** "improve" any literal.
3. **Behavior parity is verified by golden-file diffing** the .NET output against the Python output (§8). A phase is not done until its 🔴 items pass.
4. Legend used throughout: 🔴 = breaks at runtime if wrong · 🟠 = silent behavior drift · 🟢 = hygiene.

---

## 1. Source material

| Source | Use |
|---|---|
| Live Python backend `backend/app/**` | **Authoritative contract.** All literals below are extracted from it; re-verify against it. |
| `docs/python-migration-plan.md` | The original .NET→Python port plan (referenced for provenance; may not be present in-repo). **Endpoint bindingness lives in §5.4 of THIS spec, not there.** |
| Git commit `7b16806` (`git show 7b16806:src/...`) | Recoverable original .NET (net8 Clean Architecture, EF Core 8 + SQLite). Scaffold ~40% of the backend. |
| `README.md`, `backend/README.md`, `.env.example` | Feature/page map, run/ops, secrets, Windows gotchas. |
| Target-crm `…/web-app/src/target-crm` | Frontend architecture reference (RTK + React Query + feature modules + `src/api` axios client + `src/config`). |
| CRM monorepo `YourProduct.WebAPI` | The org's ASP.NET Core 8 / App Service pattern (`AddCustom*` DI, options, exception middleware, Swagger). |

**.NET-recoverable vs Python-new:** Recover from `7b16806` → AzureDevOpsClient (core writes/reads), Analyst/Tester/BugWriter agents + lenient-JSON converter, AgentOrchestrator + BugOrchestrator (no code-verify), KnowledgebaseService, 3 LLM clients + provider state, ReportService + HtmlBuilder + WeeklyReportScheduler, the 6 controllers, sanitizer, base entities. **Green-field (no .NET predecessor):** CodeVerifier + the whole code-discovery/verify subsystem, Batches/Epic fan-out, Reports v2 (Theme/Summarizer/spotlight/preview), SSE EventBus + `EventLogJson` columns, ClaudeCli provider, AppSettingsService UI-editable layer + ADO discovery endpoints, EF Migrations.

---

## 2. Stack & hosting decisions — CONFIRMED 2026-06-18 (see §10 for the locked answers)

- **D1 — Hosting (recommended: ASP.NET Core 8 on Azure App Service).** The CRM *microservices* are Azure Functions v4 in-process (.NET 6), but this app is long-running + stateful + streaming (minutes-long pipelines, **SSE live console**, in-memory **EventBus / cancellation registry / `Semaphore(4)`**, weekly cron) — incompatible with Functions-on-consumption (~230s cap, no durable in-memory state, no native streaming). App Service is *already an org pattern* (`YourProduct.WebAPI` is net8 ASP.NET Core on App Service) and is what the original QaAutomation .NET was. **Alternative (not recommended):** Functions + Durable Functions + Azure SignalR (replaces SSE) + Timer trigger, all in-memory state externalized — much larger rewrite. **CONFIRMED 2026-06-18 — ASP.NET Core 8 (App-Service-compatible), but run LOCALLY for development now** (Kestrel :5250, SQLite file, local `.env`/User Secrets — zero Azure cost). The company will deploy to Azure App Service **later**, so keep the architecture deploy-ready; the §7 Azure pieces (Bicep/Front Door/Key Vault/App Insights/Storage/CI) are **DEFERRED, not deleted** — do NOT build any Azure infra now. (NOT Functions.)
- **D2 — DB (recommended: SQLite + EF Core Migrations).** Single-user, tiny schema. **Forbid `EnsureCreated`/`EnsureDeleted`; run `Migrate()` on boot.** Store enums as strings (`.HasConversion<string>()`). Org-aligned alternative: Azure SQL (`UseSqlServer(..., o => o.EnableRetryOnFailure())`) only if multi-instance. Keep provider swappable.
- **D3 — Anthropic client:** community `Anthropic.SDK` NuGet *or* raw `HttpClient` (original approach). Azure/OpenAI via `Azure.AI.OpenAI` / `OpenAI` .NET SDK. ClaudeCli via `System.Diagnostics.Process`.
- **D4 — Live updates: keep SSE** (App Service streams `text/event-stream`). SignalR only under the D1 alternative.
- **D5 — Repo/ADO layout: new standalone repo + ADO project** (own service connection, build defs, Key Vault, Bicep `infra/`). Do not add into the CRM monorepo. **CONFIRMED 2026-06-18 (override): build the .NET solution in a new `dotnet/` folder in THIS repo during migration (Python stays in `backend/` as the golden-diff reference); after the migration is complete + verified, `dotnet/` MOVES to `backend/`, retiring the Python. The separate standalone repo / ADO project is deferred to the future Azure deploy, not done now.**
- **D6 — Existing-DB adoption (recommended: adopt).** Live `qa.db` is Alembic-managed; its `alembic_version` won't match EF's `__EFMigrationsHistory`. Either (a) point EF at the existing file + record a single baseline migration as already-applied (insert `__EFMigrationsHistory`, no DDL), or (b) fresh DB + accept data loss. **Confirm with the user before B0; if adopt is confirmed, implement (a) in B1** (do not re-defer the decision).
- **Logging:** local `ILogger` + App Insights (recommended for a standalone tool); wire the org's centralized Logging API only if parity is required.
- **Secrets:** Key Vault / env vars / User Secrets — never the CRM repo's committed-plaintext appsettings pattern.

---

## 3. Current → target stack

**Backend:** Python 3.12 / FastAPI / Uvicorn :5250 → **C#/.NET 8 / ASP.NET Core Web API (App Service)**. SQLAlchemy-async + aiosqlite + Alembic → **EF Core 8 + SQLite + EF Migrations**. httpx → typed `HttpClient` (`IHttpClientFactory`). anthropic/openai SDKs → `Anthropic.SDK`/`Azure.AI.OpenAI`/`OpenAI`. APScheduler → `BackgroundService` + Cronos. `asyncio.create_task`+`session_scope` → `Task.Run`+`IServiceScopeFactory.CreateScope`. `asyncio.Semaphore(4)` → `SemaphoreSlim(4)`. `dict[int,Task]` → `ConcurrentDictionary<int,CancellationTokenSource>`. pydantic-settings → Options + `IOptionsMonitor` + Settings table. SSE preserved.

**Frontend:** keep Vite 8 / MUI 9 / TS 6 / React 19 / Framer Motion (target-crm uses Vite 6 / MUI 7 / TS 5.9 — **do NOT downgrade**). Add **Redux Toolkit** (global UI state) + **React Query** (server state, replacing hand-rolled polling). Replace inline `axios('/api/...')` with **centralized `src/api/axios-client.ts`** + per-feature `api/*` modules + **`src/config`** over `VITE_QA_API`. Restructure `pages/`+`session/` into feature modules.

---

## 4. Target .NET solution structure

```
qa-automation/
  QaAutomation.sln
  src/
    QaAutomation.Api/            # ASP.NET Core 8 host
      Program.cs                 # WebApplication + AddCustom*/Configure* one-liners (mirror YourProduct.WebAPI)
      Configurations/ConfigurationExtensions.cs + *Options.cs (required/[Required] + ValidateDataAnnotations)
      Middlewares/ExceptionHandlingMiddleware.cs   # registered FIRST; ProblemDetails (+ optional Logging API)
      Controllers/ Diagnostics, Settings, Knowledgebase, AuthorTests, FindBugs, Reports, Batches
      Streaming/SseWriter.cs
      appsettings.json + appsettings.{Development,QA,Staging,Production}.json
    QaAutomation.Core/           # Abstractions/ (ILlmClient, IAzureDevOpsClient, IEventBus, I*Orchestrator, I*Service, ILlmProviderState)
                                 # Configuration/ (*Options), Models/ (EF entities + enums + FlexibleStringListConverter), Contracts/ (DTOs)
    QaAutomation.Infrastructure/ # Data/ (QaDbContext + Migrations + FtsBootstrapper), AzureDevOps/, Llm/, Agents/, Orchestrators/,
                                 # Knowledgebase/, Reports/, Events/ (EventBus + ConsoleEvent), DependencyInjection.cs
    QaAutomation.Tests/          # xUnit + Moq + FluentAssertions (org test stack) — NONE exist today, build from scratch
  client/                        # restructured React app (§5.10 / §6 F-phases)
  infra/                         # Bicep
  build/                         # build-branch/develop/qa/main.yml
```

**Repo layout during migration (CONFIRMED 2026-06-18):** the structure above is the .NET *solution* root; in THIS repo it is scaffolded under a new top-level **`dotnet/`** folder while the live Python stays in **`backend/`** (run both side-by-side for golden-diffing, §8). The React `client/` is realigned **in place** at the repo root (not nested under the solution root as drawn above). `infra/` and `build/` are **deferred** (created later for the company's Azure deploy). **After the migration is verified, `dotnet/` replaces `backend/`.**

**Org conventions to adopt:** `Program.cs` = list of `AddCustom*`/`Configure*`; strongly-typed `required`/`[Required]` options + `ValidateDataAnnotations()`; `nameof`-based config-key constants; `ExceptionHandlingMiddleware` first; Swagger w/ Bearer; EF `EnableRetryOnFailure` (if SQL Server). Use `IOptionsMonitor`/mutable snapshot (not immutable `IOptions`) so UI settings rebuild the ADO/LLM clients at runtime; re-read the PAT per request (IHttpClientFactory pools handlers). **Do NOT adopt URL-segment API versioning** — routes stay flat `/api/<router>` (see §5.4). **Skip** committed-plaintext secrets and the obsolete in-process `FunctionBase`.

---

## 5. FROZEN CONTRACTS (the core — reproduce verbatim)

### 5.1 JSON / serialization rules 🔴
- camelCase property policy for normal DTOs; `{ total, items }` paging; errors `{ detail }` (HTTPException) / ProblemDetails (unhandled).
- **Enums** serialize via `JsonStringEnumConverter` as the **exact NAME** (NOT int, NOT camelCased); non-identifier values via `[JsonStringEnumMemberName]`/`[EnumMember]`.
- **Carve-outs (bypass camelCase):** Settings `GET/PUT /config`, ADO-discovery (`/ado/orgs|projects|area-paths`), and `/settings/claude-cli/models` return **literal dotted-key dicts** (`ado.organization`). Model as `Dictionary<string,…>`.
- **ADO json-patch bodies** are serialized **raw** (own serializer) — never through the global camelCase options (would corrupt `/fields/System.Title` paths and `op/path/value` keys). `Content-Type: application/json-patch+json`.

### 5.2 Enums — full freeze (store as strings via `.HasConversion<string>()`) 🔴
- `SessionStatus`: Analyzing, AwaitingTester, Drafting, Drafted, Pushing, Done, Failed, Cancelled (default Analyzing).
- `BugSessionStatus`: Analyzing, AwaitingActual, Verifying, Drafting, Drafted, Pushing, Done, Failed, Cancelled.
- `DraftStatus` **and the distinct** `BugDraftStatus`: Draft, Approved, Rejected, Pushed (default Draft).
- `BugSeverity`: Critical, High, Medium, Low (default Medium).
- `ReportStatus`: Generating, Done, Failed. `ReportTheme`: Executive, Standup, Branded (default Executive).
- `LlmProvider`: Anthropic, AzureOpenAI, OpenAICompatible, ClaudeCli (default Anthropic).
- `BatchModule`: `author-tests`, `find-bugs` (kebab). `BatchMode`: `auto`, `manual` (lowercase).
- Verdict status vocabulary: `pass|fail|inconclusive` (unknown→inconclusive).

### 5.3 Data model (SQLite; PascalCase table/column names FROZEN = the live ORM models = full Alembic chain through `b1c2d3e4f5a6`) 🔴
> The frozen schema is the FULL chain (initial `1e39a44b971c` + `add_event_log` + `add_code_verification` + `report_theme_and_summary` + `add_batches`), NOT the initial migration alone — the initial migration lacks `EventLogJson`, `BatchId`, `CodeVerificationJson`, `Theme`, `SummaryJson`, and the entire `Batches` table.
Entities & key columns (UTC `DateTime` throughout):
- **Projects**(Id PK, Name, AdoOrganization, AdoProject) — seed `Id=1, Name='Your Project'`.
- **Settings**(Key PK string, Value string) — holds `llm.provider` + all UI-editable settings.
- **Documents**(Id, ProjectId FK→Projects **CASCADE**+index, FileName, StoredPath, ChunkCount, SizeBytes, UploadedAt).
- **TestSessions**(Id, ProjectId, WorkItemId, WorkItemTitle, Status, CurrentActivity?, AnalysisJson?, ErrorMessage?, EventLogJson?, BatchId? FK→Batches **SET NULL**+index, CreatedAt, UpdatedAt).
- **TestCaseDrafts**(Id, SessionId FK→TestSessions **CASCADE**+index, Title, Preconditions, StepsJson default '[]', Status, AdoTestCaseId?, SortOrder).
- **BugSessions**(Id, ProjectId, WorkItemId, WorkItemTitle, Status, CurrentActivity?, AnalysisJson?, ActualBehavior?, CodeContext?, ErrorMessage?, EventLogJson?, CodeVerificationJson?, BatchId?, CreatedAt, UpdatedAt).
- **BugDrafts**(Id, SessionId FK→BugSessions **CASCADE**+index, Title, Severity, Preconditions, ReproStepsJson '[]', ExpectedBehavior, ActualBehavior, SuggestedFix, Status, AdoBugId?, SortOrder).
- **Reports**(Id, ProjectId, FromUtc, ToUtc, Boards (joined '; '), TicketCount, Status, HtmlPath?, ErrorMessage?, CreatedAt, Title, Trigger default 'Manual', Theme default 'Executive', SummaryJson?, EventLogJson?).
- **Batches**(Id, ProjectId, Module, ParentWorkItemId?, Title, Mode?, CreatedAt, UpdatedAt) — **status DERIVED from children at read time, never stored**.

**FK divergence:** only `Documents.ProjectId`, `TestCaseDrafts.SessionId`, `BugDrafts.SessionId` are real CASCADE FKs in the DB — configure `OnDelete(Cascade)`. For `BatchId` the live SQLAlchemy model *declares* an `ondelete=SET NULL` FK, but migration `b1c2d3e4f5a6` creates the column **indexed with NO FK constraint** — the authoritative DB shape is plain-indexed-int, no FK. In .NET, model `BatchId` (and the other `ProjectId` columns) as plain indexed ints with **app-level SET-NULL**; do NOT configure an EF FK (EF would auto-create one and change delete behavior).

**FTS5 (raw SQL, not EF-managed), verbatim:** `CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)` — column is `doc_id` (not `document_id`), only `content` indexed. Created at startup after migrations.

### 5.4 Endpoint inventory (all flat under `/api`, NO version segment) 🔴
**This section is the authoritative, complete route list** (re-verify counts against `backend/app/routers/*.py`). The **four `202`s** are: author-tests `run-tester`, author-tests `push`, find-bugs `run`, find-bugs `push`. One **`204`** (KB delete). The async **starts return synchronous `200`** with `{sessionId}`/`{reportId}` (NOTE: `POST /reports/{projectId}/generate` returns **200 `{reportId}`**, not 202). All session-list endpoints clamp `take` 1..100 and `skip ≥ 0`.

- **Diagnostics:** `GET /api/diagnostics` → `{activeProvider, adoPatPresent, anthropicKeyPresent, azureOpenAiConfigured(=endpoint&&key&&deployment), claudeCliAvailable, claudeCliVersion}`; `GET /api/diagnostics/workitem/{id}` (404 if ADO 404 else 502); `GET /api/diagnostics/llm` → `{provider, reply}` (ping system `"Reply with exactly: OK"`, user `"Say OK."`, any error→502).
- **Settings:** `GET/PUT /api/settings/provider` → `{current, available}` (PUT 400 `Unknown provider`); `GET/PUT /api/settings/config` (dotted-key dict, masking — §5.11); `GET /api/settings/ado/orgs` → **200** `{organizations:[],supported:false}` on most PATs (**must not raise**); `GET /api/settings/ado/projects?organization=`; `GET /api/settings/ado/area-paths?organization=&project=`; **`GET /api/settings/claude-cli/models`** → `{suggestions[],notes}`.
- **Knowledgebase:** `GET /api/knowledgebase/{projectId}`; `POST /api/knowledgebase/{projectId}` (multipart field **`files`**, `.md` only→400, `MAX_FILE_BYTES=10_000_000` decimal, decode `errors='replace'`); `DELETE /api/knowledgebase/doc/{documentId}` → **204** (idempotent); `GET /api/knowledgebase/{projectId}/search?q&k=8` (q≥2→400, k clamp 1..50).
- **Author Tests:** `GET /api/author-tests/search?q` (blank→`[]` no ADO call, `top=20`, failure→502 — **reused by Find-Bugs + Epic-Runs**); `GET /plans`; `GET /plans/{planId}/suites`; `POST /api/author-tests/{projectId}/start` `{workItemId, autoHandoff=true}` → `{sessionId}` (validate WI: 400/404/502); `POST /sessions/{id}/run-tester` → 202 `{started:true}`; `POST /sessions/{id}/cancel` → `{cancelled}`; `GET /sessions/{projectId}?skip=0&take=20` (clamp 1..100) → `{total, items}`; `GET /sessions/detail/{id}`; `PATCH /sessions/{id}/analysis` (persist **by_alias camelCase**); `PATCH /drafts/{id}` (**skip null fields**); `POST /sessions/{id}/approve-all` (only `Draft`→Approved); `POST /sessions/{id}/push` `{testPlanId, testSuiteId}` → 202; `GET /sessions/{id}/stream` (SSE).
- **Find Bugs:** `POST /api/find-bugs/{projectId}/start` `{workItemId}` only → `{sessionId}` (parks at AwaitingActual); `POST /sessions/{id}/run` `{actualBehavior, codeContext?, verifyCode, commitIds[], prIds[], selectedTestCases[]}` → 202 (**code verification ALWAYS on, ignore verifyCode**); `POST /sessions/{id}/cancel`; `POST /sessions/{id}/push` **(body-less)** → 202; `POST /sessions/{id}/approve-all`; `GET /sessions/{projectId}?skip=0&take=30` (clamp 1..100, skip≥0); `GET /sessions/detail/{id}` (incl. codeVerificationJson); `PATCH /sessions/{id}/analysis`; `PATCH /drafts/{id}`; `GET /sessions/{id}/linked-refs` → **200** empty `BugLinkedRefs` only on **AdoApiError** (other exceptions propagate → 500); `GET /sessions/{id}/test-cases` (None→404; keys `plan#<id>` (ADO) / `draft|<title>` — this endpoint's key is DISTINCT from the code-verify internal `draft#<id>` in §5.8; both coexist, do NOT unify; source `plan`/`draft`; steps `[{action,expected}]`); `POST /sessions/{id}/bug-from-verdict` (`mode` instant|agent); `GET /sessions/{id}/stream`.
- **Reports** (register `/preview` BEFORE the `/{projectId}` route): `GET /api/reports/preview?theme=&team=`; `POST /api/reports/{projectId}/generate` `{fromUtc, toUtc, boards?[], theme}` → **200** `{reportId}` (from<to else 400 `FromUtc must be before ToUtc.`; range = fractional days, `> MAX_RANGE_DAYS=365` → 400 `Date range {days:.0f} days exceeds max 365 days.`); `GET /api/reports/{projectId}`; `GET /detail/{id}` (404); `GET /html/{id}` (inline `text/html; charset=utf-8`); `GET /download/{id}` (attachment filename `report-{id}-{from:%Y%m%d}-to-{to:%Y%m%d}.html`); `GET /{id}/stream` (SSE).
- **Batches:** `GET /api/batches/children-expand?parentWorkItemId=&recursive=true` (camelCase query alias); `POST /api/batches/{module}/{projectId}/start` `{workItemIds[], parentWorkItemId?, mode?}` → `{batchId, sessionIds[], skipped[]}` (failed children→`skipped`, do **not** abort; `mode` lowercased→`auto|manual|null`, stored only for find-bugs, always null for author-tests); `GET /api/batches/{projectId}?skip=0&take=30` (clamp 1..100, skip≥0) + `GET /detail/{batchId}` (derived status ∈ {Empty, Running, AwaitingInput, CompletedWithFailures, Cancelled, Completed}; done-set {Done,Drafted}, awaiting-set {AwaitingActual,AwaitingTester}, `running = total−done−failed−cancelled−awaiting`); `POST /{batchId}/cancel` (force parked Manual children → `Cancelled`, activity `Cancelled (epic run)`).
- **`GET /api/health`** → `{status:"ok"}`. CORS allow `http://localhost:5173`, `:5174`.
- **Error mapping:** `AdoApiError.Status==404`→404 (`Work item #<id> not found.`); other AdoApiError→**502**; LLM failure→**502**; unhandled→500; business validation→400.
- **Route precedence:** ensure literal segments win (`/preview`, `/sessions/detail/{id}`, `/detail|/html|/download`) so a `{projectId}` catch-all never shadows them.

### 5.5 ADO REST client 🔴
**Base/auth:** base = `https://dev.azure.com/{org.Trim('/')}/{Uri.EscapeDataString(project)}` (names contain spaces → MUST URL-encode). PAT Basic = `base64(":"+pat)` (empty username); default `Accept: application/json`; **HttpClient timeout 30s** (override the 100s default). Work-item **60s monotonic cache** (Stopwatch) + `force_refresh` bypass; class-level repo-id cache (lowercased key). Singleton client **rebuilt on settings change**.

**Host / api-version matrix.** Most ops use the configurable `ado_api_version` (default `7.1`). The rows below that show a *different* version (vssps preview, comments `7.0-preview.3`, updates `7.0`) are **HARDCODED literals that must NOT use the configurable option**. The `7.1` rows DO use the configurable option. `discover_area_paths` builds its URL from scratch (`dev.azure.com/{org}/{quote(project)}`), not from `ado_base_url`.
| Op | Host | api-version |
|---|---|---|
| most reads/writes | project-scoped base | `7.1` (configurable) |
| `discover_organizations` | `app.vssps.visualstudio.com` (profile/me → accounts?memberId) | `7.1-preview.3` then `7.1-preview.1` (hardcoded) |
| `discover_projects` | `dev.azure.com/{org}/_apis/projects` (`$top=200`) | `7.1` |
| `discover_area_paths` | org-level `dev.azure.com/{org}/{quote(project)}` classificationnodes/Areas (`$depth=10`, backslash-join path) | `7.1` |
| `get_work_item_comments` | project-scoped `/_apis/wit/workItems/{id}/comments` (camelCase) | `7.0-preview.3` (hardcoded) |
| `get_work_item_updates` | project-scoped `/_apis/wit/workItems/{id}/updates` (camelCase) | `7.0` (hardcoded) |
| `search_code` (POST body) | `almsearch.dev.azure.com` | `7.1` |

**URL casing (do not normalize):** lowercase `/plans/{id}/suites` vs capitalized `/Plans/{id}/Suites/{id}/TestCase`; lowercase `/workitems` (PATCH + `get_work_item`) vs camelCase `/workItems` (relation URLs AND the comments/updates read endpoints).

**Frozen write bodies (golden-test):**
- `create_test_case`: POST `$Test%20Case` json-patch: add `/fields/System.Title`, `/fields/Microsoft.VSTS.TCM.Steps` (Steps XML), optional `/fields/System.Description` = `<div><b>Preconditions:</b> {esc(preconditions)}</div>`.
- **Steps XML (byte-identical):** `<steps id="0" last="{n+1}">` + per step (id from 2): `<step id="{i+2}" type="ActionStep"><parameterizedString isformatted="true">&lt;DIV&gt;{esc(action)}&lt;/DIV&gt;</parameterizedString><parameterizedString isformatted="true">&lt;DIV&gt;{esc(expected)}&lt;/DIV&gt;</parameterizedString><description/></step>` + `</steps>`. **Escaping is load-bearing:** Python uses `html.escape(x, quote=True)` → escapes `&` `<` `>` plus `"`→`&quot;` and `'`→`&#x27;`. .NET `HttpUtility.HtmlEncode` does NOT escape `'` — the port MUST escape `'`→`&#x27;` (and `"`→`&quot;`) to stay byte-identical. Implement the inverse `_parse_steps_xml` too (reads linked test cases back to `{action,expected}`; `get_linked_test_cases` sets `preconditions=''` unconditionally — needed for code-verify).
- `add_test_case_to_suite`: POST `/Plans/{planId}/Suites/{suiteId}/TestCase` body `[{ "workItem": { "id": "<tcId-as-STRING>" }, "pointAssignments": [] }]`.
- `link_tested_by`: PATCH story add `/relations/-` `{ rel:"Microsoft.VSTS.Common.TestedBy-Reverse", url }`. `link_related`: `{ rel:"System.LinkTypes.Related", url }`.
- `create_bug`: POST `$Bug`. Severity map Critical→`1 - Critical`, High→`2 - High`, Medium→`3 - Medium`, Low→`4 - Low`, **default `3 - Medium`**. Standing fields: `System.AreaPath`=`Your Project\Your Team` (literal backslash), `Microsoft.VSTS.Common.Priority`=**integer 2**, `ValueArea`=`Business`, `Custom.QA`=`Mahmood Ahmad`, `Custom.FoundWith`=`Manual Test`. ReproSteps HTML in order: optional `<div><b>Preconditions:</b> …</div>` → `<ul><li>step</li></ul>` → `<div><b>OBSERVATION:</b></div><div><ul><li>{actual}</li></ul></div>` → `<div><i>[Attach screenshot/GIF here]</i></div>`. Optional AcceptanceCriteria `<div>I know this bug is resolved when:</div><div><ul><li>{expected}</li></ul></div>`; optional `SystemInfo` `<div><b>Suggested fix:</b> {fix}</div>`. **Frozen json-patch op ORDER** (golden-test): System.Title → ReproSteps → Severity → System.AreaPath → Priority(int 2) → ValueArea → Custom.QA → Custom.FoundWith → [AcceptanceCriteria if expected] → [SystemInfo if fix].

**Reads:** `get_work_item` fields csv (exact order) `System.Id,System.Title,System.Description,System.WorkItemType,System.AssignedTo,System.State,Microsoft.VSTS.Common.AcceptanceCriteria`, map `AssignedTo`→`displayName`. `search_work_items` numeric→direct get (catch AdoApiError) else WIQL `Title CONTAINS` (`''`-escaped), `$top=20`, ORDER BY ChangedDate DESC. `get_child_work_items` (B8): **EXCLUSION list not allow-list** — collect anything not container `{Epic,Feature}` and not `{Test Case,Test Plan,Test Suite,Shared Steps,Shared Parameter}`; `max_depth=5, max_nodes=500, max_leaves=200`. `get_tickets_in_range`: WIQL **date-only** on `[System.ChangedDate]`, `System.ClosedDate` **omitted** (TF51535), type IN list, per-board `[System.AreaPath] UNDER` OR-group, 200-id batch, 14-field REPORT_FIELDS, single-quote doubling. `enrich_tickets_for_report`: `limit=150`, **`Semaphore(8)`** (distinct from pipeline `Semaphore(4)`), best-effort. Add the missing reads: `get_linked_test_cases`, `_direct_children`, `get_work_item_comments/updates`, `get_work_item_git_refs`, `get_linked_changes`, PR/commit changed-files + diff, `get_commits_for_path`, `get_file_content`, `search_code`, `_change_paths/_change_entries` (note `changeEntries` vs `changes` key; vstfs parse).

**Errors/secrets:** `AdoApiError(op,status,sanitizedBody)` — distinct **catchable** type carrying `.Status`; `str` = `ADO [{op}] {status}: {body[:512]}`; `_raise_if_failed` **sanitizes before parse**, unwraps `{message}`, success short-circuit on `is_success`; callers branch on `.Status`. `sanitize(*secrets)`: `MAX_LENGTH=2048`, **`len(secret)>6` guard**, `***REDACTED***`, suffix `…[truncated]` (U+2026), empty→`''`. Frozen lowercase domains: `LinkedChange.kind` `pr|commit`; `ChangedFile.change_type` `add|edit|delete|rename|"edit, rename"|seed`; `TimelineEvent.kind` `created|state|assignment|comment|other` (detail strings use `→` U+2192, `Unassigned`).

### 5.6 LLM provider layer 🔴
- `ILlmClient.Complete(system, user, model=null, progress=null, emit=null) → Task<string>` (single concatenated string, never null). Non-streaming providers **ignore** progress/emit (don't throw). `ProviderName` returns exact strings.
- **Factory is per-call, no-cache, reads settings at construction** (NOT a singleton `ILlmClient`) — so runtime switch + settings edits apply next call. Use `IOptionsMonitor`/mutable snapshot.
- **Shared `anthropic_max_tokens=4096`** reused by Azure + OpenAICompatible — do NOT diverge per provider. Anthropic model `claude-sonnet-4-6` (verify vs live). Azure api-version `2024-08-01-preview`. OpenAICompatible default base `https://models.inference.ai.azure.com`, model `gpt-4o-mini`; **blank key → fall back to literal `"missing-key"` (construction NEVER fails; auth error surfaces at call time as the sanitized message)** — do NOT add a constructor guard.
- **Per-provider error sanitization**: each key-provider catches SDK status error → sanitize echoed body with that provider's key → rethrow with the provider's EXACT prefix (NOT a uniform `ProviderName` substitution): Anthropic = `Anthropic API returned {status}: {body}`; Azure = `Azure OpenAI returned {status}: {body}`; OpenAICompatible = `OpenAI-compatible endpoint returned {status}: {body}`. ClaudeCli does not sanitize.
- **StreamProgress** (thread-safe `{chars, ticks, elapsed}`) + emit payload `{kind:'token'|'thinking', text}` (exception-guarded). Orchestrator polls **1.5s** → `stat` ConsoleEvent + CurrentActivity `'{label} — {chars:,} chars · {elapsed}s'` / `'{label} — working · {elapsed}s'`.
- **ClaudeCli:** args `-p --output-format stream-json --include-partial-messages --verbose` (+`--model` only if non-empty); `<system_instructions>` wrapper; **prompt via STDIN** (WinError 206 / ~32KB argv limit — distinct reason from the asyncio note; .NET async process I/O works fine, so the Python Selector-loop workaround is unneeded); **idle timeout = `max(30, claude_cli.timeout_seconds)` (configurable; default 180, settings-layer floor `max(10,int)`) AND absolute cap `1800s`** (distinct messages — do NOT collapse to one total timeout); the `result` event is the only authoritative answer; distinct exit-code (stderr `[:500]`) / missing-result (stderr `[:300]`) / FileNotFound(PATH-hint) errors; emit-coalescing buffer flushes on channel-switch / ≥256 chars / ≥80ms. One shared `resolve_claude_cli_path` feeds provider + `probe_claude_cli` (`--version`, 10s); search order: config-path → (abs: exists / rel: `shutil.which`) → `~/.local/bin` → AppData npm → AppData Local Programs → `/usr/local/bin` → `/opt/homebrew`. `provider_state` IS a process singleton (persists `llm.provider` to Settings, malformed→default); only the client instances are per-call.

### 5.7 Agents (prompts authoritative from Python) 🔴
- **Prompt freeze manifest (capture golden FROM live Python; line ranges are guides — re-confirm against the files):** `analyst.py:13-77`, `tester.py:12-120`, `bug_writer.py:13-98`, `code_verifier.py:19-45` (case prompt) + `51-78` (STEP_SYSTEM_PROMPT) + `80-87` (`_ACTUAL_RULE_FULL`/`_LITE` substitution constants); orchestrator-embedded **triage** prompt; Reports **summarizer** prompt. House style encoded in prompts: ALL-CAPS keyword rules, DMS names ASPEN/INFINITY/EVEREST/IDEAL/TARGET, envs QA/Staging/Production, title `[Module] - [Feature] - Verify that … .`, "no preconditions — step 1 always Login to YourProduct", childlike one-action-per-step, evidence tags `[src:]`/`[ticket]`/`[ASSUMED]`/`[UNCLEAR]`.
- **User-prompt builders** (every label): `### From: {file_name}` header (couples to Analyst `[src:]`), Bug Writer `Cross-flow impacts` vs Tester `Cross-flow`, answered-vs-unanswered open-question handling (Tester surfaces both; Bug Writer drops unanswered). Bug Writer `_strip_html`. Frozen render strings `I know this bug is resolved when: <expected>` and bold `OBSERVATION:`.
- **Output DTO key freeze** (dual-case tolerant; `preconditions` always `""`): AnalystOutput 12 keys (`question_answers` index-aligned to `open_questions`, authoritative when non-empty); Tester `{title, preconditions, steps:[{action,expected}]}`; BugWriter `{title, severity, preconditions, reproSteps[], expected, actual, suggestedFix}`; `CodeVerdict {testCase, status, evidence, reasoning}`; `StepVerdict {action, expected, status, actual}`.
- **Do-not-conflate Tester vs Bug Writer:** title format, step-1 rule, step shape (`{action,expected}` vs plain strings). Preserve BugWriter's mixed-trailing-period example titles verbatim.
- **Code Verifier:** pass-1 truncates `tc.steps[:12]`, single `complete`+strip_fences+parse, **raises** on bad JSON, **NO retry**; pass-2 `{ACTUAL_RULE}` FULL/LITE substitution, `{case, steps:[{status,actual}]}`, by-case-number-then-positional **alignment fallback**, swallow JSONDecodeError→all-inconclusive, **never raises**.
- **json_utils:** `strip_fences` only on ```` ``` ```` prefix; `extract_json` **string-aware** (backslash escapes; returns `s[start:]` on truncation, not `''`); `coerce_string` bool→`'true'/'false'` (check bool before numeric), dict→`'key: value; …'`, list→`json.dumps(ensure_ascii=False)`; `coerce_string_list` blank→`[]`, drops None, wraps scalars; retry attempts=3 + verbatim nudge + per-agent opener (`{` vs `[`); logger `qa.agents`. Agents pass `model=None`; **no temperature param**.

### 5.8 Orchestrators / concurrency / EventBus / SSE 🔴
- **DI lifetime:** `IEventBus`, `AgentOrchestrator`, `BugOrchestrator` are **SINGLETON**; app stays **single-instance**. Scoped registration fragments the cancellation registry + EventBus → Cancel/SSE silently no-op.
- **Cancellation:** thread the registry CTS token into `SemaphoreSlim.WaitAsync(ct)` **and every awaited LLM/ADO/EF call** (cooperative cancellation, unlike Python's in-band `task.cancel()`). **Two handlers:** (a) the *in-body* handler (inside the DB scope) sets `Cancelled` + `"Cancelled by user"` (push variant `"Push cancelled — N of M pushed"`), persists the log, `_safe_commit`, re-throws; (b) the *outer* queued-guard handler (wrapping the semaphore, for a child cancelled while still queued) calls guarded `mark_cancelled` (skips if already terminal), re-throws. General Exception (in-body)→mark Failed→**swallow**; `_safe_commit` swallows; `cancel()` returns bool. `_TERMINAL = {Done, Drafted, Failed, Cancelled}`.
- **Background-task contract:** seed row SYNCHRONOUSLY in a short-lived scope (insert+commit+capture id+register CTS) **before** 202; pipeline opens its OWN scope via `IServiceScopeFactory`; semaphore acquired INSIDE the task AFTER `mark_queued`, AROUND the body; done-callback removes registry entry by captured key. Signatures: `start(projectId, workItemId, autoHandoff=true|auto=false, batchId=null)`, `run_tester(sessionId)`, `run_bug_writer(sessionId, actualBehavior, codeContext, verifyCode=false, commitIds=null, prIds=null, selectedTestCases=null)` (all three list params default null, each coerced to `[]` inside; the router passes `actualBehavior = req.actualBehavior or ""`), `push(sessionId[, testPlanId, testSuiteId])`.
- **EventBus:** `_RING_MAX=400`, `_QUEUE_MAX=1000` (drop-oldest), `_GRACE_TTL=600s`; **one bounded channel PER SUBSCRIBER** (DropOldest), not a shared channel; bus assigns `seq` under a lock; replay strictly **`seq > after_seq`**; key namespaces `session:{id}`/`bug:{id}`/`report:{id}`; GC only when no-subscribers AND terminal AND age>600s. `NARRATIVE_KINDS` persisted to `EventLogJson` (cap 300); token/thinking live-only.
- **SSE:** headers `Cache-Control: no-cache, no-transform`, `Connection: keep-alive`, **`X-Accel-Buffering: no`** (App Service/Front Door buffer by default → console never updates); `: connected` preamble; `: keepalive` every 15s; frame `id: <seq>\ndata: <json>\n\n` (space after each colon — byte-exact; comment frames `: connected\n\n` / `: keepalive\n\n`); `as_camel` keys `{seq, ts, sessionKey, kind, phase, level, text, data}`; terminal closes stream; `Last-Event-ID` via header `last-event-id` AND query `lastEventId` (digits only, header wins).
- **CurrentActivity / phase strings (load-bearing — UI keys off them; enumerate + golden-test incl. QUEUED + CANCELLED paths):** `QUEUED_ACTIVITY="Queued — waiting for a slot"`, `Cancelled by user`, `Failed: {e}`, `Push starting…`, `Pushing {i}/{n}: …`, all Analyst/Tester/BugWriter phase activities, em-dash/·/… result templates; **batch cancel uses `Cancelled (epic run)`** (distinct from `Cancelled by user`). **`EventKind` = {phase, token, thinking, system, stat, result, error, push}** — orchestrators ALSO emit `kind="push"` (agent_orchestrator.py:338, bug_orchestrator.py:822), so the .NET enum/converter MUST accept **8** values. **`EventPhase` = {fetch, retrieve, analyst, tester, bugwriter, push, summarize, discover, code-verify, enrich, triage}** — `triage` is emitted during the code-verify file-triage pass (bug_orchestrator.py:607,610). **`EventLevel` = {info, success, warn, error}.**
- **Code-verify subsystem (flagship; highest risk):** phase order discover→code-verify→bugwriter. `_discover_anchored_context`: resolve_repo first, auto-then-manual `dict.fromkeys` merge, **degrade-never-raise**, `sha[:8]`. Fallback search: `_STOPWORDS`, `[A-Za-z]{2,}`, first-3-words short-query rule, `top=8`, first-2-seeds recent-commit, `change_type='seed'`. Import/caller expansion: `_MAX_FILES=20`, `_MAX_BYTES_PER_FILE=30_000`, `_MAX_TOTAL_BYTES=250_000`, `_IMPORT_RE`, relative-only, exts incl `.cs`, index-guess `exts[:4]` excluding `.cs`, first-5-files caller scan `top=6` break-on-error, forward-slash normalization. Triage+assembly: candidate caps `[:30]`, block markers `=== [CHANGED] …===` / `=== [{ROLE}] …===`, deterministic `[:8]` fallback. `_load_test_cases`: ADO-linked-wins precedence, latest-session drafts, stable keys `plan#{id}`/`draft#{id}` (NOTE: this *internal* key uses `draft#<id>`; the `/test-cases` ENDPOINT response key in §5.4 is the DIFFERENT `draft|<title>` — both coexist, do NOT unify). Pass-1 status `pass|fail|inconclusive`. Pass-2 config keys `find_bugs.verify_detail` default `step`, `find_bugs.verify_step_scope` default `fails_only`, BATCH=6, **fail>inconclusive>pass** reconciliation, `failedStepIndex`. **`code_context` cap `120_000` is applied in `_run_bug_writer`** (repo_code appended then truncated) — **NOT** in `create_bug_from_verdict` (which passes `session.code_context` UNCAPPED). `_run_bug_writer` filters cases by the `selectedTestCases` stable keys (empty/stale-no-match → verify ALL). `create_bug_from_verdict`: in-flight guard `{Analyzing,Verifying,Drafting,Pushing}`, re-open `{Drafted,Done,AwaitingActual,Failed,Cancelled}`, `bug_from_case_mode` default `instant`, agent→instant fallback; failing-step pick = explicit in-range `failedStepIndex` → else first `fail` → else first `inconclusive` → else None (repro covers all steps).
- **Pipeline asymmetry (decide preserve vs unify, then document):** agent pipeline resolves LLM INSIDE try (config error→Failed); bug analyst resolves OUTSIDE (propagates); bug analyst skips the 1.5s pump, key prefix `bug:`, AUTO mode runs analyst→discover→verify→bugwriter in the SAME task/slot.
- **Concurrency:** `Semaphore(4)`; batch fan-out **concurrent** (`Task.WhenAll` return-exceptions — serial loop → duplicate batches on client retry). `_parse_severity` branch order crit/1→Critical, high/2→High, low/4→Low, else Medium.

### 5.9 Reports + Knowledgebase 🔴
- **ReportHtmlBuilder** (~1500 lines): `build_report_html` signature; body `data-mode='explore'` default + `data-theme=theme.value.lower()`; slide list + empty-collapse rules (cover/overview/by_person>1/themes/risks(**summary.risks** OR no_fix OR open_critical)/wins/spot_*/closing) with counter `{n} / {total}`; `overview` slide falls back to tldr/by_type when summary absent; spotlight thresholds (≥3 tickets, **max 6 PER-PERSON**, remaining roll into a 7th `Others (N)` slide with its own `data-spot` index); explore filter ids `f-type/f-person/f-state/f-q` + ticket `data-type/data-person/data-state`; heading/marker literals (`By Ticket Type`, `AI-Detected Themes`, `⚠ Fix missing`); date formats `%b %d, %Y` (cover) / `%Y-%m-%d %H:%M` (modal); present-mode keyboard nav/fullscreen/speaker-notes JS.
- **`window.__REPORT__`** modal: `{tickets:{id->_ticket_json}, spots:{index|theme_i->[ids]}}`; **`</`→`<\/` escape (security)**; `_ticket_json` camel keys (`assignedTo, descriptionHtml, comments, timeline`, plus `rootCauseHtml` which is ALWAYS present but non-empty only when isBug); `isBug` set `{bug,defect,issue}`; `KIND_LABEL` map; data-spot keys must match `spots`.
- **Board-name derivation:** SECOND area-path segment (`parts[1]` after normalizing `/`→`\`), fallback `parts[0]` then `Other`. Closed-state set `{closed,done,resolved,completed}`.
- **Summarizer never-block:** empty→headline `No tickets in range.`; `max_tickets_in_prompt=80`; ALL failures (no client / LLM error / bad JSON / non-dict) → `_fallback`. summary_json `{tldr, headline, themes:[{name,description,ticketIds}], speakerScript, risks, wins}`; speakerScript keys `{cover,tldr,by_type,by_person,themes,risks,wins,closing}`; `_fallback` limited keys (cover/tldr/by_type/by_person/closing).
- **reports.* settings + type divergence:** `K_REPORT_TYPES` default (no Issue/Feature) vs ado_client `_DEFAULT_REPORT_TYPES` (adds Issue/Feature); open_critical counts Issue but Issue not queried.
- **Scheduler:** `BackgroundService`, Thursday 11:00 **Asia/Karachi** (`TimeZoneInfo.FindSystemTimeZoneById`, Cronos), no missed-slot catch-up. The scheduler **trigger** reads `schedule_enabled` (truthy set {true,1,yes,on}) / `default_range_days` (int or 7) / `default_boards` (`;`→`,` then split). **`ticket_types` is read inside `ReportService.generate`'s background task (applies to BOTH manual & scheduled runs), NOT the scheduler trigger.**
- **KB FTS5/BM25:** bm25 `ORDER BY score ASCENDING` (lower=better — classic inversion bug), `top_k=8`; `_match_expression` (`[A-Za-z0-9_]+`, len>1, de-dup, cap 20, quoted-OR, None→`[]`); ~1000-char greedy chunker on blank lines, **no sub-split of oversized paragraphs**; storage `kb/{project_id}/{uuid}-{basename}` with `os.path.basename` traversal-strip; delete order chunks→file→row→commit (swallow missing-file).

### 5.10 Frontend contracts 🔴
- **Single base-URL builder over `VITE_QA_API`** used by ALL transports: axios-client, React Query, **EventSource** (`useEventStream`), ReportsPage **iframe src** + **two download anchors** (history-card + viewer-dialog header), SettingsPage **`window.open` preview**. (Today all transports use bare `/api` via the Vite dev proxy → :5250; no baseURL/`VITE_QA_API` exists yet.) SSE can't send headers → query-param auth or proxy. Consolidate the duplicated `axiosMessage` into the interceptor.
- **Polling → React Query (exact):** list **3000ms** gated-on-visible-in-flight + `mergeById` stable keys (RQ `select`/structural-sharing so the grid doesn't remount/lose scroll); detail **2500ms** + `onTerminal`→invalidate-list; epicRuns 3000/2500 gated on `derivedStatus==='Running'`; ReportsPage **2500ms cap 600**. Map ~15 mutation `refetch()` sites to invalidation tags.
- **Hardcoded ADO_ORG/ADO_PROJECT deep links = BUG FIX:** source `ado.organization`/`ado.project` from `GET /api/settings/config` (shared slice/query); rebuild the **5 deep-link builders** (authorTests.tsx ×2, findBugs.tsx ×3) + Projects/Layout display text; remove the literals (they live ONLY in authorTests.tsx + findBugs.tsx — SettingsPage references the setting KEYS `ado.organization`/`ado.project`, not literals; leave its K-map alone). Centralize `PROJECT_ID=1` (6 files) into one config/slice (keep =1, matches backend seed).
- **Frozen client-side literals:** ~26 dotted Settings keys; provider enum spellings; `REPORT_TYPE_OPTIONS`; post-push CSV header order; KB multipart field `files` + Doc/Hit shapes + k=8; Analysis camelCase fields + `questionAnswers` index-alignment + evidence-tag regex `/\s*\[(src:\s*[^\]]+|ticket|assumed|unclear)\]\s*$/i`; ConsoleEvent unions (kind {phase,token,thinking,system,stat,result,error}, level {info,success,warn,error}, phase incl `code-verify`+null; terminal-close on `ev.data.terminal`) + `PHASE_LABEL` (**TWO copies that DIFFER only on `bugwriter`**: shared.tsx=`BUG WRITER`, AgentConsole.tsx=`BUGWRITER` — decide canonical on consolidation); AnalystProgress 9 STEPS (step[0] keyless intro; steps[1..8] → keys module/acceptanceCriteria/businessRules/edgeCases/negativeCases/permissions/crossFlow/openQuestions; forward-only, filler bar caps 92%); post-push CSV header order `[SessionId,StoryId,StoryTitle,TestCaseId,TestCaseTitle,PlanId,PlanName,SuiteId,SuiteName,AdoUrl]` (esc `/[",\n]/`→quote-double); EpicRuns `derivedStatus ∈ {Running,AwaitingInput,CompletedWithFailures,Completed}`; `pipeline.ts` status/draft/severity color maps + IN_FLIGHT set (keep AwaitingTester in ProjectsPage donut).
- **Preserve the SessionModuleConfig engine** (ONE generic `SessionWorkspace`/`SessionDetailFullScreen`/`epicRuns`/shared-hooks — do NOT duplicate per feature). find-bugs's `/api/author-tests/search` call lives in shared/common api. Provider state in a shared slice (AppBar+Settings+diagnostics in sync). `activeStepIndex(status,activity)` + per-module `inFlightStatuses`.
- **Routing/provider shell:** 4 full-screen routes (epic before `:sessionId`) OUTSIDE Layout; nested Layout `<Routes>` (6 nav + root-`/`→Settings + `/diagnostics` alias + `*`→Coming soon); `DRAWER_WIDTH=244`; `layoutId='nav-active'`; two AnimatePresence regions (ReportsPage iframe OUTSIDE it); **preserve `MotionConfig reducedMotion='user'`**. ColorMode key `qa-color-mode` (preserve `resolveInitialMode` precedence + plain non-colorSchemes MUI mode); preserve `index.html` font links + the 25-step shadow ramp.
- Provider shell order (target): `StrictMode > MotionConfig(reducedMotion='user') > ErrorBoundary > QueryClientProvider > Redux Provider > ColorModeProvider(ThemeProvider + CssBaseline) > BrowserRouter > Routes` — keep MotionConfig + ColorModeProvider (which mounts ThemeProvider+CssBaseline); ADD ErrorBoundary/QueryClient/Redux (none exist yet). Component specs to preserve: `AgentConsole` (streaming-merge, `PHASE_LABEL`, stat tracking, follow-pause, 3 consumers), `AnalystProgress` (9 steps; 8 keyed), `SessionTimeline` (noise-filter kinds), `AgentPipeline` (per-module step arrays, onSelect phase nav), `SessionWorkspace` (3 tabs, **PAGE_SIZE=17** in two places — list pagination const AND AnalyticsTable's `.slice(0,17)` literal; keep equal, accent map).

### 5.11 Config / settings keys
- **Secrets from `.env`:** `ADOACCESS_PAT`, `ANTHROPIC_API_KEY`, `AZURE_OPENAI_ENDPOINT/_API_KEY/_DEPLOYMENT`, `OPENAI_BASE_URL/_API_KEY/_MODEL` (+ optional `CLAUDE_CLI_PATH/MODEL/TIMEOUT`, `BACKEND_PORT`). PAT scopes: Work Items R/W, Test Management R/W, Code Read.
- **Settings table (UI-editable, DB-over-env):** `ALL_KEYS` = exactly **26** dotted keys; secret set `{ado.pat, anthropic.api_key, azure_openai.api_key, openai.api_key}` (4); GET masks each non-empty secret to a FIXED `••••••••` (8 × U+2022, **no suffix, regardless of length**; empty→`''`) + emits `{key}.set` bool; empty-secret on PUT = unchanged; silent-ignore unknown keys; on `apply()` `claude_cli.timeout_seconds` is clamped `max(10,int(value))` (non-int ignored→prior kept; raw value stored unclamped); on PUT, **rebuild ADO + LLM clients**.
- **Env-default literals:** `schedule_enabled='true'`, `default_range_days='7'`, `ticket_types='User Story;Bug;Defect;Task'`, `verify_detail='step'`, `verify_step_scope='fails_only'`, `bug_from_case_mode='instant'`, `team_name=''`, `default_boards=''`, `llm_provider_default=Anthropic`. ADO house-style defaults: org `YourOrg`, project `Your Project`, repo `Your Repo`, branch `main`, bug area path `Your Project\Your Team`, QA owner `Mahmood Ahmad`, api-version `7.1`. Ports: backend `5250`, client `5173`.

---

## 6. Phased execution plan (definition-of-done = the §5 contracts for that area)

**Backend (App Service path):**
- **B0 — Skeleton.** Solution + 4 projects; `Program.cs` `AddCustom*`; Swagger; CORS (`:5173`,`:5174`); ProblemDetails; `ExceptionHandlingMiddleware`; `GET /api/health`; **flat `/api` routes (no versioning)**; recover `7b16806` Core options/models as a starting point — **STRIP any `api/v{version}` routing from the recovered scaffold AND the `YourProduct.WebAPI` pattern; it must NOT be carried over** (Risk register row 1).
- **B1 — Data layer.** EF Core 8 `QaDbContext`, entities with frozen names (§5.3), enums as strings, FK config, FtsBootstrapper, seed `Project id=1`. Baseline migration (D6 = **adopt**, confirmed 2026-06-18); `Migrate()` on boot; ensure `Theme` server_default `Executive` for legacy rows. **`seed_demo` DEMO band `[188000-188999]`: explicitly DROPPED** (port only if demo data is needed). **Startup ordering (🔴, single contract):** `Migrate()` → FtsBootstrap (`doc_chunks`) → seed `Project id=1` (idempotent) → `AppSettings.Apply` (patch options + rebuild ADO/LLM clients) → start `WeeklyReportScheduler` **last** (a hosted startup service must finish the first four before the scheduler ticks).
- **B2 — ADO client.** §5.5 in full (host/version matrix, frozen bodies, reads incl. Git/code-search, AdoApiError + sanitizer, 60s cache, repo-id cache).
- **B3 — LLM layer.** §5.6 (4 providers, factory per-call, shared max_tokens, per-provider sanitization, StreamProgress, ClaudeCli idle+absolute timeout).
- **B4 — Agents + JsonUtils.** §5.7 (prompts verbatim from Python; CodeVerifier single-shot; string-aware extract_json; coercion).
- **B5 — Orchestrators + EventBus + SSE.** §5.8 (singleton lifetime, cancellation threading, background-scope, EventBus constants, SSE headers, CurrentActivity strings, full code-verify subsystem, bug-from-verdict).
- **B6 — Knowledgebase.** §5.9 KB.
- **B7 — Reports + scheduler.** §5.9 reports (HTML deck, window.__REPORT__ escape, board-name, summarizer never-block, Thu 11:00 Asia/Karachi).
- **B8 — Batches.** §5.4 batches + EXCLUSION-list `get_child_work_items`, concurrent fan-out, derived rollup, cancel force-transition.
- **B9 — Settings/Diagnostics + AppSettings.** §5.11 (IOptionsMonitor, masking, rebuild clients), ADO discovery, diagnostics, provider switch.
- **B10 — Tests + parity verification.** §8.

**Frontend (architecture-only realign, can run in parallel against the Python backend until F5):**
- **F0** add `@reduxjs/toolkit`, `react-redux`, `@tanstack/react-query`; scaffold `app/`, `api/`, `config/`, `common/`, `routes/`; mount provider shell (§5.10).
- **F1** centralized `axios-client` + `config` + single base-URL builder; migrate inline `/api` calls into per-feature `api/*` (behavior-preserving). Fix the ADO_ORG/PROJECT deep-link bug; centralize PROJECT_ID.
- **F2** React Query feature-by-feature (Author Tests → Find Bugs → Batches → Reports → KB → Settings) with the exact intervals/gates; keep `useEventStream`.
- **F3** Redux slices for cross-cutting UI state (selection/dialogs/provider/color-mode).
- **F4** relocate `pages/`+`session/` into `components/<feature>/` modules + central `routes/Routes.tsx` (preserve the SessionModuleConfig engine + literals).
- **F5** point `config` at the .NET App Service backend; verify against §8.

**Milestones:** M1 skeleton+ADO+LLM+agents (B0–B4) · M2 Author-Tests e2e (B5 author + B6 + B9) · M3 Find-Bugs+verify+reports+batches+SSE (B5 bug + B7 + B8) · M4 frontend realign (F0–F4) · M5 cutover + golden verification + ADO deploy.
**Rough effort (1 eng):** backend ≈ 6–7 wks, frontend ≈ 2–3 wks (parallel), CI/infra ≈ 1 wk.

---

## 7. Hosting & CI/CD on Azure DevOps (separate project; no Docker — zip + Bicep + Front Door)

> **DEFERRED (CONFIRMED 2026-06-18).** This entire section is the **future company-Azure deploy path**, implemented *after* the local migration is done and verified — NOT during the local build. For now the app runs locally (D1); there is no Azure infra, no Bicep, no Key Vault, no App Insights, and no CI/CD pipeline. Keep the .NET architecture App-Service-compatible so this section can be executed later with minimal change.
- **Backend → App Service**, pipeline modeled on `YourProduct.WebAPI/BuildPipelines/azure-pipelines-qa.yml`: `UseDotNet@2 (8.x)` → restore/build/test → `publish --configuration Release` → zip → deploy via **`AzureWebApp@1`/`AzureRmWebAppDeployment@4`**. One YAML per branch with path-filtered triggers.
- **Frontend → Storage static website**, modeled on `web-app/build/build-branch.yml`: `NodeTool@0 (18.x)` → `npm ci` → `vite build` (`VITE_QA_API` per env) → `AzureCLI@2 az storage blob upload-batch --destination '$web'`. **Set the static-website 404 document to `index.html`** (SPA fallback) or every deep link/refresh 404s.
- **Infra (`infra/` Bicep):** `appServicePlan.bicep` (B1, Always On), `webApp.bicep` (`kind=app`, `v8.0`), `storageAccount.bicep` (static site), `appInsights.bicep`; secrets via **Key Vault refs + User-Assigned Managed Identity**. Optional `frontDoorRoutes.bicep` (`/qa-api/*`→App Service, `/*`→static site).
- For an internal single-user tool, a single App Service + App Insights + Key Vault (no Front Door / no per-branch envs) is sufficient.
- **ADO prereqs:** new repo + project, ARM service connection, build definitions, Key Vault seeded with the §5.11 secrets, UAMI with KV + RG access.

---

## 8. Golden-file / contract test scope (B10 — build from scratch; run Python + .NET, diff)
- ADO write bodies: Steps XML (+ apostrophe/quote escaping), bug ReproSteps/AC/SystemInfo HTML, json-patch shapes, suite-add body, severity default `3 - Medium`, Priority integer 2; `_parse_steps_xml` round-trip.
- `sanitize()` + `AdoApiError.str`; LLM per-provider error wrapping.
- `json_utils` (dict/bool/None/blank/non-ASCII inputs); agent output DTO keys; verdict statuses.
- CurrentActivity/phase strings incl. **QUEUED + CANCELLED paths**; SSE frame keys + headers.
- Enum-as-string assertions read from the migrated SQLite DB; Settings dotted-key round-trip; route-precedence (`/preview`, `/sessions/detail/{id}`).
- KB bm25 ordering + match-expression; report board-name derivation + `window.__REPORT__` escape.
- Client↔backend enum-string contract test.
- **Verification harness must NOT use PowerShell for non-ASCII payloads** (PS 5.1 mangles non-ASCII to `?`) — use axios/browser/python httpx for Unicode (em-dash, bullets, `→`, accents).

---

## 9. Risk register (top "what breaks")
| Risk | Mitigation |
|---|---|
| 🔴 Adopting org URL-versioning | Routes stay flat `/api/<router>` (§5.4). |
| 🔴 Enum→int / camelCased | `JsonStringEnumConverter` exact-name + EF `.HasConversion<string>()` (§5.1–5.2). |
| 🔴 SSE buffered by App Service/Front Door | `X-Accel-Buffering: no` + `Cache-Control: no-transform` (§5.8); buffering test in M5. |
| 🔴 EF vs Alembic history mismatch wipes data | D6 baseline-migration adoption; forbid EnsureCreated (§2, B1). |
| 🔴 Forcing Functions breaks streaming/state | ASP.NET Core on App Service (D1). |
| 🔴 Cancellation no-op | Thread CancellationToken into every await + semaphore (§5.8). |
| 🔴 Singleton EventBus/orchestrators not set | Register singleton; single-instance (§5.8). |
| 🔴 IOptions immutable / pooled PAT stale | IOptionsMonitor + per-request PAT (§4, B9). |
| 🔴 SPA deep-link 404 | Storage 404 doc = index.html (§7). |
| 🔴 Base-URL only on axios | One builder for EventSource/iframe/window.open too (§5.10). |
| 🟠 Frozen-literal drift (prompts, Steps XML, activity strings) | Golden-file tests vs Python (§8); copy, don't retype; Python is source of truth. |
| 🟠 Scheduler timezone (UTC default) | Explicit Asia/Karachi `TimeZoneInfo` + Cronos (§5.9). |

---

## 10. Decisions — CONFIRMED 2026-06-18 (locked with the user before B0)
- **D1 — Hosting:** **ASP.NET Core 8, App-Service-compatible** (NOT Functions). Develop & run **locally for now** (Kestrel :5250, SQLite file, local `.env`/User Secrets — zero Azure cost). The company deploys to **Azure App Service later**, so keep the architecture deploy-ready; §7 (Bicep/Front Door/Key Vault/App Insights/Storage/CI) is **DEFERRED, not deleted** — do not build Azure infra now.
- **D2 — DB:** **SQLite + EF Core Migrations** (free, embedded, matches live `qa.db`). Enums as strings; `Migrate()` on boot; no `EnsureCreated/Deleted`.
- **D3 — Anthropic client:** **official/best free .NET package** with streaming support (NOT hand-rolled `HttpClient`); confirm the exact current package at B3 — do not guess the name from memory. Azure/OpenAI keep their official SDKs.
- **D5 — Repo home:** scaffold the .NET solution in a **new top-level `dotnet/` folder in THIS repo** during migration (Python stays in `backend/` as the golden-diff reference). **After the migration is complete + verified, `dotnet/` MOVES to `backend/`, retiring the Python.** Source control = this repo; separate repo/ADO project deferred to the future Azure deploy.
- **D6 — Existing DB:** **Adopt** live `qa.db` via a baseline migration recorded as already-applied (no DDL, no data loss). Implement in B1.
- **CI/Logging:** **None** — local run + `ILogger` to console + a local file. No pipelines, no App Insights (added later with the Azure deploy).

> **Final rule for the implementing agent:** when in doubt, run the live Python app and the .NET app side-by-side and diff. The Python behavior is the contract. Do not normalize casing, "fix" odd literals, change enum/JSON shapes, or refactor the SessionModuleConfig engine. Work phase by phase; a phase is done only when its 🔴 contract items pass a golden diff.
