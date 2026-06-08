"""
Claude CLI client — shells out to the local `claude` CLI in print mode.

This lets you use a Pro/Max subscription instead of an API key for single-user, local-only
runs. Each call spawns one CLI process: `claude -p` (print/headless mode). System + user
prompts are concatenated; the CLI prints the final assistant text to stdout.

Trade-offs:
  - Slower than API calls (~2-5s CLI startup overhead per call).
  - Auth is whatever the CLI is logged into on this machine (no key required).
  - Subject to Pro/Max conversation limits, not API tier limits.
"""
from __future__ import annotations

import asyncio
import logging
import shutil

from app.config import settings


log = logging.getLogger("qa.claude-cli")


class ClaudeCliLlmClient:
    @property
    def provider_name(self) -> str:
        return "ClaudeCli"

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
    ) -> str:
        cli = settings.claude_cli_path
        # Resolve a friendly error early if the CLI isn't on PATH.
        if shutil.which(cli) is None and not cli.endswith(".exe"):
            raise RuntimeError(
                f"Claude CLI '{cli}' not found on PATH. Install Claude Code and log in, "
                "or set CLAUDE_CLI_PATH in the .env to an absolute path."
            )

        # The CLI's print mode (`-p` / `--print`) takes a prompt and returns the final text.
        # System prompt is concatenated as a leading instruction block.
        combined = (
            f"<system_instructions>\n{system_prompt.strip()}\n</system_instructions>\n\n"
            f"{user_prompt}"
        )

        args = [cli, "-p", combined]
        chosen_model = (model or settings.claude_cli_model).strip()
        if chosen_model:
            args.extend(["--model", chosen_model])

        log.info("Spawning claude CLI (model=%s)", chosen_model or "default")

        try:
            proc = await asyncio.create_subprocess_exec(
                *args,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
        except FileNotFoundError as e:
            raise RuntimeError(
                f"Could not launch Claude CLI '{cli}': {e}. "
                "Confirm Claude Code is installed and `claude --version` works in a terminal."
            ) from None

        try:
            stdout, stderr = await asyncio.wait_for(
                proc.communicate(), timeout=settings.claude_cli_timeout_seconds
            )
        except asyncio.TimeoutError:
            proc.kill()
            raise RuntimeError(
                f"Claude CLI timed out after {settings.claude_cli_timeout_seconds}s."
            ) from None

        if proc.returncode != 0:
            err = (stderr or b"").decode(errors="replace").strip()
            raise RuntimeError(f"Claude CLI exited {proc.returncode}: {err or '(no stderr)'}")

        text = (stdout or b"").decode(errors="replace").strip()
        if not text:
            raise RuntimeError("Claude CLI returned empty output.")
        return text
