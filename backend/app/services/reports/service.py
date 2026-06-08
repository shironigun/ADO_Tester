"""
ReportService — ports .NET ReportService.

Seeds a Report row synchronously (so the caller gets an id) then kicks off an asyncio.Task that
opens a fresh SessionLocal, queries ADO for tickets in range, builds the HTML, writes it under
data/reports/, and flips status to Done (or Failed).
"""
from __future__ import annotations

import asyncio
import json
import logging
import uuid
from datetime import datetime
from pathlib import Path

from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal, session_scope
from app.models import Report, ReportStatus, ReportTheme
from app.services.ado_client import get_ado_client
from app.services.reports.html_builder import build_report_html
from app.services.reports.summarizer import ReportSummarizer
from app.services.event_bus import get_event_bus
from app.services.events import ConsoleEvent
from app.services.orchestrators.agent_orchestrator import _emit_closure

log = logging.getLogger("qa.reports")


def _rpub(key: str, kind: str, phase: str | None = None, text: str = "",
          level: str = "info", data: dict | None = None) -> None:
    get_event_bus().publish(key, ConsoleEvent(kind=kind, phase=phase, text=text, level=level, data=data))


class ReportService:
    def __init__(self) -> None:
        self._reports_dir: Path = settings.data_path / settings.reports_folder_name
        self._reports_dir.mkdir(parents=True, exist_ok=True)

    async def generate(
        self,
        project_id: int,
        from_utc: datetime,
        to_utc: datetime,
        boards: list[str],
        trigger: str = "Manual",
        theme: ReportTheme = ReportTheme.Executive,
    ) -> int:
        async with SessionLocal() as s:
            report = Report(
                project_id=project_id,
                from_utc=from_utc,
                to_utc=to_utc,
                boards="; ".join(boards),
                status=ReportStatus.Generating,
                created_at=datetime.utcnow(),
                title=f"Weekly Report — {from_utc:%Y-%m-%d} to {to_utc:%Y-%m-%d}",
                trigger=trigger,
                theme=theme,
            )
            s.add(report)
            await s.commit()
            await s.refresh(report)
            rid = report.id

        asyncio.create_task(
            self._generate_in_background(rid, project_id, from_utc, to_utc, boards, theme)
        )
        return rid

    async def list_(self, project_id: int) -> list[Report]:
        async with SessionLocal() as s:
            rows = (await s.execute(
                select(Report).where(Report.project_id == project_id)
                .order_by(Report.created_at.desc())
            )).scalars().all()
            return list(rows)

    async def get(self, report_id: int) -> Report | None:
        async with SessionLocal() as s:
            return await s.get(Report, report_id)

    async def read_html(self, report_id: int) -> str | None:
        report = await self.get(report_id)
        if report is None or not report.html_path:
            return None
        p = Path(report.html_path)
        if not p.exists():
            return None
        return p.read_text(encoding="utf-8")

    async def _generate_in_background(
        self, report_id: int, project_id: int,
        from_utc: datetime, to_utc: datetime, boards: list[str],
        theme: ReportTheme,
    ) -> None:
        ado = get_ado_client()
        key = f"report:{report_id}"
        loop = asyncio.get_running_loop()
        bus = get_event_bus()

        def _persist(report: Report) -> None:
            try:
                report.event_log_json = json.dumps(bus.narrative(key))
            except Exception:  # noqa: BLE001
                pass

        async with session_scope() as s:
            report = await s.get(Report, report_id)
            if report is None:
                return
            try:
                _rpub(key, "system", text="Report generation started")
                tickets = await ado.get_tickets_in_range(from_utc, to_utc, boards)
                boards_label = "" if not boards else "Boards: " + ", ".join(boards)
                _rpub(key, "result", "fetch", f"Pulled {len(tickets)} ticket(s) in range",
                      level="success", data={"tickets": len(tickets)})

                # AI summarization always runs. Failures don't block — summarizer returns
                # a minimal fallback so the deck still has a TL;DR.
                _rpub(key, "phase", "summarize", "Summarizer — generating AI insights")
                summary = await ReportSummarizer().summarize(
                    tickets, emit=_emit_closure(loop, key, "summarize"))
                report.summary_json = summary.to_json()
                await s.commit()
                _rpub(key, "result", "summarize", "AI insights generated", level="success")

                _rpub(key, "system", "summarize", "Rendering self-contained HTML…")
                html = build_report_html(
                    report.title, from_utc, to_utc, boards_label, tickets,
                    theme=theme, summary=summary,
                )

                file_name = (
                    f"report-{report_id}-{datetime.utcnow():%Y%m%d%H%M%S}-"
                    f"{uuid.uuid4().hex[:8]}.html"
                )
                path = self._reports_dir / file_name
                path.write_text(html, encoding="utf-8")

                report.ticket_count = len(tickets)
                report.html_path = str(path)
                report.status = ReportStatus.Done
                _rpub(key, "system", "summarize", f"Report ready · {len(tickets)} ticket(s)",
                      level="success", data={"terminal": True})
                _persist(report)
                await s.commit()
            except Exception as e:  # noqa: BLE001
                log.exception("Report %d generation failed", report_id)
                report.status = ReportStatus.Failed
                report.error_message = str(e)
                _rpub(key, "error", text=f"Failed: {e}", level="error", data={"terminal": True})
                _persist(report)
                try:
                    await s.commit()
                except Exception:  # noqa: BLE001
                    pass


_singleton: ReportService | None = None


def get_report_service() -> ReportService:
    global _singleton
    if _singleton is None:
        _singleton = ReportService()
    return _singleton
