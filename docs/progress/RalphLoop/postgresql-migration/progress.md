# Progress: PostgreSQL Migration Implementation

## Overview
- **Ruleset:** `Ralph loop.md` (follow it every iteration)
- **Creation sequence:** solution.md → blueprint.md → prd.md → progress.md
- **PRD:** prd.md · **Design:** solution.md · **Guide:** blueprint.md
- **Branch:** `claude/chat-session-flzhjh`
- **Started:** 2026-07-28
- **Status:** DESIGN COMPLETE. The RalphLoop doc set + both setup guides are written; **backend
  implementation not started.** Backend/DB tasks will be CODE-COMPLETE once written+pushed and VERIFIED
  only after the user runs `dotnet build`/`dotnet test`/Docker/Azure locally (no .NET SDK or Docker in
  the container). Deadline: go live this week for the QA team.

## Status legend
- **VERIFIED** — implemented + verified green in this environment (or trivially-verifiable docs).
- **CODE-COMPLETE** — implemented + pushed; backend build/test/migration/Docker verification pending on the user's local machine.
- **NOT STARTED** — not begun.

---

## Task Progress

| Task ID | Task Name | Status | Started | Completed | Notes |
|---------|-----------|--------|---------|-----------|-------|
| PGM-001 | Add Npgsql EF Core provider package | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11 added; user restores/builds locally |
| PGM-002 | Boot-time provider selection + DatabaseOptions | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | DatabaseOptions enum; Program.cs reads DATABASE_PROVIDER (+ legacy-timestamp switch); AddInfrastructure branch (SQLite overload kept for tests) |
| PGM-003 | Design-time factory provider branch | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | QaDbContextFactory branches on DATABASE_PROVIDER |
| PGM-004 | ITextSearchProvider abstraction | NOT STARTED | | | Phase 2 |
| PGM-005 | SqliteFts5Search + PostgresFtsSearch | NOT STARTED | | | SQLite impl moved verbatim |
| PGM-006 | Generate the Npgsql migration set | NOT STARTED | | | Local only (needs .NET SDK) |
| PGM-007 | DatabaseInitializer provider branch | NOT STARTED | | | Gate sqlite-isms |
| PGM-008 | KnowledgebaseService delegates FTS | NOT STARTED | | | |
| PGM-009 | ExistingTestsIndexService delegates FTS | NOT STARTED | | | |
| PGM-010 | Register ITextSearchProvider by provider (DI) | NOT STARTED | | | |
| PGM-011 | .env keys | VERIFIED | 2026-07-28 | 2026-07-28 | DATABASE_PROVIDER + POSTGRES_CONNECTION_STRING documented in .env.example (config text) |
| PGM-012 | Local Docker setup guide | VERIFIED | 2026-07-28 | 2026-07-28 | docs/setup/postgresql-docker.md written |
| PGM-013 | Azure deployment guide | VERIFIED | 2026-07-28 | 2026-07-28 | docs/setup/postgresql-azure.md written |
| PGM-014 | Local verification (SQLite green + PG round-trip) | NOT STARTED | | | User-run |
| PGM-015 | Feature doc + changelog | NOT STARTED | | | After verification |

---

## Summary
- **Total Tasks:** 15
- **VERIFIED:** 3 (two setup guides + .env keys)
- **CODE-COMPLETE:** 3 (PGM-001/002/003 — Phase 1 provider plumbing)
- **NOT STARTED:** 9

---

## Log

### 2026-07-28 (Phase 1 — provider plumbing; SQLite default unchanged)
- **PGM-001** — added `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.11 to the Infrastructure csproj
  (alongside the SQLite provider). Version tracks EF Core 8.x.
- **PGM-002** — new `Core/Configuration/DatabaseOptions.cs` (`DatabaseProvider { Sqlite, Postgres }` +
  `DatabaseOptions`). `Program.cs` reads `DATABASE_PROVIDER` (default `sqlite`); for `postgres` it reads
  `POSTGRES_CONNECTION_STRING`/`DATABASE_URL` (fail-fast if absent) and sets
  `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`. `AddInfrastructure` gained a
  provider-aware overload that branches `UseNpgsql(cs, EnableRetryOnFailure)` vs
  `UseSqlite(cs)+SqliteBusyTimeoutInterceptor`; the **original 2-arg SQLite overload is kept** so the two
  test callers (`LlmProviderFactoryTests`, `SettingsServiceTests`) and any legacy caller still compile.
- **PGM-003** — `QaDbContextFactory` branches on `DATABASE_PROVIDER` so `dotnet ef` targets either
  provider (Postgres falls back to a dummy design-time connection string, fine for `migrations add`).
- **PGM-011** — documented `DATABASE_PROVIDER` + `POSTGRES_CONNECTION_STRING` in `.env.example`; default
  keeps SQLite.
- **Safety:** with no env change the app still selects SQLite via the exact original code path
  (`$"Data Source={dbPath}"` + interceptor) — zero behavior change; SQLite remains the default.
- **CODE-COMPLETE, pending local verify:** the backend can't be built in the container (no .NET SDK). The
  user runs `dotnet build` + `dotnet test` locally to confirm restore + green tests before this is
  VERIFIED.
- **Next:** Phase 2 — `ITextSearchProvider` (PGM-004) + `SqliteFts5Search`/`PostgresFtsSearch` (PGM-005),
  moving the SQLite `bm25`/`MATCH` logic verbatim so BM25 tests stay green.

### 2026-07-28 (Design + setup guides authored)
- Created the RalphLoop feature folder `docs/progress/RalphLoop/postgresql-migration/` and authored the
  doc set in sequence: `solution.md` (design), `blueprint.md` (file-by-file guide), `prd.md` (15-task
  PGM-### backlog), this `progress.md`, and copied `Ralph loop.md` (ruleset).
- Wrote both setup guides (PGM-012, PGM-013): `docs/setup/postgresql-docker.md` (install → run → link →
  switch, step by step) and `docs/setup/postgresql-azure.md` (Flexible Server → App Service → migrate →
  smoke test).
- Design anchored on verified exploration: DbContext wired to SQLite at `DependencyInjection.cs:39-41`,
  provider chosen once at boot (→ restart-to-switch, fits "SQLite until verified"); FTS5 concentrated in
  `KnowledgebaseService` + `ExistingTestsIndexService` + `DatabaseInitializer`; 11 SQLite-shaped
  migrations (→ parallel Npgsql set); DateTime→timestamptz gotcha (→ `EnableLegacyTimestampBehavior`).
- Azure question resolved: Azure Database for PostgreSQL Flexible Server is the managed target; free 12
  months on a new free account (B1ms/750hrs/32GB) or any paid tier on company Azure; provider-agnostic
  design ⇒ Azure = connection-string swap + `SSL Mode=Require`, backend on App Service.
- **Next (implementation, Phase 1 first):** PGM-001 Npgsql package → PGM-002 boot-time selection (SQLite
  default unchanged) → PGM-003 design-time factory. Then FTS abstraction, service wiring, initializer
  branch, and the Npgsql migration. Each backend task lands CODE-COMPLETE pending the user's local
  `dotnet`/Docker verification.

---

## Pending / next actions
1. Implement Phase 1 (PGM-001..003) — SQLite default must stay green with no env change.
2. Phase 2 FTS abstraction (PGM-004..005) — move the SQLite impl verbatim to keep BM25 tests green.
3. Phase 3 wire services + initializer (PGM-007..010).
4. Phase 4 Npgsql migration (PGM-006) — **user runs `dotnet ef` locally**.
5. Phase 5 config (PGM-011) — .env keys.
6. PGM-014 local verification (user), then PGM-015 feature doc + changelog.
