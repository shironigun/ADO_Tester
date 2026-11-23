# Running PostgreSQL in Docker (local development)

PostgreSQL is required to run ADO_Tester. This guide takes you from **nothing installed** to a local
PostgreSQL-backed development environment. For Azure deployment see
[`postgresql-azure.md`](./postgresql-azure.md).

The examples use PostgreSQL 16 with database/user/password `qa` on localhost port 5432.

---

## Step 1 — Install Docker

**Windows / macOS — Docker Desktop**
1. Download Docker Desktop: https://www.docker.com/products/docker-desktop/
2. Run the installer, accept the defaults, and restart if prompted.
   - Windows: keep the **WSL 2** backend option enabled (the default).
3. Launch Docker Desktop and wait for the whale icon to say "Docker Desktop is running."

**Linux — Docker Engine**
```bash
# Ubuntu/Debian (see docs.docker.com/engine/install for other distros)
sudo apt-get update
sudo apt-get install -y docker.io
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"   # then log out/in so you can run docker without sudo
```

**Verify (all platforms):**
```bash
docker --version
# e.g. Docker version 27.x.x
```

---

## Step 2 — Start a PostgreSQL container

Two options — pick one. Both create a database named `qa` with user `qa` / password `qa` on the standard
port 5432, and persist data in a named volume so it survives restarts.

**Option A — one-liner:**
```bash
docker run --name adotester-postgres \
  -e POSTGRES_USER=qa \
  -e POSTGRES_PASSWORD=qa \
  -e POSTGRES_DB=qa \
  -p 5432:5432 \
  -v adotester_pgdata:/var/lib/postgresql/data \
  -d postgres:16
```

**Option B — `docker compose`** (a ready `docker-compose.yml` is committed at the repo root):
```bash
docker compose up -d      # start; docker compose down (keep data) / down -v (wipe)
```

**Verify it's up:**
```bash
docker ps                       # adotester-postgres should be "Up"
docker exec -it adotester-postgres psql -U qa -d qa -c "select version();"
```

---

## Step 3 — Point ADO_Tester at Postgres (.env)

Edit the repo-root `.env` (create it from `.env.example` if needed) and add:

```dotenv
POSTGRES_CONNECTION_STRING=Host=localhost;Port=5432;Database=qa;Username=qa;Password=qa
```

- `POSTGRES_CONNECTION_STRING` is required; `DATABASE_URL` is accepted as an alternative.
- **No SSL locally** — Docker Postgres doesn't need it. (Azure/Neon/Supabase add `SSL Mode=Require;` —
  see the Azure guide.)

---

## Step 4 — Create the PostgreSQL schema (EF migration)

Npgsql migrations live in **`src/QaAutomation.Migrations.Npgsql`** and are always wired through
`MigrationsAssembly`. The migration set is already committed. To add a future migration, run from
`backend/` (using the `dotnet-build-environment` note in `CLAUDE.md` if `dotnet` isn't on PATH):

```bash
dotnet ef migrations add DescribeTheChange \
  --project src/QaAutomation.Migrations.Npgsql \
  --startup-project src/QaAutomation.Api
```

You normally **don't** need `dotnet ef database update`: `DatabaseInitializer` applies migrations on
startup and creates the `tsvector`/GIN full-text tables. Run `database update` explicitly only if you
prefer to migrate before boot.

---

## Step 5 — Run the backend on Postgres

```bash
# from backend/
dotnet run --project src/QaAutomation.Api
```

On first start the backend creates the full schema **plus** the PostgreSQL full-text tables
(`doc_chunks`, `test_case_index` with `tsvector` columns + GIN indexes). Then start the frontend as usual
(`npm run dev` in `client/`) and open http://localhost:5173.

**Smoke test:**
- Upload a Knowledgebase doc, then run an Author Tests or Find Bugs flow → KB retrieval should return
  ranked hits (now via Postgres `ts_rank`).
- Run an Author Tests dedup scan against a reference plan → the existing-test index builds and dedup
  works.

---

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `port 5432 ... already in use` | Another Postgres is running. Stop it, or map a different host port (`-p 5433:5432`) and set `Port=5433` in the connection string. |
| `password authentication failed` | The container was first created with different creds. Recreate it: `docker rm -f adotester-postgres` + delete the volume `docker volume rm adotester_pgdata`, then Step 2. |
| Backend logs `Npgsql ... SSL` errors | Don't set `SSL Mode=Require` for local Docker — omit it. |
| `relation "doc_chunks" does not exist` | Startup initialization did not finish. Check backend logs, confirm the connection string has schema-creation rights, and restart. |
| Want a clean DB | `docker rm -f adotester-postgres && docker volume rm adotester_pgdata`, then Step 2. |

## Handy commands
```bash
docker stop adotester-postgres         # stop (data kept)
docker start adotester-postgres        # start again
docker logs -f adotester-postgres      # watch logs
docker exec -it adotester-postgres psql -U qa -d qa   # psql shell
docker rm -f adotester-postgres        # remove container (volume kept unless you rm it)
```
