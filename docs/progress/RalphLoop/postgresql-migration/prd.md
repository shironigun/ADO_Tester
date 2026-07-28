# PRD: PostgreSQL Migration — dual-provider (SQLite default), Azure-ready

## Project Overview
Make the ADO_Tester data layer **dual-provider**: keep **SQLite as the default** (unchanged, always the
safe fallback) and add **PostgreSQL** as an opt-in provider selected at boot via `DATABASE_PROVIDER`
(default `sqlite`). PostgreSQL runs in **local Docker** for development/verification and on **Azure
Database for PostgreSQL Flexible Server** in production, with the .NET backend on **Azure App Service** —
the same code, only the connection string differs (`SSL Mode=Require` for Azure). The one genuinely
provider-specific capability, **full-text search** (SQLite FTS5 `bm25`/`MATCH` for KB `doc_chunks` and
dedup `test_case_index`), is isolated behind a new `ITextSearchProvider` with a SQLite implementation
(moved verbatim) and a PostgreSQL implementation (`tsvector`/`ts_rank` + GIN indexes on real EF-modeled
tables). A **parallel Npgsql migration set** is added (the SQLite migrations are frozen); `DatabaseInitializer`
is provider-branched (SQLite keeps adopt-`qa.db` + fts5 bootstrap, Postgres migrates fresh); and
`Npgsql.EnableLegacyTimestampBehavior` covers the DateTime/`timestamptz` gotcha for a low-risk first
port. **Data policy: fresh start only** — Postgres begins empty; no ETL. Deliverables include a
step-by-step local Docker setup guide and an Azure deployment guide. No ADO write body, embedded prompt,
or bug-pipeline behavior changes — data-layer only.

## Created
- **Date:** 2026-07-28
- **Author:** Mahmood Ahmad + Claude
- **Branch:** `claude/chat-session-flzhjh`
- **Companion docs:** `solution.md` (design) · `blueprint.md` (guide) · `progress.md` (status + log) · `Ralph loop.md` (ruleset)

## Status legend
- **`[x]` VERIFIED** — implemented and verified green in this environment (or trivially verifiable docs).
- **`[~]` CODE-COMPLETE** — implemented and pushed; backend `dotnet build`/`dotnet test`/migration/Docker verification **pending on the user's local machine** (no .NET SDK or Docker in the container).
- **`[ ]` TODO** — not yet done.

---

## Tasks

### PHASE 1 — PROVIDER PLUMBING (SQLite default unchanged)

#### PGM-001: Add Npgsql EF Core provider package
- **Status:** [ ]
- **ID:** PGM-001
- **Description:** blueprint.md Phase 1. Add the Npgsql EF Core provider so `UseNpgsql` is available; SQLite provider stays.
- **File:** `backend/src/QaAutomation.Infrastructure/QaAutomation.Infrastructure.csproj`
- **Acceptance Criteria:**
  - `Npgsql.EntityFrameworkCore.PostgreSQL` referenced at an 8.x version matching EF Core 8
  - `Microsoft.EntityFrameworkCore.Sqlite` 8.0.11 remains; solution restores/builds

#### PGM-002: Boot-time provider selection + DatabaseOptions
- **Status:** [ ]
- **ID:** PGM-002
- **Description:** solution.md Provider Selection. Add `DatabaseProvider` enum + `DatabaseOptions`; read `DATABASE_PROVIDER` (default `sqlite`) + `POSTGRES_CONNECTION_STRING` in `Program.cs`; enable the Npgsql legacy-timestamp switch when postgres; generalize `AddInfrastructure(config, provider, connectionString)` and branch `UseSqlite`(+interceptor) vs `UseNpgsql`(+retry).
- **File:** `backend/src/QaAutomation.Core/Configuration/DatabaseOptions.cs`, `backend/src/QaAutomation.Api/Program.cs`, `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
- **Acceptance Criteria:**
  - Default (no env) selects SQLite with the existing `Data Source={dbPath}` + `SqliteBusyTimeoutInterceptor` — zero behavior change
  - `postgres` selects `UseNpgsql` with `EnableRetryOnFailure`, no SQLite interceptor
  - Blank connection string when `postgres` fails fast with a clear message; unknown provider → sqlite
  - Builds

#### PGM-003: Design-time factory provider branch
- **Status:** [ ]
- **ID:** PGM-003
- **Description:** solution.md Provider Selection. `QaDbContextFactory` reads `DATABASE_PROVIDER` and branches so `dotnet ef` targets either provider.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/QaDbContextFactory.cs`
- **Acceptance Criteria:**
  - postgres → `UseNpgsql(Env("POSTGRES_CONNECTION_STRING"), n => n.MigrationsAssembly(...))`; else current `UseSqlite`
  - `dotnet ef` commands resolve the context under both providers

### PHASE 2 — FULL-TEXT SEARCH ABSTRACTION

#### PGM-004: ITextSearchProvider abstraction
- **Status:** [ ]
- **ID:** PGM-004
- **Description:** solution.md FTS Abstraction. Interface covering FTS ensure + KB `doc_chunks` upsert/delete/search + dedup `test_case_index` upsert/delete/search.
- **File:** `backend/src/QaAutomation.Core/Abstractions/ITextSearchProvider.cs`
- **Acceptance Criteria:**
  - `EnsureAsync`, `UpsertDocChunkAsync`, `DeleteDocChunksAsync`, `SearchDocChunksAsync(projectId, query, topK)`, `UpsertTestCaseAsync`, `DeleteTestCasesForScanAsync`, `SearchTestCasesAsync(scanId, query, top)`
  - Returns ranked results (doc/entry ids); compiles

#### PGM-005: SqliteFts5Search + PostgresFtsSearch implementations
- **Status:** [ ]
- **ID:** PGM-005
- **Description:** solution.md FTS Abstraction. SQLite impl = current `bm25`/`MATCH` reads + fts5 bootstrap + mirror INSERT/DELETE moved **verbatim** (behavior must not change). Postgres impl = `to_tsvector`/`websearch_to_tsquery` + `ts_rank`, real tables + GIN (schema owned by migrations; `EnsureAsync` no-op).
- **File:** `backend/src/QaAutomation.Infrastructure/TextSearch/SqliteFts5Search.cs`, `backend/src/QaAutomation.Infrastructure/TextSearch/PostgresFtsSearch.cs`
- **Acceptance Criteria:**
  - SQLite impl preserves today's BM25 ordering (keeps `KnowledgebaseServiceTests` green)
  - Postgres impl ranks via `ts_rank`, scoped by `project_id`/`scan_id`, within the same top-K budget
  - Compiles

### PHASE 3 — WIRE SERVICES + INITIALIZER

#### PGM-007: DatabaseInitializer provider branch
- **Status:** [ ]
- **ID:** PGM-007
- **Description:** solution.md DatabaseInitializer. Gate the adopt-`qa.db` path (`sqlite_master`, `INSERT OR IGNORE`) and `EnsureFtsTable` fts5 bootstrap to SQLite; Postgres skips adoption, `Migrate()`s fresh (schema incl. FTS tables), `EnsureAsync` no-op. `SeedProject` + `ReconcileOrphanedRuns` stay on both.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/DatabaseInitializer.cs`
- **Acceptance Criteria:**
  - SQLite path byte-identical to today (adopt + fts5 bootstrap + seed + reconcile)
  - Postgres path: no `sqlite_master`/`INSERT OR IGNORE`; migrate + seed + reconcile only
  - Compiles

#### PGM-008: KnowledgebaseService delegates FTS
- **Status:** [ ]
- **ID:** PGM-008
- **Description:** solution.md FTS Abstraction. Replace the raw `SqliteConnection`/`bm25`/`MATCH` read + `INSERT`/`DELETE` with `ITextSearchProvider` calls; drop the manual `PRAGMA busy_timeout`. Public `IKnowledgebaseService` surface unchanged.
- **File:** `backend/src/QaAutomation.Infrastructure/Knowledgebase/KnowledgebaseService.cs`
- **Acceptance Criteria:**
  - Upload → search → delete round-trip goes through `ITextSearchProvider`
  - No direct `Microsoft.Data.Sqlite` usage remains in the service; compiles

#### PGM-009: ExistingTestsIndexService delegates FTS
- **Status:** [ ]
- **ID:** PGM-009
- **Description:** solution.md FTS Abstraction. Replace the raw `test_case_index` `MATCH`/`ORDER BY rank` read + `INSERT`/`DELETE` with `ITextSearchProvider`. `FindDuplicatesAsync` (in-memory signature+Jaccard) unchanged.
- **File:** `backend/src/QaAutomation.Infrastructure/ExistingTests/ExistingTestsIndexService.cs`
- **Acceptance Criteria:**
  - Scan index build + `BuildTesterContextAsync` FTS read go through the provider
  - `FindDuplicatesAsync` unchanged; compiles

#### PGM-010: Register ITextSearchProvider by provider (DI)
- **Status:** [ ]
- **ID:** PGM-010
- **Description:** solution.md FTS Abstraction. Register `SqliteFts5Search` or `PostgresFtsSearch` per `DATABASE_PROVIDER` in the Infrastructure DI root.
- **File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
- **Acceptance Criteria:**
  - sqlite → `SqliteFts5Search`; postgres → `PostgresFtsSearch`; correct lifetime (scoped to the DbContext connection); compiles

### PHASE 4 — MIGRATIONS

#### PGM-006: Generate the Npgsql migration set
- **Status:** [ ]
- **ID:** PGM-006
- **Description:** solution.md Migrations. With `DATABASE_PROVIDER=postgres`, generate an Npgsql migration set in its own folder/namespace; include the real `doc_chunks`/`test_case_index` tables + `tsvector` columns + GIN indexes. Keep the SQLite set frozen. **Local only** (needs the .NET SDK). Fallback to a dedicated per-provider migrations project if the single-snapshot collision bites.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/Migrations/Npgsql/<ts>_InitialNpgsql.cs` (+ `.Designer.cs`, snapshot)
- **Acceptance Criteria:**
  - Command: `dotnet ef migrations add InitialNpgsql --project src/QaAutomation.Infrastructure --startup-project src/QaAutomation.Api --output-dir Data/Migrations/Npgsql`
  - DDL uses Postgres types + `GENERATED … AS IDENTITY`; FTS tables + GIN indexes present
  - Applies cleanly on a fresh Postgres; SQLite migrations untouched

### PHASE 5 — CONFIG + DOCS

#### PGM-011: .env keys
- **Status:** [ ]
- **ID:** PGM-011
- **Description:** solution.md Settings. Add `DATABASE_PROVIDER` (default `sqlite`) + a commented `POSTGRES_CONNECTION_STRING` to `.env.example` (and the user's `.env`), documented.
- **File:** `.env.example`
- **Acceptance Criteria:**
  - Both keys present + documented; default keeps SQLite; secret not committed

#### PGM-012: Local Docker setup guide
- **Status:** [x]
- **ID:** PGM-012
- **Description:** solution.md Verification + setup. Step-by-step: install Docker → run Postgres → link to the project (.env + migration) → switch SQLite↔Postgres → troubleshooting.
- **File:** `docs/setup/postgresql-docker.md`
- **Acceptance Criteria:**
  - Covers install through switching both ways; commands are copy-pasteable; SQLite-rollback documented

#### PGM-013: Azure deployment guide
- **Status:** [x]
- **ID:** PGM-013
- **Description:** solution.md Azure Deployment. Create Flexible Server → networking/SSL → App Service app settings (`DATABASE_PROVIDER`/connection string) → deploy + migrate → smoke test → cost/rollback notes.
- **File:** `docs/setup/postgresql-azure.md`
- **Acceptance Criteria:**
  - End-to-end Azure path; `SSL Mode=Require` noted; free-12-month vs paid tier noted; rollback to SQLite noted

### PHASE 6 — VERIFY + DOCUMENT

#### PGM-014: Local verification (SQLite green + Postgres round-trip)
- **Status:** [ ]
- **ID:** PGM-014
- **Description:** solution.md Verification. User-run: SQLite default `dotnet build`/`dotnet test` green (esp. `AdoGoldenTests`, `KnowledgebaseServiceTests`, `DatabaseInitializerTests`); then Postgres on Docker — migrate, KB upload→search ranked, dedup `ts_rank`; then switch back to SQLite.
- **File:** (verification only)
- **Acceptance Criteria:**
  - SQLite suite green with no env change; Postgres round-trip works locally; switch-back works

#### PGM-015: Feature doc + changelog
- **Status:** [ ]
- **ID:** PGM-015
- **Description:** Per the documentation-discipline rule, write `docs/features/postgresql-dual-provider.md` + a dated `docs/changelog/` entry **after** verification passes.
- **File:** `docs/features/postgresql-dual-provider.md`, `docs/changelog/<date>-postgresql-migration.md`
- **Acceptance Criteria:**
  - Feature page + changelog written after verification; matches shipped behavior
