"""LLM provider interface — same shape as the .NET ILlmClient."""
from __future__ import annotations

import threading
import time
from typing import Any, Callable, Optional, Protocol

# Emit callback for live streaming events. Streaming providers (ClaudeCli) invoke it with
# {'kind': 'token'|'thinking', 'text': chunk}; non-streaming providers ignore it. Mirrors the
# optional `progress` kwarg — never required, keyword-only at call sites.
EmitFn = Callable[[dict[str, Any]], None]


class StreamProgress:
    """
    Thread-safe progress holder shared between a (possibly threaded) streaming LLM
    call and the orchestrator's polling loop. The provider mutates it as tokens
    stream in; the orchestrator reads `snapshot()` periodically to update the
    session's live activity. Plain object + lock — no DB, no event-loop coupling.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._chars = 0
        self._ticks = 0
        self._t0 = time.monotonic()

    def update(self, *, chars: int | None = None) -> None:
        with self._lock:
            if chars is not None:
                self._chars = chars
            self._ticks += 1

    def tick(self) -> None:
        with self._lock:
            self._ticks += 1

    def snapshot(self) -> dict:
        with self._lock:
            return {"chars": self._chars, "ticks": self._ticks, "elapsed": time.monotonic() - self._t0}


class LlmClient(Protocol):
    """Provider-agnostic chat completion. Implementations: Anthropic, AzureOpenAI, OpenAICompatible, ClaudeCli."""

    @property
    def provider_name(self) -> str: ...

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
        progress: Optional[StreamProgress] = None,
        emit: Optional[EmitFn] = None,
    ) -> str:
        """Single-turn completion. Returns the concatenated text content of the reply.

        `progress`, if given, is updated live by streaming providers (currently
        ClaudeCli); non-streaming providers ignore it. `emit`, if given, receives live
        {'kind','text'} delta dicts from streaming providers; ignored by the others.
        """
        ...
