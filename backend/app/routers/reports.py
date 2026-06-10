"""
Reports router — mirrors the .NET ReportsController.
  POST /api/reports/{projectId}/generate
  GET  /api/reports/{projectId}
  GET  /api/reports/detail/{reportId}
  GET  /api/reports/html/{reportId}       (inline HTML for <iframe>)
  GET  /api/reports/download/{reportId}   (forced download)
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import HTMLResponse, Response, StreamingResponse

from app.models import ReportTheme
from app.schemas import GenerateReportRequest, GenerateReportResponse, ReportSchema
from app.services.app_settings import get_app_settings_service, K_REPORT_TEAM_NAME
from app.services.reports import get_report_service
from app.services.reports.mock import build_mock_report_html
from app.services.sse import SSE_HEADERS, event_stream, parse_last_event_id

MAX_RANGE_DAYS = 365

router = APIRouter(prefix="/api/reports", tags=["reports"])
log = logging.getLogger("qa.reports-router")


@router.get("/{report_id}/stream")
async def stream_report(report_id: int, request: Request) -> StreamingResponse:
    """Live SSE console for a report generation (fetch + summarize token deltas + render)."""
    last = parse_last_event_id(
        request.headers.get("last-event-id"), request.query_params.get("lastEventId"))
    return StreamingResponse(
        event_stream(f"report:{report_id}", last),
        media_type="text/event-stream", headers=SSE_HEADERS,
    )


@router.get("/preview")
async def preview(theme: str = "Executive", team: str | None = None) -> HTMLResponse:
    """Render the report deck from fixed mock data — lets Settings preview the format
    without generating a real report. `theme` picks the palette; `team` overrides the
    cover eyebrow (so an unsaved Settings value previews live), else the saved setting.
    NB: declared before the bare GET /{project_id} so 'preview' isn't parsed as an id."""
    chosen = next(
        (t for t in ReportTheme if t.value.lower() == (theme or "").lower()),
        ReportTheme.Executive,
    )
    team_name = team if team is not None else await get_app_settings_service().get(K_REPORT_TEAM_NAME)
    html = build_mock_report_html(theme=chosen, team_name=team_name)
    return HTMLResponse(content=html)


@router.post("/{project_id}/generate", response_model=GenerateReportResponse)
async def generate(project_id: int, req: GenerateReportRequest) -> GenerateReportResponse:
    if req.from_utc >= req.to_utc:
        raise HTTPException(status_code=400, detail="FromUtc must be before ToUtc.")
    range_days = (req.to_utc - req.from_utc).total_seconds() / 86400
    if range_days > MAX_RANGE_DAYS:
        raise HTTPException(
            status_code=400,
            detail=f"Date range {range_days:.0f} days exceeds max {MAX_RANGE_DAYS} days.",
        )
    try:
        rid = await get_report_service().generate(
            project_id, req.from_utc, req.to_utc, req.boards or [],
            trigger="Manual", theme=req.theme,
        )
        return GenerateReportResponse(report_id=rid)
    except Exception as e:  # noqa: BLE001
        log.exception("Report generation kickoff failed for project %d", project_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/{project_id}", response_model=list[ReportSchema])
async def list_(project_id: int) -> list[ReportSchema]:
    try:
        rows = await get_report_service().list_(project_id)
        return [ReportSchema.model_validate(r) for r in rows]
    except Exception as e:  # noqa: BLE001
        log.exception("Report list failed for project %d", project_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/detail/{report_id}", response_model=ReportSchema)
async def get(report_id: int) -> ReportSchema:
    try:
        r = await get_report_service().get(report_id)
        if r is None:
            raise HTTPException(status_code=404, detail="Report not found.")
        return ReportSchema.model_validate(r)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("Report detail failed for %d", report_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/html/{report_id}", response_class=HTMLResponse)
async def html(report_id: int) -> HTMLResponse:
    try:
        body = await get_report_service().read_html(report_id)
        if body is None:
            raise HTTPException(status_code=404, detail="Report HTML not found.")
        return HTMLResponse(content=body, media_type="text/html; charset=utf-8")
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("Report html read failed for %d", report_id)
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.get("/download/{report_id}")
async def download(report_id: int) -> Response:
    svc = get_report_service()
    try:
        body = await svc.read_html(report_id)
        if body is None:
            raise HTTPException(status_code=404, detail="Report HTML not found.")
        report = await svc.get(report_id)
        name = (
            f"report-{report_id}-"
            f"{report.from_utc:%Y%m%d}-to-{report.to_utc:%Y%m%d}.html"
            if report else f"report-{report_id}.html"
        )
        return Response(
            content=body.encode("utf-8"),
            media_type="text/html",
            headers={"Content-Disposition": f'attachment; filename="{name}"'},
        )
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        log.exception("Report download failed for %d", report_id)
        raise HTTPException(status_code=500, detail=str(e)) from None
