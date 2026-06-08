"""
ConsoleEvent — one live event in a pipeline's activity stream.

These are produced by the orchestrators (phase/system/result/error/stat events) and by the
Claude CLI streaming client (token/thinking deltas), fanned out to SSE subscribers by the
EventBus, and a narrative subset is persisted to `EventLogJson` for replay of finished runs.

`seq` is assigned by the bus (NOT the producer) so it is the authoritative ordering / replay
cursor for SSE `Last-Event-ID`.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Literal, Optional

EventKind = Literal["phase", "token", "thinking", "system", "stat", "result", "error"]
EventPhase = Literal[
    "fetch", "retrieve", "analyst", "tester", "bugwriter", "push", "summarize",
    "discover", "code-verify",
]
EventLevel = Literal["info", "success", "warn", "error"]

# Kinds that form the durable "narrative" (persisted to EventLogJson). Token/thinking deltas
# are ephemeral live-only and never persisted (would bloat the row).
NARRATIVE_KINDS: frozenset[str] = frozenset({"phase", "system", "result", "error", "stat"})


@dataclass
class ConsoleEvent:
    kind: EventKind
    text: str = ""
    phase: Optional[EventPhase] = None
    level: EventLevel = "info"
    data: Optional[dict[str, Any]] = None
    # Assigned by the bus on publish:
    seq: int = 0
    ts: float = 0.0
    session_key: str = ""

    def as_camel(self) -> dict[str, Any]:
        """camelCase dict for the SSE/JSON frame (matches the rest of the API)."""
        return {
            "seq": self.seq,
            "ts": self.ts,
            "sessionKey": self.session_key,
            "kind": self.kind,
            "phase": self.phase,
            "level": self.level,
            "text": self.text,
            "data": self.data,
        }

    @property
    def is_narrative(self) -> bool:
        return self.kind in NARRATIVE_KINDS

    @property
    def is_terminal(self) -> bool:
        return bool(self.data and self.data.get("terminal"))
