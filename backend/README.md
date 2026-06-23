# QA Automation — .NET backend

The .NET 8 backend for the `ADO_Tester` QA Automation app. It runs locally (Kestrel + SQLite, zero
cloud cost) and stays **Azure App Service-ready** for a later company deploy. This is now the **sole
backend**: it replaced the original Python FastAPI app, which was **retired on 2026-06-21** (recover
it from git history if a parity reference is ever needed).

- Authoritative migration brief + locked decisions: [`../docs/dotnet-migration-spec.md`](../docs/dotnet-migration-spec.md) (§10).
- End-to-end runtime status + current open issues: [`../docs/manual-verification-and-open-issues.md`](../docs/manual-verification-and-open-issues.md).

## Layout

```
backend/
  QaAutomation.sln
  src/
    QaAutomation.Api/            # ASP.NET Core 8 host — Program.cs, Controllers/, Middlewares/, Streaming/ (SSE)
    QaAutomation.Core/           # Abstractions/, Configuration/ (options), Models/ (entities/DTOs), Agents/ (JsonUtils)
    QaAutomation.Infrastructure/ # EF data, ADO client, LLM, agents, orchestrators, events, KB, reports, batches, settings, logging
  tests/
    QaAutomation.Tests/          # xUnit
  data/                          # qa.db + KB uploads + report HTML — git-ignored, auto-created on first run
```

## Run (local dev)

```powershell
# from repo root
dotnet run --project backend\src\QaAutomation.Api
```

- Listens on `http://localhost:5250` (the `http` launch profile / canonical backend port); override with `ASPNETCORE_URLS`.
- Health: `GET /api/health` → `{"status":"ok"}`
- Swagger UI (Development only): `/swagger`
- Secrets load from the repo-root `.env` (DotNetEnv walks up the tree). The SQLite DB + FTS5 index auto-create on first run, adopting an existing `data/qa.db` if present — no separate migration step.

## Phase status (see spec §6)

The full migration has landed. B0–B4 below were each verified by golden-file diffs against the
then-live Python plus adversarial port-parity audits. B5+ (orchestration/SSE/EventBus, KB, reports,
batches, settings/diagnostics, and the eight feature controllers) is built and smoke-tested end-to-end
— see the [verification log](../docs/manual-verification-and-open-issues.md) for what is confirmed
working and the remaining open issues (notably large-ticket Tester generation, Open Issue #1).

- **B0 — Skeleton ✅** solution + 4 projects, `Program.cs` (`AddCustom*`), Swagger, CORS (`:5173`/`:5174`),
  ProblemDetails + `ExceptionHandlingMiddleware` (first), flat `/api` routes, `GET /api/health`. Builds clean.
- **B1 — Data layer ✅** EF Core 8 `QaDbContext` over the frozen schema (9 entities, enums-as-strings,
  3 CASCADE FKs, `BatchId` no-FK indexes, `Theme`/`Title` server defaults); `InitialBaseline` migration;
  `DatabaseInitializer` adopts the existing Alembic `qa.db` via a baseline-history insert (D6, no data
  loss) or builds fresh; FTS5 `doc_chunks` bootstrap; seed Project id=1. Verified by 2 xUnit tests
  (adopt-no-data-loss + fresh/idempotent) and a live app-boot against a DB copy.
- **B2 — ADO client ✅** full port of `ado_client.py` (~28 methods) in `AzureDevOps/AzureDevOpsClient.cs`:
  base/auth (PAT Basic via `AdoAuthHandler`, 30s timeout, 60s WI cache, repo-id cache), the host/api-version
  matrix (hardcoded vs configurable), byte-frozen write bodies (Steps XML w/ `'`→`&#x27;`, bug
  ReproSteps/AC/SystemInfo HTML, json-patch op order, integer Priority), all reads incl. Git/code-search,
  `AdoApiError` + `Sanitizer`. Verified: **golden-file diff vs live Python** (`golden_capture.py` →
  `tests/.../golden/ado_golden.json`, 7 byte-equal tests) + an **adversarial port-parity audit** (workflow,
  12 agents) whose 8 confirmed divergences are all fixed. 16/16 tests green.
- **B3 — LLM layer ✅** 4 providers in `Llm/` — Anthropic (raw HttpClient per D3, since the official
  SDK is beta), Azure OpenAI + OpenAI-compatible (official `Azure.AI.OpenAI`/`OpenAI` 2.1 SDKs), Claude
  CLI (Process streaming: stdin prompt, idle + 1800s timeouts, emit-coalescing, stream-json classify) —
  plus a per-call no-cache `LlmClientFactory`, DB-persisted `LlmProviderState`, shared `StreamProgress`,
  shared `ClaudeCli` locator/probe, per-provider frozen error prefixes + sanitization. Verified: 35/35
  tests + adversarial parity audit (14 agents) — 5 confirmed divergences fixed.
  - **Accepted deviations (documented, not bugs):** (1) Anthropic error `{body}` is the raw JSON
    response, vs Python's anthropic-SDK-decorated `"Error code: N - {python-dict-repr}"` — a direct
    consequence of the D3 raw-HTTP choice; the prefix + status match. (2) Claude CLI surfaces the
    friendly "Could not launch…" message for *all* process-start failures, vs Python only for
    FileNotFoundError (a minor UX improvement; the common not-found path is identical).
- **B4 — Agents + JsonUtils ✅** Analyst/Tester/BugWriter/CodeVerifier in `Infrastructure/Agents/` with
  system prompts **extracted byte-exact** from Python into embedded resources (`write_prompts.py` →
  `AgentPrompts`). `Core/Agents/JsonUtils` (string-aware ExtractJson, CoerceString/List, CompleteJsonAsync
  retry+nudge) + rewritten `AnalystOutput` (dual-case FromJson, frozen-order ToJson) + agent-output DTOs.
  Verified: 48/48 tests + adversarial parity audit (16 agents) — 11 findings, 1 fixed (duplicate-key
  payloads now degrade to a graceful retry/AgentException instead of an unwrapped ArgumentException crash).
  - **Accepted deviations (malformed-input-only; valid agent output is byte-identical):** the prompts
    mandate plain-string fields, so these never fire on conforming output: (1) scalar-field coercion
    (`StrOr`) and `CoerceString` differ from Python's `str()`/`json.dumps` only for non-string values in
    string-typed fields (`bool`→`true` not `True`; `0`/`false` don't fall through; exponential floats
    `1e10` vs `10000000000.0`; array spacing `[1,2]` vs `[1, 2]`); (2) bare `NaN`/`Infinity` are rejected
    (Python accepts them as `"nan"` garbage) and duplicate keys degrade to a retry (Python last-wins);
    (3) the all-attempts-failed error snippet uses simple quoting vs Python `repr()`.
- **B5+ — built & smoke-tested ✅** orchestrators/SSE/EventBus, KB, reports, batches,
  settings/diagnostics, and the eight feature controllers (AuthorTests, FindBugs, Batches,
  Knowledgebase, Reports, Settings, Diagnostics, Logs) are all in place; the frontend re-architecture
  (F0–F5) is done. Confirmed end-to-end against live ADO + `ClaudeCli` in the verification log; the
  one blocking open issue is large-ticket Tester generation (Open Issue #1).
