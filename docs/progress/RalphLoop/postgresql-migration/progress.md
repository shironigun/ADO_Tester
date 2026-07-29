# Progress: PostgreSQL Migration Implementation

## Overview
- **Ruleset:** `Ralph loop.md` (follow it every iteration)
- **Creation sequence:** solution.md → blueprint.md → prd.md → progress.md
- **PRD:** prd.md · **Design:** solution.md · **Guide:** blueprint.md
- **Branch:** `claude/chat-session-flzhjh`
- **Started:** 2026-07-28
- **Status:** PGM-006 COMPLETE (Initial Npgsql migration generated 2026-07-29; solution updated; 156/156 tests pass).
  **PGM-014 (Docker verify) and PGM-015 (feature doc) still pending** — Docker required to run Postgres round-trip.

## Status legend
- **VERIFIED** — implemented + verified green in this environment (or trivially-verifiable docs).
- **CODE-COMPLETE** — implemented + pushed; backend build/test/migration/Docker verification pending on the user's local machine.
- **NOT STARTED** — not begun.

---

## Task Progress

| Task ID | Task Name                                         | Status        | Started    | Completed  | Notes                                                                                                                                           |
| ------- | ------------------------------------------------- | ------------- | ---------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| PGM-001 | Add Npgsql EF Core provider package               | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11 added; user restores/builds locally                                                                |
| PGM-002 | Boot-time provider selection + DatabaseOptions    | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | DatabaseOptions enum; Program.cs reads DATABASE_PROVIDER (+ legacy-timestamp switch); AddInfrastructure branch (SQLite overload kept for tests) |
| PGM-003 | Design-time factory provider branch               | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | QaDbContextFactory branches on DATABASE_PROVIDER                                                                                                |
| PGM-004 | ITextSearchProvider abstraction                   | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | Interface + DocChunkHit in Infrastructure/TextSearch (see layering note in log)                                                                 |
| PGM-005 | SqliteFts5Search + PostgresFtsSearch              | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | SQLite = bm25/MATCH moved verbatim; Postgres = tsvector/GIN/ts_rank; FtsMatch shared tokenizer                                                  |
| PGM-006 | Generate the Npgsql migration                     | VERIFIED      | 2026-07-29 | 2026-07-29 | `dotnet sln add` + `dotnet ef migrations add Initial`; 156/156 tests pass                                                                       |
| PGM-007 | DatabaseInitializer provider branch               | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | adopt-qa.db gated to IsSqlite(); EnsureFtsTable → _textSearch.EnsureSchema                                                                      |
| PGM-008 | KnowledgebaseService delegates FTS                | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | upload/delete/search via provider; MatchExpression kept (delegates to FtsMatch)                                                                 |
| PGM-009 | ExistingTestsIndexService delegates FTS           | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | write + FTS read via provider; also removed a latent non-partial-class regex issue                                                              |
| PGM-010 | Register ITextSearchProvider by provider (DI)     | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | Sqlite/Postgres singleton by DATABASE_PROVIDER; 2 test fixtures updated                                                                         |
| PGM-011 | .env keys                                         | VERIFIED      | 2026-07-28 | 2026-07-28 | DATABASE_PROVIDER + POSTGRES_CONNECTION_STRING documented in .env.example (config text)                                                         |
| PGM-012 | Local Docker setup guide                          | VERIFIED      | 2026-07-28 | 2026-07-28 | docs/setup/postgresql-docker.md written                                                                                                         |
| PGM-013 | Azure deployment guide                            | VERIFIED      | 2026-07-28 | 2026-07-28 | docs/setup/postgresql-azure.md written                                                                                                          |
| PGM-014 | Local verification (SQLite green + PG round-trip) | NOT STARTED   |            |            | User-run                                                                                                                                        |
| PGM-015 | Feature doc + changelog                           | NOT STARTED   |            |            | After verification                                                                                                                              |
| PGM-016 | Scaffold the Npgsql migrations assembly           | CODE-COMPLETE | 2026-07-28 | 2026-07-28 | New QaAutomation.Migrations.Npgsql project + MigrationsAssembly wiring + Api ref + docker-compose.yml                                           |

---

## Summary
- **Total Tasks:** 16
- **VERIFIED:** 3 (two setup guides + .env keys)
- **CODE-COMPLETE:** 10 (PGM-001/002/003 plumbing · 004/005/007/008/009/010 FTS · 016 migrations-assembly scaffold)
- **NOT STARTED:** 3 (PGM-006 Npgsql migration · PGM-014 verify · PGM-015 feature doc)

---

## Log

### 2026-07-28 (Migrations-assembly scaffold — PGM-016; raw-SQL audit clean)
- **Audit:** swept all of `backend/src` for raw SQL — the ONLY provider-specific SQL is in the files already
  handled (TextSearch/*, the `IsSqlite()`-gated `DatabaseInitializer` helpers). Every orchestrator/service is
  pure LINQ/EF; `QaDbContext` has no `HasColumnType`/`HasDefaultValueSql`. So no other service code needed
  changing — the Phase 2/3 refactor covered the full raw-SQL surface.
- **PGM-016:** added `QaAutomation.Migrations.Npgsql` (dedicated migrations assembly — EF migrations can't
  share the SQLite model snapshot): csproj (references Infrastructure; Npgsql flows transitively) +
  `AssemblyMarker`; wired `MigrationsAssembly("QaAutomation.Migrations.Npgsql")` in the DI Npgsql branch AND
  the design-time factory; Api now references the project so its DLL is loadable for `Migrate()`. SQLite keeps
  the default migrations assembly (unchanged). Added a root `docker-compose.yml`; updated the Docker guide's
  migration step to `dotnet sln add` + `dotnet ef migrations add` (no `--output-dir`).
- **Correction:** on Postgres the `doc_chunks`/`test_case_index` tables are created by
  `PostgresFtsSearch.EnsureSchema` at startup (outside EF), so PGM-006's migration covers only the EF
  relational tables — PGM-006 updated to match.
- **Net:** all compiler-independent work is done. Remaining = local only: `dotnet sln add` +
  `dotnet ef migrations add` (PGM-006), then the B/C/D checklist below (build, module run, Azure).

### 2026-07-28 (Phase 2 + 3 — FTS abstraction + wiring)
- **PGM-004/005** — new `Infrastructure/TextSearch/`: `ITextSearchProvider` (+ `DocChunkHit`), `SqliteFts5Search`
  (doc_chunks/test_case_index fts5 + bm25/rank reads moved VERBATIM), `PostgresFtsSearch` (real tables with a
  generated `tsvector` column + GIN index, `websearch_to_tsquery`/`ts_rank`), and `FtsMatch` (shared SQLite
  match-expression tokenizer).
  - **Layering note (deviation from plan):** the interface lives in **Infrastructure**, not Core — it must take
    `QaDbContext`, and Core has no implementation dependencies. This is an internal data-layer seam, not a Core contract.
  - **FTS tables are outside EF on BOTH providers:** created idempotently by `EnsureSchema` (SQLite fts5 virtual
    tables; Postgres real tables + GIN). So the Npgsql migration (PGM-006) only needs the relational tables.
- **PGM-008/009** — `KnowledgebaseService` and `ExistingTestsIndexService` now delegate all FTS write/read to the
  provider (no raw `SqliteConnection`). `KnowledgebaseService.MatchExpression` kept (delegates to `FtsMatch`) so the
  BM25 unit tests are unchanged; `FindDuplicatesAsync`’s in-memory Jaccard is untouched.
- **PGM-007** — `DatabaseInitializer` branches: the adopt-existing-`qa.db` path (sqlite_master / INSERT OR IGNORE)
  is gated to `_db.Database.IsSqlite()`; `EnsureFtsTable()` replaced by `_textSearch.EnsureSchema(_db)`.
- **PGM-010** — DI registers `SqliteFts5Search` or `PostgresFtsSearch` by `DATABASE_PROVIDER`.
- **Tests updated** (ruleset — keep green): `KnowledgebaseServiceTests` + `DatabaseInitializerTests` now pass a
  `new SqliteFts5Search()` into the `DatabaseInitializer`/`KnowledgebaseService` constructors. The `MatchExpression`
  and BM25-ordering assertions are unchanged.
- **Bonus fix:** removing `ExistingTestsIndexService`’s `[GeneratedRegex]` `TokenRegex` cleared a latent issue — it
  was a `partial` method inside a **non-`partial`** class (a compile error the un-built author-tests code carried).
- **CODE-COMPLETE, pending local verify:** no .NET SDK/Docker in the container. The user builds + tests locally.
- **Next:** PGM-006 (Npgsql migration, local) → PGM-014 verify (SQLite green + Postgres round-trip on Docker) →
  Azure deploy → PGM-015 feature doc.

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

## Remaining — to run locally (nothing here can run in the container: no .NET SDK / Docker)

Do these on your machine, in order. Backend commands use your usual `dotnet` invocation (see the
`dotnet-build-environment` note in `CLAUDE.md` if `dotnet` isn't on PATH). Guides:
`docs/setup/postgresql-docker.md` (local) and `docs/setup/postgresql-azure.md` (deploy).

### A. Confirm nothing regressed on SQLite (the default)
- [ ] **Build the project** — from `backend/`: `dotnet build` → 0 errors (this also restores the new Npgsql package).
- [ ] **Run the tests** — `dotnet test` → all green. Watch especially:
  - [ ] `dotnet test --filter "FullyQualifiedName~AdoGoldenTests"` (byte-parity unchanged)
  - [ ] `dotnet test --filter "FullyQualifiedName~KnowledgebaseServiceTests"` (BM25 round-trip + MatchExpression)
  - [ ] `dotnet test --filter "FullyQualifiedName~DatabaseInitializerTests"` (adopt + fresh + fts5 bootstrap)
- [ ] **Run the app on SQLite** (no env change): `dotnet run --project src/QaAutomation.Api` + `npm run dev` in `client/` → open http://localhost:5173 and confirm it behaves exactly as before.

### B. Bring up PostgreSQL locally (Docker) — PGM-006 + PGM-014
- [ ] **Start Postgres in Docker**: `docker compose up -d` (compose file committed at repo root).
- [ ] **Set `.env`**: `DATABASE_PROVIDER=postgres` + `POSTGRES_CONNECTION_STRING=Host=localhost;Port=5432;Database=qa;Username=qa;Password=qa`.
- [ ] **PGM-006 — generate the Npgsql migration** (assembly already scaffolded): `dotnet sln add src/QaAutomation.Migrations.Npgsql` then `DATABASE_PROVIDER=postgres dotnet ef migrations add InitialNpgsql --project src/QaAutomation.Migrations.Npgsql --startup-project src/QaAutomation.Api`. Confirm Postgres DDL (`integer`/`text`/`timestamp`, `GENERATED … AS IDENTITY`) and that the SQLite set is untouched.
- [ ] **Start the backend on Postgres** → confirm the schema + the `doc_chunks`/`test_case_index` tsvector/GIN tables are created on boot (no errors).

### C. Do a real run for a module / ticket (functional check on Postgres)
- [ ] **Knowledgebase**: upload a `.md`, then search → returns ranked hits (now Postgres `ts_rank`).
- [ ] **Author Tests**: start a run for a work item (e.g. standing ticket **171740**) → Fetch → Retrieve KB → Discover → Analyst → Tester; confirm KB retrieval steers it and drafts generate.
- [ ] **Existing-test dedup**: point a run at a reference Test Plan → the scan indexes and the dedup card shows skipped/flagged.
- [ ] **Find Bugs** and/or **QA Loop module scan**: run one to exercise the pipelines end-to-end against Postgres.
- [ ] **Switch back to SQLite** (`DATABASE_PROVIDER=sqlite`, restart) → app still runs on the original `qa.db`.

### D. Deploy to Azure (go-live) — per `docs/setup/postgresql-azure.md`
- [ ] Create an Azure Database for PostgreSQL **Flexible Server**; get the connection string (`SSL Mode=Require`).
- [ ] Create the **App Service** (.NET 8); set `DATABASE_PROVIDER=postgres` + `POSTGRES_CONNECTION_STRING` (+ the other secrets) as app settings.
- [ ] Deploy the backend; confirm migrations apply on boot; smoke-test the QA flows the team will use.

### E. Close out
- [ ] **PGM-015** — once B–D pass, write `docs/features/postgresql-dual-provider.md` + a dated `docs/changelog/` entry, and flip PGM-006/014/015 to done here.
