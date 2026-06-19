# QA Automation — .NET backend (migration in progress)

The .NET 8 re-platform of the `ADO_Tester` backend. Built to **run locally now** (Kestrel + SQLite,
zero cloud cost) and stay **Azure App Service-ready** for a later company deploy. The authoritative
brief is [`../docs/dotnet-migration-spec.md`](../docs/dotnet-migration-spec.md) — read its §10 for the
locked decisions (local-first, SQLite, adopt the existing `qa.db`, official Anthropic package).

> **The live Python backend in `../backend/` is the source of truth.** During the migration the .NET
> lives here in `dotnet/`; once it reaches parity and is verified, this tree **replaces `../backend/`**.

## Layout

```
dotnet/
  QaAutomation.sln
  src/
    QaAutomation.Api/            # ASP.NET Core 8 host — Program.cs, Configurations/, Middlewares/
    QaAutomation.Core/           # Abstractions/, Configuration/ (options), Models/ (entities/DTOs)
    QaAutomation.Infrastructure/ # EF data, ADO client, LLM, agents, orchestrators, KB, reports
  tests/
    QaAutomation.Tests/          # xUnit
  _recovered/                    # see below — NOT compiled
```

### `_recovered/` — reference only, not built
Scaffolding recovered from git `7b16806` that depends on a not-yet-built Data layer (it was only
~40% complete and does **not** compile as-is). It is parked here, **outside every `.csproj`**, as a
starting point. Each later phase pulls the relevant file back into a project and rewrites it,
**diffing against the Python**, never trusting the scaffold. Do not add `_recovered/` to a project.

## Run (local dev)

```powershell
# from repo root
dotnet run --project dotnet\src\QaAutomation.Api
```

- Health: `GET /api/health` → `{"status":"ok"}`
- Swagger UI (Development only): `/swagger`
- Override the port with `ASPNETCORE_URLS=http://localhost:5250` (the canonical backend port).

## Phase status (see spec §6)

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
- **B5+ — pending:** orchestrators/SSE/EventBus, KB, reports, batches, settings/diagnostics,
  comprehensive tests. (B5–B10 / F0–F5 in the spec.)
