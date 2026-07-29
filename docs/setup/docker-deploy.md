# Deploying ADO_Tester with Docker (backend + frontend + PostgreSQL)

Runs the whole app as three containers on one machine:

| Service    | Image (built from)                                       | Container port | Host port | Role                                             |
| ---------- | -------------------------------------------------------- | -------------- | --------- | ------------------------------------------------ |
| `frontend` | `client/Dockerfile` (nginx)                              | 80             | **8080**  | Serves the SPA, reverse-proxies `/api` → backend |
| `backend`  | `backend/Dockerfile` (.NET 8 + Claude CLI + Copilot CLI) | 8080           | 5250      | The API / agent pipelines                        |
| `postgres` | `postgres:16`                                            | 5432           | 5432      | Database                                         |

The browser only talks to the **frontend** origin, so requests are same-origin (no CORS). You open
**http://localhost:8080**.

---

## 1. Prerequisites

- Docker Desktop (or Docker Engine + Compose v2). Verify: `docker --version` and `docker compose version`.
- A repo-root **`.env`** (copy from `.env.example`). At minimum set `ADOACCESS_PAT` and your
  `ADO_ORGANIZATION` / `ADO_PROJECT`. You do **not** need to set `DATABASE_PROVIDER` or the Postgres
  connection string — `docker-compose.yml` forces those at the container Postgres.

## 2. Build & start

```powershell
docker compose up -d --build
```

First build compiles the .NET API and the Vite bundle and installs the Claude CLI and the GitHub
Copilot CLI into the backend image, so it takes a few minutes. Postgres comes up first (compose waits
for its healthcheck), then the backend migrates/creates the schema on start, then the frontend.

Check everything is healthy:

```powershell
docker compose ps
docker compose logs -f backend      # Ctrl+C to stop tailing
```

Open **http://localhost:8080**.

## 3. Log in to Claude (one time) — no API key needed

The default LLM provider is the **Claude CLI**, which uses your **Claude Pro/Max subscription** login
(not an API key). The CLI is installed in the backend image; you authenticate once and the credentials
persist on the `adotester_claude` volume across restarts.

```powershell
docker compose exec backend claude
```

- Follow the prompt and choose **"Log in with your Claude account"** (the subscription option).
- The CLI prints an authorization **URL**. Open it in a **new browser tab**, approve, and copy the code
  it gives you back into the terminal (the container is headless, so it can't pop the browser itself —
  you open the tab and paste the code manually).
- Once it says you're logged in, press `Ctrl+C` to leave the CLI. The app now uses that login for every
  generation.

Verify from the app: **Diagnostics** page → the Claude CLI provider should show as available. Or run an
**Author Tests** / **Find Bugs** flow.

> **Heads-up (known limit):** the subscription CLI enforces usage limits and can fail on very large
> single generations. For big tickets, switch the provider in the AppBar to a paid API provider
> (Anthropic / Azure OpenAI / an OpenAI-compatible gateway) — set that provider's key in `.env` and
> restart the backend (`docker compose restart backend`).

## 4. Authenticate GitHub Copilot CLI (optional)

The **GitHub Copilot CLI** is also installed in the backend image. You only need this if you want to
use the **CopilotCli** provider (switchable in the AppBar).

The cleanest authentication method for Docker is a **GitHub PAT** set as `COPILOT_GITHUB_TOKEN`
in your `.env` file. The CLI picks it up automatically; no interactive step needed.

1. Generate a classic GitHub PAT at <https://github.com/settings/tokens> (no specific scopes
   required — Copilot access is account-level).
2. Add it to your `.env`:

   ```
   COPILOT_GITHUB_TOKEN=ghp_your_token_here
   ```

3. Restart the backend to pick it up:

   ```powershell
   docker compose restart backend
   ```

4. Switch the provider to **CopilotCli** in the app AppBar and run a generation to verify.

> **Alternative:** exec into the container (`docker compose exec -it backend sh`) and run
> `copilot`, then use the `/login` command for the interactive device-flow.

## 5. Everyday commands

```powershell
docker compose logs -f backend            # tail backend logs
docker compose restart backend            # pick up .env / provider changes
docker compose up -d --build              # rebuild after code changes
docker compose down                       # stop; DB + KB/reports + Claude login kept
docker compose down -v                    # stop AND wipe all volumes (fresh start)
```

## 6. Data & persistence

Three named volumes survive `docker compose down` (removed only by `down -v`):

- `adotester_pgdata` — PostgreSQL data.
- `adotester_data` — mounted at `/app/data`: KB uploads and generated report HTML (SQLite too, though
  unused when on Postgres).
- `adotester_claude` — mounted at `/home/appuser`: Claude CLI **and** GitHub Copilot CLI subscription logins.

## 7. Configuration reference

`docker-compose.yml` loads the repo-root `.env` into the backend container (`env_file`) and then
overrides the database settings so the app always targets the bundled Postgres:

```yaml
DATABASE_PROVIDER: postgres
POSTGRES_CONNECTION_STRING: Host=postgres;Port=5432;Database=qa;Username=qa;Password=qa
```

Override the DB name/user/password by exporting `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD`
before `docker compose up` (they flow into both the Postgres container and the connection string).

## 8. Troubleshooting

| Symptom                                                  | Fix                                                                                                                                                                                       |
| -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `env file .env not found`                                | Create it: `Copy-Item .env.example .env`, then fill in `ADOACCESS_PAT`.                                                                                                                   |
| Frontend loads but every call 502s                       | Backend isn't up yet or crashed — `docker compose logs backend`. It waits for Postgres to be healthy first.                                                                               |
| LLM calls fail with "Claude CLI not found" / auth error  | Run the login in step 3; confirm `docker compose exec backend claude --version` works and the `adotester_claude` volume is mounted.                                                       |
| LLM calls fail with "Copilot CLI not found" / auth error | Run the login in step 4; confirm `docker compose exec backend copilot --version` works.                                                                                                   |
| LLM calls fail with "Invalid API key"                    | A real/placeholder `ANTHROPIC_API_KEY` in `.env` shouldn't matter (the CLI child strips it), but if you switched the provider to Anthropic, set a valid key or switch back to Claude CLI. |
| Live agent console shows nothing (SSE)                   | Confirm you're hitting the app via the **frontend** (`:8080`), not the backend directly — nginx is configured to stream `/api` without buffering.                                         |
| Port 8080/5250/5432 already in use                       | Change the left-hand host port in `docker-compose.yml` (e.g. `"8090:80"`).                                                                                                                |

For a Postgres-only local setup (running the backend from source, not in Docker) see
[`postgresql-docker.md`](./postgresql-docker.md).
