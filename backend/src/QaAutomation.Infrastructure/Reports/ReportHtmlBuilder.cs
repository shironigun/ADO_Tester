using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Self-contained themed HTML report (spec B7). Ports the frozen building blocks — board-name
/// derivation (2nd area-path segment), closed-state set, type buckets, the <c>window.__REPORT__</c>
/// modal payload with the security-critical <c>&lt;/</c>→<c>&lt;\/</c> escape, <c>data-theme</c>/<c>data-mode</c>
/// body attributes, and the cover/TL;DR/by-type/by-person/by-board/themes/risks/wins/spotlight sections.
/// <para>NOTE: this is a faithful-CORE port. The exhaustive 1506-line presentation (every slide
/// micro-literal, the present-mode keyboard/fullscreen/speaker-notes JS) is the B10 golden-file
/// verification task against the Python output — pending (SAC blocks running the diff here).</para>
/// </summary>
public sealed partial class ReportHtmlBuilder : IReportHtmlBuilder
{
    private static readonly IReadOnlySet<string> ClosedStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "closed", "done", "resolved", "completed" };

    public string Build(
        string title, DateTime fromUtc, DateTime toUtc, string boardsLabel, IReadOnlyList<ReportTicket> tickets,
        ReportTheme theme, ReportSummary? summary, string teamName, IReadOnlyList<string> boards)
    {
        summary ??= new ReportSummary();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{H(title)}</title><style>{Css()}</style></head>");
        sb.Append($"<body data-theme=\"{theme.ToString().ToLowerInvariant()}\" data-mode=\"explore\">");

        // ── Cover ──
        var eyebrow = string.IsNullOrWhiteSpace(teamName) ? "QA Report" : H(teamName);
        sb.Append("<header class=\"cover\">");
        sb.Append($"<div class=\"eyebrow\">{eyebrow}</div>");
        sb.Append($"<h1>{H(title)}</h1>");
        sb.Append($"<div class=\"daterange\">{fromUtc:MMM dd, yyyy} – {toUtc:MMM dd, yyyy}</div>");
        if (boardsLabel.Length > 0) sb.Append($"<div class=\"boards\">{H(boardsLabel)}</div>");
        if (summary.Headline.Length > 0) sb.Append($"<p class=\"headline\">{H(summary.Headline)}</p>");
        sb.Append($"<div class=\"count\">{tickets.Count} ticket(s)</div></header>");

        // ── TL;DR ──
        if (summary.Tldr.Count > 0)
            sb.Append(Section("tl-dr", "TL;DR", $"<ul>{string.Concat(summary.Tldr.Select(b => $"<li>{H(b)}</li>"))}</ul>"));

        // ── By ticket type ──
        var byType = GroupCount(tickets, t => string.IsNullOrEmpty(t.WorkItemType) ? "Other" : t.WorkItemType);
        sb.Append(Section("by-type", "By Ticket Type", StatTable(byType)));

        // ── By person ──
        var byPerson = GroupCount(tickets, t => string.IsNullOrEmpty(t.AssignedTo) ? "Unassigned" : t.AssignedTo);
        sb.Append(Section("by-person", "By Assignee", StatTable(byPerson)));

        // ── By board ──
        var byBoard = GroupCount(tickets, t => BoardLabel(t.AreaPath));
        sb.Append(Section("by-board", "By Board", StatTable(byBoard)));

        // ── AI themes ──
        if (summary.Themes.Count > 0)
        {
            var body = string.Concat(summary.Themes.Select(t =>
                $"<div class=\"theme\"><h3>{H(t.Name)}</h3><p>{H(t.Description)}</p>" +
                $"<div class=\"ids\">{string.Join(", ", t.TicketIds.Select(i => $"#{i}"))}</div></div>"));
            sb.Append(Section("themes", "AI-Detected Themes", body));
        }

        // ── Risks / Wins ──
        if (summary.Risks.Count > 0)
            sb.Append(Section("risks", "Risks", $"<ul>{string.Concat(summary.Risks.Select(r => $"<li>{H(r)}</li>"))}</ul>"));
        if (summary.Wins.Count > 0)
            sb.Append(Section("wins", "Wins", $"<ul>{string.Concat(summary.Wins.Select(w => $"<li>{H(w)}</li>"))}</ul>"));

        // ── Ticket table (explore) ──
        var rows = new StringBuilder();
        foreach (var t in tickets)
        {
            var closed = IsClosed(t.State);
            var fixMissing = (t.WorkItemType?.ToLowerInvariant() is "bug" or "defect") && closed && string.IsNullOrWhiteSpace(t.RootCause);
            rows.Append($"<tr data-id=\"{t.Id}\" data-type=\"{H(t.WorkItemType)}\" data-person=\"{H(t.AssignedTo ?? "Unassigned")}\" data-state=\"{H(t.State)}\" onclick=\"openTicket({t.Id})\">");
            rows.Append($"<td>#{t.Id}</td><td>{H(t.Title)}</td><td>{H(t.WorkItemType)}</td>");
            rows.Append($"<td>{H(t.State)}</td><td>{H(t.AssignedTo ?? "Unassigned")}</td>");
            rows.Append($"<td>{(fixMissing ? "<span class=\"warn\">⚠ Fix missing</span>" : "")}</td></tr>");
        }
        sb.Append(Section("tickets", "Tickets",
            $"<table class=\"tickets\"><thead><tr><th>ID</th><th>Title</th><th>Type</th><th>State</th><th>Assignee</th><th></th></tr></thead><tbody>{rows}</tbody></table>"));

        // ── Ticket modal + window.__REPORT__ (</ escaped: security, not cosmetic) ──
        var modalData = JsonSerializer.Serialize(new
        {
            tickets = tickets.ToDictionary(t => t.Id.ToString(), t => new
            {
                id = t.Id,
                title = t.Title,
                workItemType = t.WorkItemType,
                state = t.State,
                assignedTo = t.AssignedTo,
                isBug = t.WorkItemType?.ToLowerInvariant() is "bug" or "defect" or "issue",
                descriptionHtml = Sanitize(t.Description),
                rootCauseHtml = Sanitize(t.RootCause),
                comments = t.Comments.Select(c => new { author = c.Author, date = c.Date, html = Sanitize(c.Html) }),
                timeline = t.Timeline.Select(e => new { date = e.Date, actor = e.Actor, kind = e.Kind, detail = e.Detail }),
            }),
        });
        modalData = modalData.Replace("</", "<\\/");
        sb.Append("<div id=\"modal\" class=\"modal\" onclick=\"if(event.target===this)this.style.display='none'\"><div class=\"modal-body\"></div></div>");
        sb.Append($"<script>window.__REPORT__={modalData};{ModalJs()}</script>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    // Faithful port of the Python mock deck (legacy-backend reports/mock.py): a fixed, realistic
    // sample dataset (no ADO, no DB) that exercises EVERY slide — cover, by-board matrix + issues,
    // by-person, themes (modules), risks, wins, per-developer spotlights, and the ticket modal
    // (comments + timeline). 15 tickets across 4 boards / 4 assignees, 3 themes.
    public string BuildMock(ReportTheme theme, string teamName)
    {
        TicketComment C(string a, string d, string h) => new(a, d, h);
        ReportTicket T(int id, string title, string type, string state, string who, string board, string? rca = null,
            List<TicketComment>? comments = null, List<TimelineEvent>? timeline = null)
        {
            var closed = ClosedStates.Contains(state);
            var desc = $"<div>Sample description for <b>{title}</b>. Repro steps and expected vs actual would appear here.</div>";
            return new ReportTicket
            {
                Id = id, Title = title, WorkItemType = type, State = state, AssignedTo = who,
                IssueDescription = desc, Fix = closed ? "Resolved in build 2026.05.3." : null,
                ClosedAt = closed ? "2026-05-22T10:00:00Z" : null,
                CreatedBy = "Sample User", CreatedDate = "2026-05-19T09:00:00Z",
                Description = desc, RootCause = rca, AreaPath = "Your Project\\" + board,
                Comments = comments ?? [], Timeline = timeline ?? [],
            };
        }

        List<TicketComment> cmts =
        [
            C("Sample User", "2026-05-20T11:00:00Z", "<div>Reproduced on staging — looks like the parser rejects non-CSV extensions.</div>"),
            C("Marcus Lee", "2026-05-21T14:30:00Z", "<div>Fix deployed to QA, please re-verify the XLSX path.</div>"),
        ];
        List<TimelineEvent> tl =
        [
            new("2026-05-19T09:00:00Z", "Sample User", "created", "Work item created"),
            new("2026-05-19T09:05:00Z", "Sample User", "state", "State New → Active"),
            new("2026-05-20T11:00:00Z", "Sample User", "assignment", "Assigned to Unassigned → Sample User"),
            new("2026-05-20T11:00:00Z", "Sample User", "comment", "Comment added"),
            new("2026-05-22T10:00:00Z", "Marcus Lee", "state", "State Active → Resolved"),
        ];

        var tickets = new List<ReportTicket>
        {
            // Your Team — Sample User (spotlight)
            T(101, "CSV import rejects valid XLSX files", "Bug", "Resolved", "Sample User", "Your Team", comments: cmts, timeline: tl),
            T(102, "Duplicate customers created on re-import", "Defect", "Closed", "Sample User", "Your Team"),
            T(103, "Add column-mapping step to import wizard", "User Story", "Active", "Sample User", "Your Team"),
            T(104, "Import preview shows wrong row count", "Bug", "New", "Sample User", "Your Team"),
            T(105, "Document import size limits", "Task", "Closed", "Sample User", "Your Team", rca: "Parser missing extension validation."),
            // Backend — Marcus Lee (spotlight)
            T(201, "Permission API 500 when user has no password", "Defect", "Closed", "Marcus Lee", "Backend"),
            T(202, "Harden order-import auth check", "User Story", "Closed", "Marcus Lee", "Backend", rca: "Null user not handled in validation."),
            T(203, "Token refresh race condition", "Bug", "Active", "Marcus Lee", "Backend"),
            T(204, "Add audit log for permission changes", "Task", "New", "Marcus Lee", "Backend"),
            // Payments — Priya Nair (spotlight)
            T(301, "Webhook refund event not received", "Defect", "Resolved", "Priya Nair", "Payments"),
            T(302, "Retry failed webhook deliveries", "User Story", "Active", "Priya Nair", "Payments"),
            T(303, "Webhook signature validation missing", "Bug", "New", "Priya Nair", "Payments"),
            // Mobile — Unassigned (rolls into Others)
            T(401, "Mobile grid zoom resets on rotate", "Bug", "Closed", "Unassigned", "Mobile"),
            T(402, "Offline cache sync drops edits", "User Story", "Active", "Unassigned", "Mobile"),
            T(403, "Crash on rapid rotate", "Defect", "New", "Unassigned", "Mobile"),
        };
        var summary = new ReportSummary
        {
            Headline = "Steady week — import & auth fixes landed",
            Tldr = ["Import pipeline hardened", "Auth edge-cases closed", "Webhook retries in progress"],
            Themes =
            [
                new ThemeCluster { Name = "CSV Import", Description = "File-upload import flow", TicketIds = [101, 102, 103, 104, 105] },
                new ThemeCluster { Name = "Auth & Permissions", Description = "Order-import authorization", TicketIds = [201, 202, 203, 204] },
                new ThemeCluster { Name = "Webhooks", Description = "Payment webhook delivery", TicketIds = [301, 302, 303] },
            ],
            Risks = ["CSV duplicate-sync still under review", "Token-refresh race not yet root-caused"],
            Wins =
            [
                "XLSX import validated end-to-end", "Order-import auth hardened",
                "Refund webhook flow verified", "Import wizard column-mapping spiked",
            ],
        };
        return Build("Weekly Report — 2026-05-19 to 2026-05-23", new DateTime(2026, 5, 19), new DateTime(2026, 5, 23),
            "Boards: Your Team, Backend, Payments, Mobile", tickets, theme, summary, teamName, []);
    }

    // ── frozen helpers ─────────────────────────────────────────────────────────

    internal static bool IsClosed(string? state) => state is not null && ClosedStates.Contains(state);

    /// <summary>Board = SECOND area-path segment (project root is first); fallback first, else "Other".</summary>
    internal static string BoardLabel(string? areaPath)
    {
        if (string.IsNullOrWhiteSpace(areaPath))
            return "Other";
        var parts = areaPath.Replace("/", "\\").Split('\\').Where(p => p.Trim().Length > 0).Select(p => p.Trim()).ToList();
        if (parts.Count >= 2) return parts[1];
        return parts.Count > 0 ? parts[0] : "Other";
    }

    private static List<(string Key, int Count)> GroupCount(IReadOnlyList<ReportTicket> items, Func<ReportTicket, string> key)
        => items.GroupBy(key).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();

    private static string StatTable(List<(string Key, int Count)> rows)
        => $"<table class=\"stat\"><tbody>{string.Concat(rows.Select(r => $"<tr><td>{H(r.Key)}</td><td class=\"n\">{r.Count}</td></tr>"))}</tbody></table>";

    private static string Section(string id, string heading, string body)
        => $"<section class=\"slide\" data-slide=\"{id}\"><h2>{H(heading)}</h2>{body}</section>";

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string? Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? null : ScriptRegex().Replace(html, "");

    private static string Css() =>
        ":root{--bg:#fff;--fg:#1a1a1a;--muted:#666;--accent:#1565c0;--card:#f6f7f9;--line:#e2e5e9}" +
        "body[data-theme=standup]{--bg:#0f172a;--fg:#e2e8f0;--muted:#94a3b8;--accent:#38bdf8;--card:#1e293b;--line:#334155}" +
        "body[data-theme=branded]{--bg:#fff;--fg:#102a43;--muted:#486581;--accent:#0a6cff;--card:#f0f4f8;--line:#d9e2ec}" +
        "*{box-sizing:border-box}body{margin:0;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--fg);line-height:1.5}" +
        ".cover{padding:48px 40px;border-bottom:3px solid var(--accent)}.eyebrow{color:var(--accent);font-weight:700;text-transform:uppercase;letter-spacing:.08em;font-size:13px}" +
        ".cover h1{font-size:34px;margin:10px 0}.daterange{color:var(--muted);font-size:15px}.boards{color:var(--muted);font-size:13px;margin-top:4px}.headline{font-size:18px;margin-top:16px}.count{margin-top:8px;color:var(--muted)}" +
        ".slide{padding:28px 40px;border-bottom:1px solid var(--line)}.slide h2{font-size:22px;margin:0 0 14px}" +
        "ul{margin:0;padding-left:20px}li{margin:4px 0}" +
        "table{border-collapse:collapse;width:100%}.stat td{padding:6px 10px;border-bottom:1px solid var(--line)}.stat .n{text-align:right;font-variant-numeric:tabular-nums;font-weight:600}" +
        "table.tickets th,table.tickets td{padding:8px 10px;border-bottom:1px solid var(--line);text-align:left;font-size:14px}table.tickets tr[data-id]{cursor:pointer}table.tickets tr[data-id]:hover{background:var(--card)}" +
        ".theme{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 14px;margin:8px 0}.theme h3{margin:0 0 4px}.theme .ids{color:var(--muted);font-size:12px;margin-top:6px}" +
        ".warn{color:#c62828;font-weight:600}" +
        ".modal{display:none;position:fixed;inset:0;background:rgba(0,0,0,.5);padding:5vh 5vw;overflow:auto}.modal-body{background:var(--bg);color:var(--fg);max-width:760px;margin:0 auto;border-radius:10px;padding:24px}";

    private static string ModalJs() =>
        "function openTicket(id){var t=(window.__REPORT__.tickets||{})[id];if(!t)return;" +
        "var m=document.getElementById('modal'),b=m.querySelector('.modal-body');" +
        "var h='<h2>#'+t.id+' — '+esc(t.title)+'</h2><p>'+esc(t.workItemType)+' · '+esc(t.state)+' · '+esc(t.assignedTo||'Unassigned')+'</p>';" +
        "if(t.descriptionHtml)h+='<div class=\"desc\">'+t.descriptionHtml+'</div>';" +
        "if(t.isBug&&t.rootCauseHtml)h+='<h3>Root cause</h3><div>'+t.rootCauseHtml+'</div>';" +
        "if(t.comments&&t.comments.length){h+='<h3>Comments</h3>';t.comments.forEach(function(c){h+='<div class=\"cmt\"><b>'+esc(c.author)+'</b> <span>'+esc(c.date||'')+'</span>'+(c.html||'')+'</div>';});}" +
        "if(t.timeline&&t.timeline.length){h+='<h3>Timeline</h3><ul>';t.timeline.forEach(function(e){h+='<li>'+esc((e.date||'')+' — '+e.actor+': '+e.detail)+'</li>';});h+='</ul>';}" +
        "b.innerHTML=h;m.style.display='block';}" +
        "function esc(s){return String(s==null?'':s).replace(/[&<>\"']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}[c];});}";

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();
}
