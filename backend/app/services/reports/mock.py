"""
Mock report builder — renders the full self-contained HTML from a fixed, realistic
sample dataset (no ADO, no DB). Used by the Settings → Reports "Preview format" button
so the deck layout can be reviewed without generating a real report.

It exercises every slide: cover, by-board matrix + issues, by-person, themes (modules),
risks, wins, per-developer spotlights, and the ticket modal (comments + timeline).
"""
from __future__ import annotations

from datetime import datetime

from app.models import ReportTheme
from app.services.ado_client import ReportTicket, TicketComment, TimelineEvent
from app.services.reports.html_builder import build_report_html
from app.services.reports.summarizer import ReportSummary, ThemeCluster

_AREA = "Ideal Agile\\"


def _ticket(
    tid: int, title: str, typ: str, state: str, who: str, board: str,
    *, root_cause: str | None = None,
    comments: list[TicketComment] | None = None,
    timeline: list[TimelineEvent] | None = None,
) -> ReportTicket:
    closed = state in {"Closed", "Resolved", "Done"}
    desc = f"<div>Sample description for <b>{title}</b>. Repro steps and expected vs actual would appear here.</div>"
    return ReportTicket(
        id=tid, title=title, work_item_type=typ, state=state, assigned_to=who,
        issue_description=desc, fix="Resolved in build 2026.05.3." if closed else None,
        closed_at="2026-05-22T10:00:00Z" if closed else None,
        created_by="Hajra Baksh", created_date="2026-05-19T09:00:00Z",
        description=desc, root_cause=root_cause, area_path=_AREA + board,
        comments=comments or [], timeline=timeline or [],
    )


def build_mock_report_html(theme: ReportTheme = ReportTheme.Executive, team_name: str = "") -> str:
    cmts = [
        TicketComment(author="Sarah Chen", date="2026-05-20T11:00:00Z",
                      html="<div>Reproduced on staging — looks like the parser rejects non-CSV extensions.</div>"),
        TicketComment(author="Marcus Lee", date="2026-05-21T14:30:00Z",
                      html="<div>Fix deployed to QA, please re-verify the XLSX path.</div>"),
    ]
    tl = [
        TimelineEvent(date="2026-05-19T09:00:00Z", actor="Hajra Baksh", kind="created",
                      detail="Work item created"),
        TimelineEvent(date="2026-05-19T09:05:00Z", actor="Hajra Baksh", kind="state",
                      detail="State New → Active"),
        TimelineEvent(date="2026-05-20T11:00:00Z", actor="Sarah Chen", kind="assignment",
                      detail="Assigned to Unassigned → Sarah Chen"),
        TimelineEvent(date="2026-05-20T11:00:00Z", actor="Sarah Chen", kind="comment",
                      detail="Comment added"),
        TimelineEvent(date="2026-05-22T10:00:00Z", actor="Marcus Lee", kind="state",
                      detail="State Active → Resolved"),
    ]
    tickets = [
        # CRM Team — Sarah Chen (spotlight)
        _ticket(101, "CSV import rejects valid XLSX files", "Bug", "Resolved", "Sarah Chen", "CRM Team",
                comments=cmts, timeline=tl),
        _ticket(102, "Duplicate customers created on re-import", "Defect", "Closed", "Sarah Chen", "CRM Team"),
        _ticket(103, "Add column-mapping step to import wizard", "User Story", "Active", "Sarah Chen", "CRM Team"),
        _ticket(104, "Import preview shows wrong row count", "Bug", "New", "Sarah Chen", "CRM Team"),
        _ticket(105, "Document import size limits", "Task", "Closed", "Sarah Chen", "CRM Team",
                root_cause="Parser missing extension validation."),
        # Backend — Marcus Lee (spotlight)
        _ticket(201, "Permission API 500 when user has no password", "Defect", "Closed", "Marcus Lee", "Backend"),
        _ticket(202, "Harden order-import auth check", "User Story", "Closed", "Marcus Lee", "Backend",
                root_cause="Null user not handled in validation."),
        _ticket(203, "Token refresh race condition", "Bug", "Active", "Marcus Lee", "Backend"),
        _ticket(204, "Add audit log for permission changes", "Task", "New", "Marcus Lee", "Backend"),
        # Payments — Priya Nair (spotlight)
        _ticket(301, "Webhook refund event not received", "Defect", "Resolved", "Priya Nair", "Payments"),
        _ticket(302, "Retry failed webhook deliveries", "User Story", "Active", "Priya Nair", "Payments"),
        _ticket(303, "Webhook signature validation missing", "Bug", "New", "Priya Nair", "Payments"),
        # Mobile — Unassigned (rolls into Others)
        _ticket(401, "Mobile grid zoom resets on rotate", "Bug", "Closed", "Unassigned", "Mobile"),
        _ticket(402, "Offline cache sync drops edits", "User Story", "Active", "Unassigned", "Mobile"),
        _ticket(403, "Crash on rapid rotate", "Defect", "New", "Unassigned", "Mobile"),
    ]
    summary = ReportSummary(
        headline="Steady week — import & auth fixes landed",
        tldr=["Import pipeline hardened", "Auth edge-cases closed", "Webhook retries in progress"],
        themes=[
            ThemeCluster(name="CSV Import", description="File-upload import flow",
                         ticket_ids=[101, 102, 103, 104, 105]),
            ThemeCluster(name="Auth & Permissions", description="Order-import authorization",
                         ticket_ids=[201, 202, 203, 204]),
            ThemeCluster(name="Webhooks", description="Payment webhook delivery",
                         ticket_ids=[301, 302, 303]),
        ],
        risks=["CSV duplicate-sync still under review", "Token-refresh race not yet root-caused"],
        wins=["XLSX import validated end-to-end", "Order-import auth hardened",
              "Refund webhook flow verified", "Import wizard column-mapping spiked"],
    )
    f, t = datetime(2026, 5, 19), datetime(2026, 5, 23)
    return build_report_html(
        "Weekly Report — 2026-05-19 to 2026-05-23", f, t,
        "Boards: CRM Team, Backend, Payments, Mobile", tickets,
        theme=theme, summary=summary, team_name=team_name, boards=[],
    )
