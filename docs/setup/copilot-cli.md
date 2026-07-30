# Using the GitHub Copilot CLI provider

This guide sets up the **Copilot CLI** LLM provider for ADO_Tester — the agents run through GitHub's
local `copilot` CLI against your Copilot subscription, no per‑token API billing. It works for both the
local dev run and the Docker deployment.

> **The one thing to know up front:** the backend calls the CLI in **headless/print mode**
> (`copilot -p …`). That mode does **not** use the interactive `copilot` chat login (`/login`) — it
> **requires a GitHub token**. Your interactive `copilot` chat working is *not* the same as `copilot -p`
> working. This is a GitHub Copilot CLI limitation, not an ADO_Tester one.

---

## Step 1 — Install the Copilot CLI

```powershell
npm install -g @github/copilot
copilot --version        # confirm it's on PATH
```

The backend auto‑detects `copilot` (also `github-copilot-cli` / `gh-copilot`) on PATH and in the common
npm/pnpm/Volta global‑bin locations, so you normally don't need to configure a path. To pin one, set
`COPILOT_CLI_PATH` in `.env` (or `copilot_cli.path` in Settings → LLM Providers).

## Step 2 — Provide a token (required for headless mode)

Pick **one** of these. All three env var names are read by the CLI and forwarded by the backend to the
`copilot` child process:

### Option A — token in `.env` (simplest, deterministic)

Create a GitHub Personal Access Token that has **Copilot access**, then add it to the repo‑root `.env`:

```dotenv
COPILOT_GITHUB_TOKEN=<your-token>
```

- A **fine‑grained** PAT (`github_pat_…`) is recommended. Create one at
  GitHub → Settings → Developer settings → **Fine‑grained tokens**.
- `GH_TOKEN` or `GITHUB_TOKEN` work too if you already set those.

### Option B — GitHub CLI login (no manual PAT)

`copilot -p` can also read the GitHub CLI's token automatically:

```powershell
winget install --id GitHub.cli -e         # if gh isn't installed
gh auth login                             # GitHub.com → HTTPS → Login with a web browser
```

Then make sure the backend process has `gh` **on its PATH** (start it from a terminal opened *after*
installing `gh`). No `.env` change is needed in this mode.

## Step 3 — Restart the backend

`.env` is read once at startup, so restart after changing it:

- **Local dev:** stop the backend terminal (Ctrl+C) and re‑run
  `dotnet run --project src\QaAutomation.Api` from `backend/`.
  For Option B, open a **new** terminal first so `gh` is on PATH.
- **Docker:** `docker compose up -d backend` (compose injects `.env` via `env_file`).

## Step 4 — Select the provider and test

In the app's AppBar, switch the LLM provider to **Copilot CLI**, then run any flow (Author Tests /
Find Bugs). Diagnostics (Settings → Diagnostics) shows the CLI version + availability.

**Quick command‑line sanity check** (mirrors exactly how the backend invokes it):

```powershell
copilot -p "Reply with exactly one word: pong" --available-tools --no-ask-user --no-color
```

A successful run prints `pong` (plus a usage summary on stderr — `AI Credits …`, `Tokens …` — which is
normal, not an error). "No authentication information found" means the token isn't set/valid for this
process (see below).

---

## How the backend uses the CLI (for reference)

- Spawns `copilot -p @<promptfile> --available-tools --no-ask-user --no-color [--model …] [--context …] [--effort …]`.
  `--available-tools` (no value) disables all tools so it's a pure LLM completion (prevents the CLI from
  entering agent mode and scanning the filesystem); `--no-ask-user` stops it stalling for input.
- The prompt is passed as a temp file via the CLI's `@path` convention (stdin `-p -` isn't supported).
- Runs in a neutral working directory so it can't auto‑discover this repo's own config/memory.
- Forwards `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN` to the child; strips only `COPILOT_API_KEY`.
- Exit code 1 on a successful non‑interactive run is expected — both 0 and 1 are treated as success.

## Configuration reference (`.env` / Settings → LLM Providers)

| `.env` key | Settings key | Meaning |
| --- | --- | --- |
| `COPILOT_GITHUB_TOKEN` | — | Token used for headless auth (or `GH_TOKEN` / `GITHUB_TOKEN`). |
| `COPILOT_CLI_PATH` | `copilot_cli.path` | Blank = auto‑detect; else absolute path to the CLI. |
| `COPILOT_CLI_MODEL` | `copilot_cli.model` | Blank = CLI default; else a model your Copilot plan allows. |
| `COPILOT_CLI_CONTEXT` | `copilot_cli.context` | Blank or `long_context`. |
| `COPILOT_CLI_TIMEOUT_SECONDS` | `copilot_cli.timeout_seconds` | Idle timeout (default 180). |

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `Error: No authentication information found` | No valid token for headless mode. Set `COPILOT_GITHUB_TOKEN` in `.env` (Option A) **or** `gh auth login` with `gh` on the backend's PATH (Option B), then restart the backend. |
| Interactive `copilot` chat works but the app fails | Expected — chat uses `/login`; the app uses `copilot -p`, which needs a token. They're independent. |
| `GitHub Copilot CLI not found` | `npm install -g @github/copilot`, or set `COPILOT_CLI_PATH` to the absolute path. |
| Works locally, fails in Docker | Ensure `COPILOT_GITHUB_TOKEN` is in `.env` (compose passes `.env` to the backend). The `gh`/interactive paths aren't available inside the container. |
| Model errors / "not entitled" | Blank the model (uses the CLI default) or pick one your Copilot plan allows. Run `copilot -p "List all models" --allow-all-tools` in a terminal to see entitlements. |

## Alternatives

- **Claude CLI** authenticates via its own subscription `/login` (file‑based token the spawn can read) —
  no PAT needed. See the Claude CLI card in Settings → LLM Providers.
- **OpenAICompatible** (e.g. GitHub Models, or any OpenAI‑style gateway) needs only `OPENAI_BASE_URL` +
  `OPENAI_API_KEY` + `OPENAI_MODEL`.
- **Ollama** uses its native API: `OLLAMA_BASE_URL` + `OLLAMA_MODEL`, with `OLLAMA_API_KEY` required
  for cloud endpoints and optional for a local daemon.
