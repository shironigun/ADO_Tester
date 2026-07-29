# PostgreSQL Migration — Technical Implementation Guide

| | |
| --- | --- |
| **Project Title** | PostgreSQL Migration — dual-provider (SQLite default), Azure-ready |
| **Document** | `blueprint.md` — implementation guide (companion to `solution.md`) |
| **Version** | 1.0 |
| **Date** | July 28, 2026 |
| **Prepared By** | Mahmood Ahmad (BA / QA) + Claude |

## Purpose

This is the file-by-file build map for the dual-provider migration designed in
[`solution.md`](./solution.md). It targets the .NET 8 / EF Core 8 backend in `backend/`. Task IDs
(`PGM-###`) map 1:1 to `prd.md`; live status is in `progress.md`. Follow `Ralph loop.md` every
iteration.

> **Guardrails:** SQLite stays the default and must remain green with no config change. Do not touch ADO
> write bodies, embedded prompts, or bug-pipeline behavior. Postgres is opt-in via `DATABASE_PROVIDER`.

## Technology Stack

| Component | Technology |
| --- | --- |
| Backend | .NET 8 / ASP.NET Core, EF Core 8 |
| Providers | `Microsoft.EntityFrameworkCore.Sqlite` 8.0.11 (default) **+** `Npgsql.EntityFrameworkCore.PostgreSQL` 8.x (new) |
| Full-text | SQLite FTS5 (bm25) **or** PostgreSQL `tsvector`/`ts_rank` + GIN, behind `ITextSearchProvider` |
| Dev DB | PostgreSQL in Docker (local) |
| Prod DB | Azure Database for PostgreSQL Flexible Server |
| Prod host | Azure App Service (.NET 8) |
| Config | `.env` (DotNetEnv) → `DATABASE_PROVIDER` + `POSTGRES_CONNECTION_STRING`, read at boot |

## Project Structure

`(new)` / `(modified)`; `[PGM-###]` maps to the backlog.

```
backend/src/QaAutomation.Infrastructure/
  QaAutomation.Infrastructure.csproj      (modified) [PGM-001] — + Npgsql.EntityFrameworkCore.PostgreSQL
  DependencyInjection.cs                  (modified) [PGM-002, PGM-010] — provider branch on AddDbContext; register ITextSearchProvider
  Data/
    QaDbContextFactory.cs                 (modified) [PGM-003] — design-time provider branch
    DatabaseInitializer.cs                (modified) [PGM-007] — gate SQLite-isms; PG plain-migrate
    Migrations/                           (unchanged) — SQLite set frozen
    Migrations/Npgsql/                    (new)      [PGM-006] — Npgsql migration set + snapshot
  TextSearch/
    SqliteFts5Search.cs                   (new)      [PGM-005] — current bm25/MATCH logic, moved
    PostgresFtsSearch.cs                  (new)      [PGM-005] — tsvector/ts_rank + GIN
  Knowledgebase/KnowledgebaseService.cs   (modified) [PGM-008] — delegate FTS to ITextSearchProvider
  ExistingTests/ExistingTestsIndexService.cs (modified) [PGM-009] — delegate FTS to ITextSearchProvider
backend/src/QaAutomation.Core/
  Abstractions/ITextSearchProvider.cs     (new)      [PGM-004] — FTS abstraction
  Configuration/DatabaseOptions.cs        (new)      [PGM-002] — Provider enum + connection string
backend/src/QaAutomation.Api/
  Program.cs                              (modified) [PGM-002] — read provider + cs; UTC switch; pass to AddInfrastructure
.env / .env.example                       (modified) [PGM-011] — DATABASE_PROVIDER, POSTGRES_CONNECTION_STRING
docs/setup/postgresql-docker.md           (new)      [PGM-012]
docs/setup/postgresql-azure.md            (new)      [PGM-013]
```

## Phase 1 — Provider plumbing (SQLite default unchanged)

**PGM-001 — Npgsql package.** Add `<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL"
Version="8.0.*" />` to the Infrastructure csproj (match EF 8.x).

**PGM-002 — boot-time selection.**
- `Core/Configuration/DatabaseOptions.cs`: `enum DatabaseProvider { Sqlite, Postgres }` +
  `DatabaseOptions { Provider; ConnectionString }`.
- `Api/Program.cs`: read `DATABASE_PROVIDER` (default `sqlite`) and, for postgres, `POSTGRES_CONNECTION_STRING`
  (fail fast if blank). Before building the host, if postgres: `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);`.
  Call `AddInfrastructure(config, provider, connectionString)` (generalized signature; the SQLite path
  keeps `$"Data Source={dbPath}"`).
- `Infrastructure/DependencyInjection.cs`: branch the `AddDbContext` lambda:
  ```
  if (provider == Postgres) o.UseNpgsql(cs, n => n.EnableRetryOnFailure().MigrationsAssembly(asm));
  else o.UseSqlite(cs).AddInterceptors(SqliteBusyTimeoutInterceptor.Instance);
  ```

**PGM-003 — design-time factory.** `QaDbContextFactory.CreateDbContext` reads `DATABASE_PROVIDER`; for
postgres `UseNpgsql(Env("POSTGRES_CONNECTION_STRING"), n => n.MigrationsAssembly(...))`, else the current
`UseSqlite("Data Source=qa_designtime.db")`. Lets `dotnet ef` target either provider.

## Phase 2 — FTS abstraction

**PGM-004 — interface.** `Core/Abstractions/ITextSearchProvider.cs` with: `EnsureAsync`,
`UpsertDocChunkAsync`/`DeleteDocChunksAsync`/`SearchDocChunksAsync(projectId, query, topK)`,
`UpsertTestCaseAsync`/`DeleteTestCasesForScanAsync`/`SearchTestCasesAsync(scanId, query, top)`.

**PGM-005 — implementations.**
- `TextSearch/SqliteFts5Search.cs`: move the existing raw-`SqliteConnection` bm25/`MATCH` reads
  (`KnowledgebaseService.SearchAsync` lines 166-202; `ExistingTestsIndexService.GetFtsEntryIdsAsync`
  252-280) and the mirror `INSERT`/`DELETE` verbatim — behavior must not change. `EnsureAsync` runs the
  two `CREATE VIRTUAL TABLE … fts5` statements (moved from `DatabaseInitializer`).
- `TextSearch/PostgresFtsSearch.cs`: `doc_chunks`/`test_case_index` are real tables (created by the
  Npgsql migration) with a `tsvector` column + GIN index. Writes set `to_tsvector('english', …)`; reads
  use `tsv @@ websearch_to_tsquery('english', $q) ORDER BY ts_rank(tsv, …) DESC LIMIT $k` scoped by
  `project_id`/`scan_id`. `EnsureAsync` = no-op (migrations own the schema).

## Phase 3 — Wire services + initializer

**PGM-008 / PGM-009 — delegate.** `KnowledgebaseService` and `ExistingTestsIndexService` keep their
`IKnowledgebaseService`/`IExistingTestsIndexService` surfaces but call `ITextSearchProvider` instead of
raw SQLite. `FindDuplicatesAsync` (in-memory signature+Jaccard) is unchanged. Drop the manual
`PRAGMA busy_timeout` in the KB service (it lives in the SQLite provider now).

**PGM-010 — DI registration.** In `DependencyInjection.cs`, register the FTS provider by
`DATABASE_PROVIDER`: `SqliteFts5Search` (scoped, needs the `QaDbContext` connection) or `PostgresFtsSearch`.

**PGM-007 — DatabaseInitializer branch.** Gate `ExistingSchemaNeedsAdoption`/`StampBaselineAsApplied`
(sqlite_master, `INSERT OR IGNORE`) and `EnsureFtsTable` to the SQLite branch. Postgres: skip adoption,
`Migrate()` (schema incl. FTS tables), `EnsureAsync` no-op. `SeedProject` + `ReconcileOrphanedRuns` run
on both.

## Phase 4 — Migrations

**PGM-006 — Npgsql migration set.** With `DATABASE_PROVIDER=postgres`:
`dotnet ef migrations add InitialNpgsql --project src/QaAutomation.Infrastructure --startup-project
src/QaAutomation.Api --output-dir Data/Migrations/Npgsql`. Verify the generated DDL uses Postgres types
(`integer`/`text`/`timestamp`/`boolean`, `GENERATED … AS IDENTITY`) and **includes** the real
`doc_chunks`/`test_case_index` tables + `tsvector` columns + GIN indexes. Keep the SQLite set frozen.
If the single-snapshot collision bites, fall back to a dedicated per-provider migrations project (noted
in the PRD).

## Phase 5 — Config + docs

**PGM-011 — .env.** Add `DATABASE_PROVIDER=sqlite` and a commented `POSTGRES_CONNECTION_STRING=` to
`.env.example` (+ the user's local `.env`), documented.

**PGM-012 / PGM-013 — setup guides.** `docs/setup/postgresql-docker.md` (install → run → link → switch)
and `docs/setup/postgresql-azure.md` (Flexible Server → App Service → migrate → smoke test). See the
files themselves.

## Build & verify order

Phase 1 first (SQLite must stay green with no env change — this is the safety net). Then FTS abstraction
(Phase 2) with the SQLite impl moved verbatim so BM25 tests stay green. Then wire services + initializer
(Phase 3). Then generate the Npgsql migration (Phase 4) and verify Postgres locally on Docker. Config +
docs (Phase 5) alongside. Backend/DB verification runs on the user's machine (no .NET SDK / Docker in the
container), so backend tasks are CODE-COMPLETE until the user verifies — see `progress.md`.
