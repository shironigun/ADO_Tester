"""Global concurrency cap for LLM pipelines.

Both orchestrators acquire this single semaphore inside their pipeline runner, so a fan-out
Epic/Feature run (or several) can't stampede the LLM provider with N concurrent pipelines.
Children beyond the cap sit in a "Queued — waiting for a slot" state until a slot frees.
(asyncio.Semaphore in 3.10+ binds lazily to the running loop, so a module-level instance is fine.)
"""
from __future__ import annotations

import asyncio
from datetime import datetime

from app.db import session_scope

# Max concurrent Analyst/Tester/Bug-Writer pipelines across the whole process.
PIPELINE_SEM = asyncio.Semaphore(4)

QUEUED_ACTIVITY = "Queued — waiting for a slot"


async def mark_queued(model, session_id: int) -> None:
    """Show a session as queued while it waits on the semaphore (best-effort)."""
    async with session_scope() as s:
        row = await s.get(model, session_id)
        if row is not None:
            row.current_activity = QUEUED_ACTIVITY
            row.updated_at = datetime.utcnow()
            await s.commit()


_TERMINAL = {"Done", "Drafted", "Failed", "Cancelled"}


async def mark_cancelled(model, session_id: int, cancelled_status) -> None:
    """Set a session to its Cancelled status — used when a still-queued child is cancelled before
    its pipeline body (and its own CancelledError handler) ever runs. Guarded against terminal
    statuses so a cancel landing in the post-commit teardown window can't clobber a child that
    already finished (e.g. committed Drafted/Done) back to Cancelled."""
    async with session_scope() as s:
        row = await s.get(model, session_id)
        if row is not None and str(row.status) not in _TERMINAL:
            row.status = cancelled_status
            row.current_activity = "Cancelled by user"
            row.updated_at = datetime.utcnow()
            await s.commit()
