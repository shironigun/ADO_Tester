"""
EventBus — in-memory pub/sub for live pipeline console events (SSE).

Singleton (mirrors get_agent_orchestrator). Per session-key it holds:
  - a set of subscriber asyncio.Queues (fan-out → multiple browser tabs / SSE connections)
  - a bounded ring buffer (recent events) for reconnect / late-subscriber replay
  - a monotonic seq counter

Two publish paths:
  - publish(...)            — called from the event loop (orchestrators / reports service)
  - publish_threadsafe(...) — called from the Claude CLI reader THREAD; bridges to the loop
                              via loop.call_soon_threadsafe (non-blocking, never stalls the
                              reader, so the CLI idle-timeout behaviour is unaffected)

Keys are namespaced strings ("session:{id}", "report:{id}") so session ids and report ids
never alias.
"""
from __future__ import annotations

import asyncio
import threading
import time
from collections import deque
from typing import Any, Optional

from app.services.events import ConsoleEvent

_RING_MAX = 400          # recent events kept for replay (incl. coalesced tokens)
_QUEUE_MAX = 1000        # per-subscriber bound; oldest dropped on overflow
_GRACE_TTL_SECONDS = 600  # keep ring this long after terminal + last unsub, for late reconnects


class _SessionState:
    __slots__ = ("subscribers", "ring", "seq", "terminal_at")

    def __init__(self) -> None:
        self.subscribers: set[asyncio.Queue] = set()
        self.ring: deque[ConsoleEvent] = deque(maxlen=_RING_MAX)
        self.seq: int = 0
        self.terminal_at: float | None = None


class EventBus:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sessions: dict[str, _SessionState] = {}

    # ── helpers ──────────────────────────────────────────────────────────────
    def _state(self, key: str) -> _SessionState:
        st = self._sessions.get(key)
        if st is None:
            st = _SessionState()
            self._sessions[key] = st
        return st

    # ── publish (loop-side) ────────────────────────────────────────────────
    def publish(self, key: str, ev: ConsoleEvent) -> None:
        """Finalize + fan out. Safe to call only from the event loop thread."""
        with self._lock:
            st = self._state(key)
            st.seq += 1
            ev.seq = st.seq
            ev.ts = time.time()
            ev.session_key = key
            st.ring.append(ev)
            if ev.is_terminal:
                st.terminal_at = time.time()
            subscribers = list(st.subscribers)
        for q in subscribers:
            try:
                q.put_nowait(ev)
            except asyncio.QueueFull:
                # Slow/wedged client: drop oldest, keep newest. It recovers missed
                # events from the ring on reconnect (seq gap).
                try:
                    q.get_nowait()
                    q.put_nowait(ev)
                except Exception:  # noqa: BLE001
                    pass

    # ── publish (thread-side) ────────────────────────────────────────────────
    def publish_threadsafe(self, loop: asyncio.AbstractEventLoop, key: str, ev: ConsoleEvent) -> None:
        """Called from a worker thread (the CLI reader). Schedules publish() on the loop.
        call_soon_threadsafe is non-blocking and never raises while the loop is alive."""
        try:
            loop.call_soon_threadsafe(self.publish, key, ev)
        except RuntimeError:
            # Loop closed (shutdown) — drop silently.
            pass

    # ── subscribe / unsubscribe ──────────────────────────────────────────────
    def subscribe(self, key: str, after_seq: int | None = None) -> asyncio.Queue:
        """Create a queue, pre-seeded with ring events newer than `after_seq` (replay)."""
        q: asyncio.Queue = asyncio.Queue(maxsize=_QUEUE_MAX)
        with self._lock:
            st = self._state(key)
            st.subscribers.add(q)
            replay = [e for e in st.ring if after_seq is None or e.seq > after_seq]
        for e in replay:
            try:
                q.put_nowait(e)
            except asyncio.QueueFull:
                break
        return q

    def unsubscribe(self, key: str, q: asyncio.Queue) -> None:
        with self._lock:
            st = self._sessions.get(key)
            if not st:
                return
            st.subscribers.discard(q)
            # Drop fully-finished sessions whose grace window has elapsed.
            self._gc_locked()

    def _gc_locked(self) -> None:
        now = time.time()
        dead = [
            k for k, st in self._sessions.items()
            if not st.subscribers and st.terminal_at and (now - st.terminal_at) > _GRACE_TTL_SECONDS
        ]
        for k in dead:
            self._sessions.pop(k, None)

    # ── narrative snapshot for persistence ───────────────────────────────────
    def narrative(self, key: str, cap: int = 300) -> list[dict[str, Any]]:
        """The durable (non-token) events for EventLogJson, most-recent `cap`."""
        with self._lock:
            st = self._sessions.get(key)
            if not st:
                return []
            items = [e for e in st.ring if e.is_narrative]
        return [e.as_camel() for e in items[-cap:]]


_singleton: Optional[EventBus] = None


def get_event_bus() -> EventBus:
    global _singleton
    if _singleton is None:
        _singleton = EventBus()
    return _singleton
