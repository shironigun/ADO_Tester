# DB Schema Explorer — module plan

New ADO_Tester module: a product-agnostic **database mindmap + live entity explorer** for QA.
First target product: YourProduct (ADO repo **Your Repo** — microservice-per-folder monorepo,
`web-app` frontend, ~13 EF Core DbContexts). Built to work for *any* product later.

## Reference artifact analysis (design language we adopt)

Shared artifact: "CRM Platform — Database ERD · YourProduct · Entity Framework Core".
What it does, and what we take from it:

| Artifact behavior | Our adoption |
|---|---|
| One section per microservice/DbContext | Group tables by owning service in the mindmap |
| Solid nodes = physical tables, colored chip = SQL schema | Same node design |
| **Dashed nodes = tables owned by another service**, shown because this context references them; clicking opens the owner's schema | First-class "external reference" edges — this is where most cross-service QA bugs live |
| Keyless entities (stored-proc/view result shapes) listed at section bottom | Track them, render separately |
| Edge = FK, direction = data-reference flow | Same, **plus** AI-inferred edges (no FK constraint) rendered distinctly with confidence + `file:line` evidence |
| Hover isolates a table's connections; click opens full schema popup (every column, type, key, constraint) | Same interactions |

Key insight from the artifact: in a microservice CRM, many relationships are **implicit ID
references with no FK constraint** (dashed nodes). Deterministic parsing alone misses them —
that's the AI-inference part of the scan, and also the QA goldmine (orphaned rows, broken
cross-service IDs).

## Concepts

- **Product** — a configured scan target: ADO project + repo (+ branch), later N repos. YourProduct first.
- **Schema graph** — services → tables → columns; edges = relations with `kind` (fk | ef-nav | inferred),
  `confidence`, `evidence` (file:line). Built once by the scan, stored in our SQLite, rescan on demand.
- **Entity Registry** — the user-meaningful roots: Customer, Deal, Conversation, Broadcast, Task, Order,
  Payment, Part, Wholegood, Automation, Survey, CustomerGroup, Department… plus **dealer-scoped settings
  entities** (Oversending Control, Pipeline, Sync Settings) addressed by DealerId/DmsDealerId instead of
  their own ID. Each entity = root table + id column (+ optional scope column + display columns for
  human-readable labels). AI proposes the registry from the scan; user curates it in the UI.
- **Live trace** — given entity + ID (or dealer-scope), BFS the schema graph from the root table,
  generate **SELECT-only parameterized queries** against the product DB, return per-table row counts +
  rows. Powers Tab 1.

## UI: one page, two tabs

- **Tab 1 — Live Explorer**: pick entity → enter ID (or DealerId for settings entities) → radial/graph
  view where each related table is a node showing its **live row count** (empty = dimmed, data = lit).
  Click a node → actual rows in a drawer; join-path breadcrumb shows *how* it's linked
  (Customer.Id ← Deal.CrmCustomerId ← DealLine.DealId); "Copy SQL" per node so QAs can rerun in SSMS.
- **Tab 2 — Database Mindmap**: the full ERD (artifact-style): service sections, solid/dashed/keyless
  nodes, hover-isolate, click-for-schema popup, **focus mode** (pick any table → subgraph of everything
  reachable within N hops, grouped by service/module).

Graph rendering: **hand-rolled SVG** (focused star layout). `@xyflow/react` was tried and removed —
under React 19 + StrictMode (dev) it renders nodes but never measures node handles (`handleBounds`
undefined), so edges silently don't render; `useUpdateNodeInternals` didn't fix it. SVG is fewer moving
parts, works everywhere, and a focused (≤40-neighbor) view doesn't need pan/zoom/auto-layout.

**Note on "three-layer flow" (corrected 2026-07-06):** we do **NOT** render YourProduct's frontend UI
anywhere in this module. Our module's only visual language is the **ERD graph** (the shared artifact's
style). The frontend/backend *analysis* still runs — but its output is **metadata that annotates the
ERD**, never a rendered product screen. So the "flow" is expressed *inside* the graph: tables can be
filtered/colored by the backend endpoint and product module that read/write them (endpoint → table is
primary; frontend module → endpoint is a lower-priority annotation). Think "which endpoints/modules
touch this table," surfaced as node badges + filters — not a mockup of YourProduct screens.

## Scan pipeline (ADO repo, strictly read-only — we never write to the product repo)

Background orchestrator like the other modules (own DI scope, SSE progress, cancellation,
**checkpointed + resumable** — ClaudeCli usage limits make resumability mandatory; recommend a paid
API provider for the first full scan).

- **Phase A — inventory**: list repo tree via ADO REST, classify files (DbContext / entity / migration /
  SQL / controller-function / frontend api call / other). Persist file index + commitId (freshness marker).
- **Phase B — deterministic parse**: DbContexts, entity classes, `OnModelCreating` fluent config,
  migrations → tables, columns, keys, explicit FKs. High confidence, no LLM.
- **Phase C — AI inference**: batched LLM passes over raw SQL / stored procs / cross-service DTOs to
  infer implicit relations (dashed-node edges) with confidence + evidence; propose the Entity Registry.
- **Phase D — three-layer flow**: frontend api calls → endpoints → handlers → tables (read/write),
  so every table knows which screens/modules touch it.

## Read-only everything (non-negotiable — user rule #1)

- **We never edit the product codebase.** The scan uses ADO *read* REST APIs only; nothing is ever
  committed, pushed, or written back to the product repo.
- **We never run a write/update/delete/DDL query.** Every DB access is a **SELECT**, always. There is
  no code path in this module that builds a mutating statement.
- Read-only connection string(s) per product — microservices may have **separate databases**, so
  connection config is per-service with a default; the schema graph knows which service owns each table.
- Enforcement: SELECT-only allow-list validation (reject anything that isn't a single SELECT),
  parameterized values only, TOP-capped, command timeout, and (where possible) a read-only DB login.

## QA extras (the "fun way" to verify data)

- **Orphan check** (keep it simple + fun): for inferred (FK-less) relations, a single button that runs
  one templated `SELECT child LEFT JOIN parent WHERE parent IS NULL` and shows the count with a clear
  red/green result ("✅ 0 orphans" / "⚠️ 37 orphaned rows — click to see them"). No config, one click,
  instant answer. Nothing fancier than that for v1.
- **Trace snapshot & diff** (stretch): snapshot an entity trace before/after a QA action → see exactly
  which rows the action created/changed.
- Edge provenance tooltip everywhere (evidence + confidence) so QAs know when an edge is AI-inferred.

## Tasks (one at a time)

1. ~~Artifact analysis + this plan~~ ✅
2. ~~**Foundations**: EF entities + migration + `SchemaController` skeleton + page with 2 tabs + nav~~ ✅
   - Entities (`Core/Models/SchemaExplorer.cs`): `SchemaProduct`, `SchemaScan`, `SchemaService`,
     `SchemaTable` (columns as `ColumnsJson` blob of `SchemaColumnInfo`), `SchemaRelation`
     (kind fk/ef-nav/inferred + confidence + evidence), `SchemaEntity` (registry; product-scoped,
     root table by name so it survives rescans), `SchemaConnection` (per-product/service read-only conn).
   - Migration `20260706090102_AddSchemaExplorer` (7 tables, cascades from product→scan→graph). Auto-applies.
   - `Api/Controllers/SchemaController.cs` (`api/schema`): products CRUD + `products/{id}/scans` list.
   - Frontend: `api/schema.ts`, `pages/SchemaExplorerPage.tsx` (2 tabs Live Explorer / Database Mindmap,
     product selector + Add-product dialog), nav entry + route `/schema-explorer`.
   - Verified E2E: backend build clean, migration applied, product create/list `201`/`200`; page renders,
     tabs switch, no console errors. Seed product "YourProduct" (Your Project / Your Repo / master) created.
3. ~~**Scan Phase A** — ADO inventory + classification, SSE progress~~ ✅
   - `Infrastructure/SchemaExplorer/SchemaScanService.cs` (scoped): read-only, one recursive
     `ListRepoFilePathsAsync` call, path-based `Classify` into buckets (dbContexts / entityConfigs /
     migrations / sql / entities / controllers / frontendApi; rest counted as "other"), HEAD commit as
     freshness marker, inventory → `SchemaScan.StatsJson` (camelCase). Progress streams over the shared
     EventBus/SSE; narrative persisted to `EventLogJson`. **No cancellation/concurrency gate yet** (fast
     phase — add in Phase B when parsing gets long).
   - `SchemaController`: `POST products/{id}/scan` (202, background Task + own scope), `GET scans/{id}/stream`
     (SSE via `SseWriter`), `GET scans/{id}` (scan + parsed inventory).
   - Frontend: `ScanPanel`/`ScanConsole` in the Database Mindmap tab — Scan/Rescan button, live console
     (`useEventStream`), polls `getScan` until terminal, renders inventory chips. `api/schema.ts` extended.
   - **Verified E2E on Your Repo @ main** (commit ae771ae): 3431 files → 12 DbContexts (Leads, Customer,
     Dealer, Invoice, Messaging, Notification, Shared…), 850 entities, 418 SQL, 30 frontend-api, 4 controllers.
     Failure path also verified (wrong branch → Failed + persisted error). No console errors.
4. ~~**Scan Phase B** — deterministic EF parse → persisted schema graph~~ ✅
   - `EfSchemaParser.cs` (static, unit-tested): DbSet&lt;T&gt; (incl. namespace-qualified like `Data.Invoice`),
     `[Table(nameof|"lit", Schema=Schemas.X)]`, `[Key]`, `[ForeignKey(nameof(col))]` navs, `HasNoKey()`,
     composite `HasKey(x => new {...})`. Column-vs-relation decided at assembly against the known-table set.
   - `SchemaScanService` Phase B: runs after inventory in the same scan. Fetches DbContexts + entity files
     (basename match, parallel ×8), builds one `SchemaService` per DbContext, `SchemaTable` per DbSet
     (columns as `ColumnsJson`, keyless from HasNoKey, PK from [Key]/composite), `SchemaRelation` from nav
     props (collection→child holds FK; reference→this table holds FK). `GET scans/{id}/graph` serves it.
   - Frontend `GraphSummary` in the Mindmap tab: totals + per-service accordions of table chips (PK/keyless
     markers, column tooltip). Interim before the react-flow ERD (Task 6).
   - **Verified E2E on Your Repo**: 12 services, 266 tables, 145 relations. Customer → CustomerPhone/Email/
     Address/Contact/CategoryLink (FK via CrmCustomerId), Customer → Dealer (via DealerId); CustomerView keyless;
     Invoice (namespace-qualified DbSet) parsed. Parser self-check 2/2 green. No console errors.
   - Known gaps (→ Phase C / later): cross-service *implicit* id-column relations not yet inferred; duplicate
     service rows where a folder has 2 DbContexts (reflects repo); rescans accumulate old scan graph rows.
5. ~~**Scan Phase C** — inferred relations + Entity Registry proposal~~ ✅ (curation UI still TODO)
   - Implicit relations = **deterministic** pass (no LLM): a scalar `<Entity>Id` column that isn't a
     PK/explicit-FK → the keyed table named `<Entity>` (or one whose PK is that column), `kind=Inferred`
     + confidence (0.85 exact-PK / 0.6 name) + evidence. Same-named tables (cross-service DbSet reuse)
     resolve to ONE **canonical** table (prefer the owning service) so `DealerId` links to Dealer once,
     not to all 5 copies.
   - Entity Registry = **one LLM call** (`SchemaEntityAgent`, single-shot JSON): proposes the lookup
     entities (Customer, Deal, Conversation, Dealer, Invoice…) with root table, id column, dealer
     `scopeColumn`, display columns, confidence. Persisted to `SchemaEntity` (product-scoped,
     `source="ai"`; user-curated rows preserved on rescan). Non-fatal if the LLM is unavailable.
   - API `GET products/{id}/entities`; relation DTO now carries confidence+evidence. Frontend: Live
     Explorer tab renders the entity registry cards (root/id/scope/label chips + confidence).
   - **Verified E2E on Your Repo**: 278 inferred + 145 explicit = 423 relations; **20 AI entities**
     (Customer id=CrmCustomerId scope=DealerId; Dealer; Conversation; Deal; Invoice; Pipeline scope=
     DmsDealerIdCrm; …). CustomerPhone→Dealer/Customer inferred correctly. No console errors.
   - TODO (later): entity-registry curation UI (edit/enable/add); merge duplicate same-named table nodes
     from cross-service DbSet reuse (currently 5 Dealer nodes — cosmetic, fix at render/Task 6).
6. ~~**Tab 2 Mindmap** — interactive ERD, focus mode, schema popup~~ ✅
   - `client/src/components/SchemaGraphView.tsx` — **SVG** focused graph (react-flow removed, see above).
     Centers on a table (default Customer), shows it + up to 40 linked tables on an ellipse; service-colored
     nodes with PK (bold/3px border) / keyless (dashed) markers; solid = declared FK/nav, dashed = inferred,
     arrowheads show direction, `<title>` hover shows the via-column + kind. Click a node (or a chip in the
     browse list) to re-center; Autocomplete "Focus table" search over all tables; a columns panel for the
     focused table (PK/FK chips + type/nullable/ref tooltips); "+N more" when a hub exceeds the cap; a
     collapsible "Browse all tables by service".
   - Verified E2E on Your Repo: Customer centered → 40 neighbors, 40 edges; click Dealer re-centers
     (columns panel + graph update); solid/dashed split + arrowheads render. No live console errors.
7. ~~**Live query engine + Tab 1 Live Explorer**~~ ✅ (tasks 7+8 merged)
   - `Microsoft.Data.SqlClient`; `SchemaTraceService` (scoped, SELECT-only): resolves the product's default
     read-only connection + latest scan + entity, runs a schema-qualified `SELECT TOP 50 * FROM [schema].[table]
     WHERE [col] = @p` on the root then one-hop per linked table (child = FK-holds-id; parent = look up by the
     root's FK value). Identifiers are validated against the scan + bracket-quoted, id is always a parameter,
     `EnsureSelectOnly` guard rejects non-SELECT/multi-statement, per-table try/catch so a cross-DB miss degrades.
     Unit-tested (quote/qualify/literal/guard — 7 tests). Also `TestConnectionAsync` (`SELECT 1`).
   - API: `GET/PUT/DELETE products/{id}/connection` (masked, password never returned), `POST .../connection/test`,
     `POST .../trace {entityKey,id}` → root + linked traces (each: SSMS-ready SQL + row count + sample rows).
   - Frontend `components/LiveExplorer.tsx`: connection status + configure dialog (Test / Save / Remove, read-only
     warning), entity picker + id → Trace, root card + per-linked-table accordions (direction/service/row-count
     chips, copyable SQL, rows table), "not found" / "+N more" / per-table error states.
   - **Verified E2E except a live query** (I did NOT connect to the real CRM DB — needs the user's read-only
     string): no-connection→400, bogus test→graceful SQL error, PUT masks the password, trace reaches the DB
     step and fails cleanly on an unreachable host, DELETE clears it, UI renders all states, no console errors.
     Fixed a real bug: AI sometimes stores `RootTableName` service-qualified (`customer.Customer`) — trace now
     matches on the last segment and the store normalizes to bare.
   - **To do a live trace:** open Live Explorer → Manage environments → add a **read-only** SQL Server string
     → pick the environment → Trace an entity id.
   - **Multi-environment (added on request):** a product has N named environment connections (Dev/QA/Staging/
     Production…). `SchemaConnection.Environment` column (migration `AddSchemaConnectionEnvironment`); API is now
     `GET/PUT products/{id}/connections`, `DELETE products/{id}/connections/{env}`, `POST .../connections/test`,
     and `trace` takes an `environment`. The chosen environment decides which DB is queried.
   - **Config relocated to Settings (added on request):** DB connectivity moved out of the Schema Explorer page
     into **Settings → Database** (`client/src/components/DatabaseSettings.tsx`), entered as discrete
     **Server / User / Password / Database** fields per environment (migration `AddSchemaConnectionFields` added
     Server/DbUser/DbDatabase; password lives only in the built connection string; blank on edit = keep). The
     **product is single, sourced from Settings → Azure DevOps** via `GET /api/schema/product` (org/project/repo/
     branch) — the Schema Explorer page dropped the product selector + Add-product form; Live Explorer keeps just
     the environment picker + a "Manage in Settings → Database" link. Verified E2E (field save, edit-keeps-password,
     list never returns the password, env-targeted trace, single product synced from ADO settings).
9. **Phase D flow overlay** — endpoint → table (and module → endpoint) mapping surfaced as ERD node
   badges + filters. **No YourProduct screens are rendered** — annotation on the graph only.
10. **QA extras + hardening** — orphan checks, incremental rescan, trace diff (stretch).
