# Using the Cursor Agent CLI provider

This guide sets up the **CursorCli** LLM provider for ADO_Tester — agents run through the local
Cursor **`agent`** / **`cursor-agent`** CLI (not the IDE `cursor.exe`, and not the npm `@cursor/sdk`
package). Auth is your Cursor subscription via `agent login` or a `CURSOR_API_KEY`.

> **Headless mode:** the backend invokes `agent -p --output-format text --force --trust` with a
> scratch `--workspace` under `Storage:DataDirectory/llm-scratch/cursor`, so LLM prompts do **not**
> edit the ADO_Tester checkout.

AppBar chips next to the provider switcher show the active model / effort / context as **display-only**.
Change those values in **Settings → LLM Providers → Cursor CLI**.

---

## Step 1 — Install Cursor Agent

Install the Cursor Agent CLI so `agent` (or `cursor-agent`) is available. Typical Windows location:

`%LOCALAPPDATA%\cursor-agent`

Confirm:

```powershell
agent --version
agent --list-models
```

The backend auto-detects `agent` / `cursor-agent` on PATH and under `%LOCALAPPDATA%\cursor-agent`.
To pin a path, set `CURSOR_CLI_PATH` in `.env` or `cursor_cli.path` in Settings.

## Step 2 — Authenticate

Pick one:

```powershell
agent login
```

Or set in the repo-root `.env`:

```dotenv
CURSOR_API_KEY=<your-key>
```

## Step 3 — Enable in ADO_Tester

1. Restart the backend after `.env` changes.
2. Open **Settings → LLM Providers → Cursor CLI**. Featured models at the top:
   **Haiku**, **Composer 2.5**, **Cursor Grok 4.5** — each with effort + context (Default / 200k / 1M).
   Remaining ids come from live `agent --list-models` (effort-suffixed grok/composer variants are collapsed).
3. Switch the AppBar provider to **CursorCli**. Chips should reflect the saved model/effort/context.
   Spawn uses parameterized `--model` values, e.g. `haiku[effort=low,context=200k]` or
   `cursor-grok-4.5[effort=high,context=1m]` (per Cursor Agent CLI bracket overrides).

Env keys (also in [`.env.example`](../../.env.example)):

| Env | Settings key | Notes |
|-----|--------------|--------|
| `CURSOR_CLI_PATH` | `cursor_cli.path` | Blank = auto-detect |
| `CURSOR_CLI_MODEL` | `cursor_cli.model` | Blank → `auto` |
| `CURSOR_CLI_EFFORT` | `cursor_cli.effort` | Composed as `--model id[effort=…]` |
| `CURSOR_CLI_CONTEXT` | `cursor_cli.context` | Composed as `--model id[context=…]` (e.g. `1m`) |
| `CURSOR_CLI_TIMEOUT_SECONDS` | `cursor_cli.timeout_seconds` | Idle timeout floor |
| `CURSOR_API_KEY` | — | Optional if using `agent login` |

Catalog API: `GET /api/settings/cursor-cli/catalog` (falls back to a static list if the CLI is missing).
