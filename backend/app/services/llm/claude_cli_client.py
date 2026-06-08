"""
Claude CLI client — shells out to the local `claude` CLI in print mode.

This lets you use a Pro/Max subscription instead of an API key for single-user, local-only
runs. Each call spawns one CLI process: `claude -p` (print/headless mode). System + user
prompts are concatenated; the CLI prints the final assistant text to stdout.

Trade-offs:
  - Slower than API calls (~2-5s CLI startup overhead per call).
  - Auth is whatever the CLI is logged into on this machine (no key required).
  - Subject to Pro/Max conversation limits, not API tier limits.

IMPORTANT (Windows): we use the synchronous `subprocess.run` on a worker thread
(`asyncio.to_thread`) rather than `asyncio.create_subprocess_exec`. On Windows the
asyncio subprocess API is unsupported under the Selector event loop that uvicorn
installs and raises a bare `NotImplementedError` (empty message) — which previously
surfaced as an empty 502 and made every agent run "fail at fetch". `subprocess.run`
in a thread works on any event loop.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import queue
import shutil
import subprocess
import threading
import time

from app.config import settings
from app.services.llm.base import StreamProgress


log = logging.getLogger("qa.claude-cli")

# Absolute safety cap on a single CLI call (the per-call timeout is idle-based —
# see complete()). Generous so a long-but-actively-streaming run is never killed.
_ABSOLUTE_CAP_SECONDS = 1800


def _extract_delta_text(obj: dict) -> str | None:
    """Best-effort pull of incremental text from a stream-json partial event.
    Used only for the live char counter — the authoritative answer comes from the
    `result` event — so imperfect matching here is harmless."""
    d = obj.get("delta")
    if isinstance(d, dict) and isinstance(d.get("text"), str):
        return d["text"]
    ev = obj.get("event")
    if isinstance(ev, dict):
        d2 = ev.get("delta")
        if isinstance(d2, dict) and isinstance(d2.get("text"), str):
            return d2["text"]
    if obj.get("type") == "text_delta" and isinstance(obj.get("text"), str):
        return obj["text"]
    return None


def _classify_delta(obj: dict) -> tuple[str, str] | None:
    """Classify a stream-json partial event into ('token'|'thinking', text), or None.
    Handles top-level deltas and `content_block_delta` / `stream_event` wrappers."""
    candidates: list[dict] = []
    d = obj.get("delta")
    if isinstance(d, dict):
        candidates.append(d)
    ev = obj.get("event")
    if isinstance(ev, dict):
        ed = ev.get("delta")
        if isinstance(ed, dict):
            candidates.append(ed)
    if obj.get("type") in ("text_delta", "thinking_delta"):
        candidates.append(obj)
    for c in candidates:
        ctype = c.get("type")
        if ctype == "thinking_delta":
            t = c.get("thinking") or c.get("text")
            if isinstance(t, str):
                return ("thinking", t)
        if ctype == "text_delta":
            t = c.get("text")
            if isinstance(t, str):
                return ("token", t)
        if ctype is None:
            if isinstance(c.get("text"), str):
                return ("token", c["text"])
            if isinstance(c.get("thinking"), str):
                return ("thinking", c["thinking"])
    return None


def resolve_claude_cli_path() -> str | None:
    """
    Locate a runnable `claude` executable. Tries, in order: the configured path,
    a PATH lookup, then common per-user install locations. Returns an absolute path
    (or a PATH-resolved one), or None if nothing is found. Shared by the diagnostics
    probe so the UI status and the provider agree.
    """
    configured = (settings.claude_cli_path or "claude").strip()
    home = os.path.expanduser("~")
    candidates = [
        configured,
        shutil.which(configured) or "",
        os.path.join(home, ".local", "bin", "claude.exe"),
        os.path.join(home, ".local", "bin", "claude"),
        os.path.join(home, "AppData", "Roaming", "npm", "claude.cmd"),
        os.path.join(home, "AppData", "Roaming", "npm", "claude"),
        os.path.join(home, "AppData", "Local", "Programs", "Claude", "claude.exe"),
        "/usr/local/bin/claude",
        "/opt/homebrew/bin/claude",
    ]
    seen: set[str] = set()
    for c in candidates:
        if not c or c in seen:
            continue
        seen.add(c)
        if os.path.isabs(c):
            if os.path.exists(c):
                return c
        else:
            w = shutil.which(c)
            if w:
                return w
    return None


def probe_claude_cli() -> tuple[bool, str | None]:
    """Run `claude --version`. Returns (available, version_string). Synchronous —
    callers should wrap with asyncio.to_thread."""
    cli = resolve_claude_cli_path()
    if not cli:
        return False, None
    try:
        result = subprocess.run(
            [cli, "--version"], capture_output=True, timeout=10, check=False,
        )
        if result.returncode == 0:
            return True, (result.stdout or b"").decode(errors="replace").strip() or "ok"
    except Exception:  # noqa: BLE001 — any failure means "not usable"
        return False, None
    return False, None


class ClaudeCliLlmClient:
    @property
    def provider_name(self) -> str:
        return "ClaudeCli"

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
        progress: StreamProgress | None = None,
        emit=None,  # Optional[Callable[[dict],None]] — live token/thinking deltas
    ) -> str:
        cli = resolve_claude_cli_path()
        if cli is None:
            raise RuntimeError(
                f"Claude CLI not found (configured path: '{settings.claude_cli_path}'). "
                "Install Claude Code and log in, or set CLAUDE_CLI_PATH in the .env to an "
                "absolute path to claude.exe."
            )

        # The CLI's print mode (`-p` / `--print`) takes a prompt and returns the final text.
        # System prompt is concatenated as a leading instruction block.
        combined = (
            f"<system_instructions>\n{system_prompt.strip()}\n</system_instructions>\n\n"
            f"{user_prompt}"
        )

        # Stream the output so the run survives long generations and we can show live
        # progress. --include-partial-messages emits token-level deltas continuously, so
        # there is no long silent gap — the timeout below is IDLE-based (no output for N
        # seconds), NOT a hard wall on total duration. This is what fixes the "timed out
        # after Ns while on Analyst" failure: a long-but-active run is never killed.
        # Prompt goes via STDIN (not argv) so large code-context prompts don't blow the
        # Windows ~32KB command-line limit (WinError 206).
        args = [
            cli, "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
        ]
        chosen_model = (model or settings.claude_cli_model).strip()
        if chosen_model:
            args.extend(["--model", chosen_model])

        idle_timeout = max(30, settings.claude_cli_timeout_seconds)
        log.info("Streaming claude CLI %s (model=%s, idle=%ss, prompt=%d chars)",
                 cli, chosen_model or "default", idle_timeout, len(combined))

        try:
            return await asyncio.to_thread(self._run_stream, args, idle_timeout, progress, emit, combined)
        except FileNotFoundError as e:
            raise RuntimeError(
                f"Could not launch Claude CLI '{cli}': {e}. "
                "Confirm Claude Code is installed and `claude --version` works in a terminal."
            ) from None

    @staticmethod
    def _run_stream(args: list[str], idle_timeout: int, progress: StreamProgress | None,
                    emit=None, stdin_text: str = "") -> str:
        """Blocking — run on a worker thread. Reads stream-json line by line via a reader
        thread + queue so we can enforce an idle timeout (kill only after `idle_timeout`s of
        complete silence) and an absolute safety cap. The prompt is fed via stdin (a writer
        thread) to avoid the Windows command-line length limit."""
        proc = subprocess.Popen(
            args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
        )

        # Feed the prompt on a dedicated thread so a large stdin never deadlocks against stdout.
        def _writer() -> None:
            try:
                if proc.stdin is not None:
                    proc.stdin.write(stdin_text)
                    proc.stdin.close()
            except Exception:  # noqa: BLE001
                pass

        threading.Thread(target=_writer, daemon=True).start()

        line_q: "queue.Queue[tuple[str, str | None]]" = queue.Queue()

        def _reader() -> None:
            try:
                assert proc.stdout is not None
                for line in proc.stdout:
                    line_q.put(("line", line))
            finally:
                line_q.put(("eof", None))

        threading.Thread(target=_reader, daemon=True).start()

        final_text: str | None = None
        chars = 0
        start = time.monotonic()

        # Coalesce token/thinking deltas in-thread before emitting, so a fast stream
        # produces ~12 events/s/session instead of hundreds. Flush on channel switch,
        # ~80ms elapsed, ≥256 chars, or end.
        buf_channel: str | None = None
        buf_text: list[str] = []
        last_flush = time.monotonic()

        def _flush() -> None:
            nonlocal buf_channel, buf_text, last_flush
            if emit is not None and buf_channel and buf_text:
                chunk = "".join(buf_text)
                if chunk:
                    try:
                        emit({"kind": buf_channel, "text": chunk})
                    except Exception:  # noqa: BLE001 — never let the console break the run
                        pass
            buf_text = []
            buf_channel = None
            last_flush = time.monotonic()

        while True:
            if time.monotonic() - start > _ABSOLUTE_CAP_SECONDS:
                proc.kill()
                raise RuntimeError(f"Claude CLI exceeded the absolute cap of {_ABSOLUTE_CAP_SECONDS}s.")
            try:
                kind, payload = line_q.get(timeout=idle_timeout)
            except queue.Empty:
                proc.kill()
                raise RuntimeError(
                    f"Claude CLI produced no output for {idle_timeout}s (stalled). "
                    "Increase claude_cli.timeout_seconds in Settings if the model needs longer."
                ) from None

            if kind == "eof":
                break
            line = (payload or "").strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue

            if obj.get("type") == "result":
                r = obj.get("result")
                if isinstance(r, str):
                    final_text = r
                continue

            cls = _classify_delta(obj)
            if cls is None:
                if progress is not None:
                    progress.tick()
                continue
            channel, text = cls
            if channel == "token":
                chars += len(text)
                if progress is not None:
                    progress.update(chars=chars)
            elif progress is not None:
                progress.tick()

            # Coalesce for the live console.
            if emit is not None and text:
                if buf_channel and buf_channel != channel:
                    _flush()
                buf_channel = channel
                buf_text.append(text)
                if sum(len(t) for t in buf_text) >= 256 or (time.monotonic() - last_flush) >= 0.08:
                    _flush()

        _flush()
        rc = proc.wait()
        stderr = ""
        if proc.stderr is not None:
            stderr = (proc.stderr.read() or "").strip()

        if rc not in (0, None):
            raise RuntimeError(f"Claude CLI exited {rc}: {stderr[:500] or '(no stderr)'}")
        if not final_text:
            raise RuntimeError("Claude CLI stream ended without a result." + (f" stderr: {stderr[:300]}" if stderr else ""))
        return final_text
