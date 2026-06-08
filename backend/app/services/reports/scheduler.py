"""
APScheduler-based weekly report cron — Thursday 11:00 in the configured timezone.

Same constraint as the .NET BackgroundService: the app must be running at the trigger moment;
there's no catch-up for a missed slot (user generates on demand instead).
"""
from __future__ import annotations

import logging
from datetime import datetime, timedelta

from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger

from app.config import settings
from app.services.reports.service import get_report_service

log = logging.getLogger("qa.report-scheduler")

DEFAULT_PROJECT_ID = 1


class WeeklyReportScheduler:
    def __init__(self) -> None:
        self._scheduler = AsyncIOScheduler(timezone=settings.scheduler_timezone)

    async def _fire(self) -> None:
        # Honour the UI toggle + default boards + default range, all read live from settings.
        from app.services.app_settings import (
            K_DEFAULT_BOARDS,
            K_DEFAULT_RANGE,
            K_SCHED_ENABLED,
            get_app_settings_service,
        )

        s = get_app_settings_service()
        enabled = (await s.get(K_SCHED_ENABLED)).lower()
        if enabled not in {"true", "1", "yes", "on"}:
            log.info("Weekly report skipped — schedule disabled in settings")
            return

        try:
            days = int(await s.get(K_DEFAULT_RANGE)) or 7
        except ValueError:
            days = 7
        boards_csv = (await s.get(K_DEFAULT_BOARDS)) or ""
        boards = [b.strip() for b in boards_csv.replace(";", ",").split(",") if b.strip()]

        to_utc = datetime.utcnow()
        from_utc = to_utc - timedelta(days=days)
        log.info("Weekly report fire: %s → %s (boards=%s)", from_utc, to_utc, boards)
        try:
            await get_report_service().generate(
                project_id=DEFAULT_PROJECT_ID,
                from_utc=from_utc,
                to_utc=to_utc,
                boards=boards,
                trigger="Scheduled",
            )
        except Exception:  # noqa: BLE001
            log.exception("Weekly report fire failed")

    def start(self) -> None:
        self._scheduler.add_job(
            self._fire,
            trigger=CronTrigger(day_of_week="thu", hour=11, minute=0),
            id="weekly-report",
            replace_existing=True,
        )
        self._scheduler.start()
        log.info("WeeklyReportScheduler started (timezone=%s)", settings.scheduler_timezone)

    def shutdown(self) -> None:
        self._scheduler.shutdown(wait=False)


_singleton: WeeklyReportScheduler | None = None


def get_scheduler() -> WeeklyReportScheduler:
    global _singleton
    if _singleton is None:
        _singleton = WeeklyReportScheduler()
    return _singleton
