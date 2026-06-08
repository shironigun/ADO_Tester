"""
Shared Server-Sent Events generator for the live agent console.

Subscribes to the EventBus for a session key, replays missed events (via Last-Event-ID),
streams `id:`/`data:` frames, sends periodic keepalive comments so proxies don't drop the
connection, and terminates on a terminal event or client disconnect.
"""
from __future__ import annotations

import asyncio
import json
from typing import AsyncIterator

from app.services.event_bus import get_event_bus

_HEARTBEAT_SECONDS = 15

# Headers that keep SSE flowing through the Vite dev proxy / any reverse proxy.
SSE_HEADERS = {
    "Cache-Control": "no-cache, no-transform",
    "Connection": "keep-alive",
    "X-Accel-Buffering": "no",
}


async def event_stream(key: str, last_event_id: int | None) -> AsyncIterator[str]:
    bus = get_event_bus()
    q = bus.subscribe(key, after_seq=last_event_id)
    try:
        yield ": connected\n\n"
        while True:
            try:
                ev = await asyncio.wait_for(q.get(), timeout=_HEARTBEAT_SECONDS)
            except asyncio.TimeoutError:
                yield ": keepalive\n\n"
                continue
            yield f"id: {ev.seq}\ndata: {json.dumps(ev.as_camel())}\n\n"
            if ev.is_terminal:
                break
    finally:
        bus.unsubscribe(key, q)


def parse_last_event_id(header_value: str | None, query_value: str | None) -> int | None:
    for v in (header_value, query_value):
        if v and v.isdigit():
            return int(v)
    return None
