using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Self-contained themed HTML report (spec B7). Faithful, high-fidelity port of the Python
/// <c>build_report_html</c> (legacy-backend reports/html_builder.py): the Explore/Present mode toggle,
/// the present-mode slide deck (cover, tabbed by-board panels with inline donut SVG + issue cards,
/// by-person bars, theme module cards, risks, wins, per-person spotlights with the &gt;=3-ticket
/// threshold + Others rollup, closing slide, slide counter, speaker notes), the explore-mode scrollable
/// filterable page, the ticket modal DOM + the <c>window.__REPORT__</c> data script (with the
/// security-critical <c>&lt;/</c>→<c>&lt;\/</c> escape), and the FULL inlined CSS + JS.
/// </summary>
public sealed partial class ReportHtmlBuilder : IReportHtmlBuilder
{
    private static readonly string[] Palette =
    [
        "#1565c0", "#2e7d32", "#ef6c00", "#c62828", "#6a1b9a",
        "#00838f", "#558b2f", "#4527a0", "#37474f",
    ];

    private static readonly IReadOnlySet<string> ClosedStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "closed", "done", "resolved", "completed" };

    private static readonly (string Key, string Label)[] TypeMeta =
    [
        ("story", "Story"), ("bug", "Bug"), ("defect", "Defect"), ("task", "Task"),
    ];

    public string Build(
        string title, DateTime fromUtc, DateTime toUtc, string boardsLabel, IReadOnlyList<ReportTicket> tickets,
        ReportTheme theme, ReportSummary? summary, string teamName, IReadOnlyList<string> boards)
    {
        summary ??= new ReportSummary();

        var total = tickets.Count;
        var byType = GroupCount(tickets, t => string.IsNullOrEmpty(t.WorkItemType) ? "Unknown" : t.WorkItemType);
        var byPerson = GroupCount(tickets, t => string.IsNullOrEmpty(t.AssignedTo) ? "Unassigned" : t.AssignedTo);
        var boardDetails = BoardDetails(tickets);

        var coverBoards = boards.Count > 0
            ? boards.Select(BoardLabel).ToList()
            : boardDetails.Select(d => d.Board).Take(6).ToList();

        var eyebrow = string.IsNullOrWhiteSpace(teamName)
            ? "Weekly Report"
            : $"{teamName.Trim()} · Weekly Report";

        var noFixTickets = tickets.Where(t => string.IsNullOrWhiteSpace(t.Fix)).ToList();
        var openCritical = tickets.Where(t =>
            !IsClosed(t.State) &&
            ((t.WorkItemType ?? "").ToLowerInvariant() is "bug" or "issue")).ToList();

        // group_by_person preserving first-seen insertion order (defaultdict(list) semantics).
        var groupedByPerson = new Dictionary<string, List<ReportTicket>>();
        var personOrder = new List<string>();
        foreach (var t in tickets)
        {
            var key = string.IsNullOrEmpty(t.AssignedTo) ? "Unassigned" : t.AssignedTo;
            if (!groupedByPerson.TryGetValue(key, out var list))
            {
                list = [];
                groupedByPerson[key] = list;
                personOrder.Add(key);
            }
            list.Add(t);
        }

        // Per-person spotlights — only people with >=3 tickets get a slide; rest roll up.
        var spotlights = new List<(string Person, List<ReportTicket> Items)>();
        var others = new List<ReportTicket>();
        var orderedPeople = personOrder
            .Select((p, i) => (Person: p, Items: groupedByPerson[p], Index: i))
            .OrderByDescending(x => x.Items.Count)
            .ThenBy(x => x.Index)
            .ToList();
        foreach (var (person, items, _) in orderedPeople)
        {
            if (items.Count >= 3 && spotlights.Count < 6)
                spotlights.Add((person, items));
            else
                others.AddRange(items);
        }
        if (others.Count > 0)
            spotlights.Add(($"Others ({others.Count})", others));

        var validThemes = summary.Themes
            .Where(c => !string.IsNullOrEmpty(c.Name) && (c.TicketIds.Count > 0 || !string.IsNullOrEmpty(c.Description)))
            .ToList();

        var themeAttr = theme.ToString().ToLowerInvariant();
        var outp = new StringBuilder();
        outp.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        outp.Append($"<title>{H(title)}</title>");
        outp.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        outp.Append($"<style>{BaseCss}{ThemeCss}</style></head>");
        outp.Append($"<body data-mode=\"explore\" data-theme=\"{themeAttr}\">");

        // Header — mode toggle only.
        outp.Append("<header class=\"hdr\"><div class=\"hdr-spacer\"></div>");
        outp.Append(
            "<div class=\"toggle\">" +
            "<button id=\"btn-explore\" class=\"active\">Explore</button>" +
            "<button id=\"btn-present\" title=\"Present mode · ← → to navigate · Space or Tab for fullscreen · Esc to exit\">Present</button>" +
            "</div></header>");

        // Build the ordered slide list dynamically — empty sections skip themselves.
        var slides = new List<(string Key, string Body)>
        {
            ("cover", SlideCover(eyebrow, fromUtc, toUtc, coverBoards, total)),
        };
        if (boardDetails.Count > 0)
            slides.Add(("overview", SlideBoard(boardDetails)));
        if (byPerson.Count > 1)
            slides.Add(("by_person", SlideByPerson(byPerson, total)));
        if (validThemes.Count > 0)
            slides.Add(("themes", SlideThemes(validThemes)));
        if (summary.Risks.Count > 0 || noFixTickets.Count > 0 || openCritical.Count > 0)
            slides.Add(("risks", SlideRisks(summary, noFixTickets, openCritical)));
        if (summary.Wins.Count > 0)
            slides.Add(("wins", SlideWins(summary)));
        for (var spotI = 0; spotI < spotlights.Count; spotI++)
        {
            var (person, items) = spotlights[spotI];
            slides.Add(($"spot_{person}", SlideSpotlight(person, items, spotI)));
        }
        slides.Add(("closing", SlideClosing(summary, total)));

        // Present-mode section
        outp.Append("<section class=\"present\">");
        for (var i = 0; i < slides.Count; i++)
        {
            var (key, body) = slides[i];
            var scriptKey = key.StartsWith("spot_", StringComparison.Ordinal) ? key.Split('_')[0] : key;
            var script = summary.SpeakerScript.GetValueOrDefault(scriptKey, "");
            if (string.IsNullOrEmpty(script) && key == "overview")
                script = summary.SpeakerScript.GetValueOrDefault("tldr", "") is { Length: > 0 } tldrNote
                    ? tldrNote
                    : summary.SpeakerScript.GetValueOrDefault("by_type", "");
            var counter = $"<div class=\"slide-counter\">{i + 1} / {slides.Count}</div>";
            var speaker = !string.IsNullOrEmpty(script)
                ? $"<aside class=\"speaker\"><h4>Speaker notes</h4><p>{H(script)}</p></aside>"
                : "";
            outp.Append($"<div class=\"slide\" data-key=\"{H(key)}\">{counter}{body}{speaker}</div>");
        }
        outp.Append("</section>");

        // Explore-mode section
        outp.Append("<section class=\"explore\">");
        outp.Append(Filters(byType, byPerson));
        if (summary.Tldr.Count > 0 || !string.IsNullOrEmpty(summary.Headline))
            outp.Append(ExploreSummary(summary));
        if (byType.Count > 0)
            outp.Append(ExploreSection("By Ticket Type", DonutWithCards(byType, total)));
        if (byPerson.Count > 0)
            outp.Append(ExploreSection("By Assigned Person", Bars(byPerson, total)));
        if (validThemes.Count > 0)
            outp.Append(ExploreSection("AI-Detected Themes", ExploreThemes(validThemes)));
        outp.Append(ExploreSection("Tickets", RenderTicketList(personOrder.Select(p => (p, groupedByPerson[p])))));
        outp.Append("</section>");

        // Spotlight ticket modal: inlined data + dialog shell.
        outp.Append(ModalDom());
        outp.Append(ModalDataScript(tickets, spotlights, validThemes));

        outp.Append($"<script>{Js}</script></body></html>");
        return outp.ToString();
    }

    // Faithful port of the Python mock deck: a fixed, realistic sample dataset (no ADO, no DB) that
    // exercises EVERY slide — cover, by-board matrix + issues, by-person, themes (modules), risks, wins,
    // per-developer spotlights, and the ticket modal (comments + timeline). 15 tickets across 4 boards /
    // 4 assignees, 3 themes.
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
                Description = desc, RootCause = rca, AreaPath = "Sample Area\\" + board,
                Comments = comments ?? [], Timeline = timeline ?? [],
            };
        }

        List<TicketComment> cmts =
        [
            C("Sample User", "2026-05-20T11:00:00Z", "<div>Reproduced on staging — looks like the parser rejects non-CSV extensions.</div>"),
            C("Sample User", "2026-05-21T14:30:00Z", "<div>Fix deployed to QA, please re-verify the XLSX path.</div>"),
        ];
        List<TimelineEvent> tl =
        [
            new("2026-05-19T09:00:00Z", "Sample User", "created", "Work item created"),
            new("2026-05-19T09:05:00Z", "Sample User", "state", "State New → Active"),
            new("2026-05-20T11:00:00Z", "Sample User", "assignment", "Assigned to Unassigned → Sample User"),
            new("2026-05-20T11:00:00Z", "Sample User", "comment", "Comment added"),
            new("2026-05-22T10:00:00Z", "Sample User", "state", "State Active → Resolved"),
        ];

        var tickets = new List<ReportTicket>
        {
            // Sample Board — Sample User (spotlight)
            T(101, "CSV import rejects valid XLSX files", "Bug", "Resolved", "Sample User", "Sample Board", comments: cmts, timeline: tl),
            T(102, "Duplicate customers created on re-import", "Defect", "Closed", "Sample User", "Sample Board"),
            T(103, "Add column-mapping step to import wizard", "User Story", "Active", "Sample User", "Sample Board"),
            T(104, "Import preview shows wrong row count", "Bug", "New", "Sample User", "Sample Board"),
            T(105, "Document import size limits", "Task", "Closed", "Sample User", "Sample Board", rca: "Parser missing extension validation."),
            // Backend — Sample User (spotlight)
            T(201, "Permission API 500 when user has no password", "Defect", "Closed", "Sample User", "Backend"),
            T(202, "Harden order-import auth check", "User Story", "Closed", "Sample User", "Backend", rca: "Null user not handled in validation."),
            T(203, "Token refresh race condition", "Bug", "Active", "Sample User", "Backend"),
            T(204, "Add audit log for permission changes", "Task", "New", "Sample User", "Backend"),
            // Payments — Sample User (spotlight)
            T(301, "Webhook refund event not received", "Defect", "Resolved", "Sample User", "Payments"),
            T(302, "Retry failed webhook deliveries", "User Story", "Active", "Sample User", "Payments"),
            T(303, "Webhook signature validation missing", "Bug", "New", "Sample User", "Payments"),
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
            "Boards: Sample Board, Backend, Payments, Mobile", tickets, theme, summary, teamName, []);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Slide builders (Present mode)
    // ════════════════════════════════════════════════════════════════════════════

    private static string SlideCover(string eyebrow, DateTime fromUtc, DateTime toUtc, List<string> coverBoards, int total)
    {
        var parts = new StringBuilder("<div class=\"slide-body slide-cover\">");
        parts.Append($"<div class=\"cover-eyebrow\">{H(eyebrow)}</div>");
        parts.Append(
            "<div class=\"cover-hero\">" +
            $"<span class=\"cover-hero-num\">{total}</span>" +
            "<span class=\"cover-hero-lbl\">tickets</span>" +
            "</div>");
        parts.Append(
            "<div class=\"cover-dates\">" +
            $"<div class=\"cover-date-row\"><span class=\"cdr-k\">Start</span>" +
            $"<span class=\"cdr-v\">{fromUtc.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture)}</span></div>" +
            $"<div class=\"cover-date-row\"><span class=\"cdr-k\">End</span>" +
            $"<span class=\"cdr-v\">{toUtc.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture)}</span></div>" +
            "</div>");
        if (coverBoards.Count > 0)
        {
            var chips = string.Concat(coverBoards.Select(b => $"<span class=\"board-chip\">{H(b)}</span>"));
            parts.Append($"<div class=\"cover-boards\">{chips}</div>");
        }
        parts.Append("</div>");
        return parts.ToString();
    }

    private static string BoardPanel(BoardDetail d, int idx)
    {
        var cards = string.Concat(TypeMeta.Select(tm =>
        {
            var (k, lbl) = tm;
            var tt = d.Types[k];
            return "<div class=\"tc-card\" data-kind=\"" + k + "\">" +
                   "<div class=\"tc-main\">" +
                   $"<div class=\"tc-h\">{lbl}</div>" +
                   $"<div class=\"tc-n\">{tt.N}</div>" +
                   "</div>" +
                   "<div class=\"tc-split\">" +
                   $"<span class=\"tc-st open\"><b>{tt.O}</b> open</span>" +
                   $"<span class=\"tc-st closed\"><b>{tt.C}</b> closed</span>" +
                   "</div>" +
                   "</div>";
        }));
        var bt = TypeMeta
            .Select(tm => (Label: tm.Label, N: d.Types[tm.Key].N))
            .Where(x => x.N != 0)
            .ToList();
        var donut = d.All != 0
            ? DonutSvg(bt.Select(x => (x.Label, x.N)).ToList(), d.All)
            : "<div class=\"chart-empty\">No data</div>";
        var legend = string.Concat(bt.Select((x, i) =>
            $"<li><span class=\"dot\" style=\"background:{Palette[i % Palette.Length]}\"></span>" +
            $"<span class=\"lg-k\">{H(x.Label)}</span>" +
            $"<span class=\"lg-v\">{(x.N * 100.0 / d.All).ToString("F0", CultureInfo.InvariantCulture)}%</span></li>"));
        var donutBlock =
            "<div class=\"board-type\"><div class=\"board-type-h\">By type</div>" +
            $"{donut}<ul class=\"board-legend\">{legend}</ul></div>";
        var issuesBlock = "";
        if (d.Issues.Count > 0)
        {
            var ic = string.Concat(d.Issues.Select(iss =>
                "<div class=\"issue-card\"><span class=\"issue-ic\">⚠</span>" +
                $"<span class=\"issue-n\">{iss.N}</span>" +
                $"<span class=\"issue-l\">{H(iss.Label)}</span></div>"));
            issuesBlock =
                "<div class=\"issues\"><div class=\"issues-h\">Issues</div>" +
                $"<div class=\"issue-cards\">{ic}</div></div>";
        }
        return
            $"<div class=\"bd-panel{(idx == 0 ? " active" : "")}\" data-board=\"{idx}\">" +
            $"<h2 class=\"slide-h\">{H(d.Board)}</h2>" +
            $"<div class=\"bd-sub\">{d.All} tickets · {d.Open} open · {d.Closed} closed</div>" +
            "<div class=\"board-grid\">" +
            "<div class=\"bd-left\">" +
            $"<div class=\"bd-typecards\">{cards}</div>" +
            $"{issuesBlock}" +
            "</div>" +
            $"{donutBlock}" +
            "</div>" +
            "</div>";
    }

    private static string SlideBoard(List<BoardDetail> boardDetails)
    {
        if (boardDetails.Count == 0)
            return "<div class=\"slide-body wide slide-board\">" +
                   "<div class=\"slide-eyebrow\">This week · By board</div>" +
                   "<div class=\"chart-empty\">No data</div></div>";
        var tabs = string.Concat(boardDetails.Select((d, i) =>
            $"<button class=\"bd-tab{(i == 0 ? " active" : "")}\" data-bt=\"{i}\" type=\"button\">" +
            $"{H(d.Board)}</button>"));
        var panels = string.Concat(boardDetails.Select((d, i) => BoardPanel(d, i)));
        return
            "<div class=\"slide-body wide slide-board\">" +
            "<div class=\"bd-head\">" +
            "<div class=\"slide-eyebrow\">This week · By board</div>" +
            $"<div class=\"bd-tabs\">{tabs}</div>" +
            "</div>" +
            $"{panels}" +
            "</div>";
    }

    private static string SlideByPerson(List<(string Key, int Count)> byPerson, int total)
        =>
        "<div class=\"slide-body\">" +
        "<div class=\"slide-eyebrow\">Breakdown</div>" +
        "<h2 class=\"slide-h\">By Assigned Person</h2>" +
        $"{Bars(byPerson, total)}" +
        "</div>";

    private static string SlideThemes(List<ThemeCluster> themes)
    {
        var cards = new StringBuilder();
        for (var i = 0; i < Math.Min(themes.Count, 6); i++)
        {
            var c = themes[i];
            var n = c.TicketIds.Count;
            cards.Append(
                "<article class=\"module-card\">" +
                $"<h3>{H(c.Name)}</h3>" +
                $"<div class=\"module-n\">{n}</div>" +
                "<div class=\"module-l\">tickets</div>" +
                $"<button class=\"spot-open module-btn\" data-spot=\"theme_{i}\" type=\"button\">" +
                "View detail ▸</button>" +
                "</article>");
        }
        return
            "<div class=\"slide-body wide\">" +
            "<div class=\"slide-eyebrow\">Patterns · By module</div>" +
            "<h2 class=\"slide-h\">Themes the AI surfaced</h2>" +
            $"<div class=\"module-grid\">{cards}</div>" +
            "</div>";
    }

    private static string SlideRisks(ReportSummary summary, List<ReportTicket> noFixTickets, List<ReportTicket> openCritical)
    {
        var sections = new StringBuilder();
        if (summary.Risks.Count > 0)
        {
            var items = string.Concat(summary.Risks.Take(6).Select(x => $"<li>{H(x)}</li>"));
            sections.Append($"<div class=\"risk-block\"><h3>Identified risks</h3><ul>{items}</ul></div>");
        }
        if (noFixTickets.Count > 0)
        {
            var items = string.Concat(noFixTickets.Select(t =>
                $"<li><span class=\"tid\">#{t.Id}</span> {H(t.Title)}</li>"));
            sections.Append(
                "<div class=\"risk-block\">" +
                $"<h3>{noFixTickets.Count} closed without a recorded fix</h3>" +
                $"<ul class=\"tight scroll-list\">{items}</ul></div>");
        }
        if (openCritical.Count > 0)
        {
            var items = string.Concat(openCritical.Select(t =>
                $"<li><span class=\"tid\">#{t.Id}</span> {H(t.Title)} " +
                $"<span class=\"state-pill open\">{H(t.State ?? "")}</span></li>"));
            sections.Append(
                "<div class=\"risk-block\">" +
                $"<h3>{openCritical.Count} open Bug/Issue(s)</h3>" +
                $"<ul class=\"tight scroll-list\">{items}</ul></div>");
        }
        return
            "<div class=\"slide-body wide slide-risks\">" +
            "<div class=\"slide-eyebrow\">Watch list</div>" +
            "<h2 class=\"slide-h\">Risks &amp; Gaps</h2>" +
            sections +
            "</div>";
    }

    private static string SlideWins(ReportSummary summary)
    {
        var cards = string.Concat(summary.Wins.Take(6).Select(w =>
            "<article class=\"win-card\"><span class=\"win-mark\">✦</span>" +
            $"<p>{H(w)}</p></article>"));
        return
            "<div class=\"slide-body wide slide-wins\">" +
            "<div class=\"slide-eyebrow\">Highlights</div>" +
            "<h2 class=\"slide-h\">Wins this week</h2>" +
            $"<div class=\"win-grid\">{cards}</div>" +
            "</div>";
    }

    private static string SlideSpotlight(string person, List<ReportTicket> tickets, int spotIndex)
    {
        var total = tickets.Count;
        var typeDefs = new[] { ("Stories", "story"), ("Bugs", "bug"), ("Defects", "defect"), ("Tasks", "task") };
        var bodyRows = new StringBuilder();
        var totOpen = 0;
        var totClosed = 0;
        foreach (var (label, bucket) in typeDefs)
        {
            var items = tickets.Where(t => TypeBucket(t.WorkItemType) == bucket).ToList();
            var closed = items.Count(t => IsClosed(t.State));
            var opened = items.Count - closed;
            totOpen += opened;
            totClosed += closed;
            bodyRows.Append(
                $"<tr><td class=\"bt-board\">{label}</td>" +
                $"<td class=\"num\">{opened}</td><td class=\"num\">{closed}</td>" +
                $"<td class=\"num\">{items.Count}</td></tr>");
        }
        bodyRows.Append(
            "<tr class=\"bt-total\"><td class=\"bt-board\">Total</td>" +
            $"<td class=\"num\">{totOpen}</td><td class=\"num\">{totClosed}</td>" +
            $"<td class=\"num\">{total}</td></tr>");
        var table =
            "<table class=\"board-table spot-table\"><thead><tr>" +
            "<th>Type</th><th class=\"num\">Open</th><th class=\"num\">Closed</th>" +
            "<th class=\"num\">All</th></tr></thead>" +
            $"<tbody>{bodyRows}</tbody></table>";
        return
            "<div class=\"slide-body wide slide-spot\">" +
            "<div class=\"spot-head\">" +
            "<div>" +
            "<div class=\"slide-eyebrow\">Spotlight</div>" +
            $"<h2 class=\"slide-h\">{H(person)} " +
            $"<span class=\"spot-count\">{total} ticket(s)</span></h2>" +
            "</div>" +
            $"<button class=\"spot-open\" data-spot=\"{spotIndex}\" type=\"button\">" +
            "Open tickets ▸</button>" +
            "</div>" +
            $"{table}" +
            "</div>";
    }

    private static string SlideClosing(ReportSummary summary, int total)
    {
        var bullets = "";
        if (summary.Tldr.Count > 0)
            bullets =
                "<ul class=\"closing-bullets\">" +
                string.Concat(summary.Tldr.Take(3).Select(b => $"<li>{H(b)}</li>")) +
                "</ul>";
        return
            "<div class=\"slide-body slide-cover\">" +
            "<div class=\"cover-eyebrow\">Wrap-up</div>" +
            "<h1 class=\"cover-title\">Questions?</h1>" +
            $"{bullets}" +
            $"<p class=\"cover-sub muted\">{total} ticket(s) reviewed</p>" +
            "</div>";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Explore-mode pieces
    // ════════════════════════════════════════════════════════════════════════════

    private static string ExploreSummary(ReportSummary summary)
    {
        var bullets = string.Concat(summary.Tldr.Select(b => $"<li>{H(b)}</li>"));
        return
            "<div class=\"section ai-summary\">" +
            "<h2>Executive summary</h2>" +
            (!string.IsNullOrEmpty(summary.Headline) ? $"<p class=\"headline\">{H(summary.Headline)}</p>" : "") +
            (bullets.Length > 0 ? $"<ul>{bullets}</ul>" : "") +
            "</div>";
    }

    private static string ExploreSection(string heading, string body)
        => $"<div class=\"section\"><h2>{H(heading)}</h2>{body}</div>";

    private static string Filters(List<(string Key, int Count)> byType, List<(string Key, int Count)> byPerson)
    {
        var typeOpts = string.Concat(byType.Select(kv => $"<option>{H(kv.Key)}</option>"));
        var personOpts = string.Concat(byPerson.Select(kv => $"<option>{H(kv.Key)}</option>"));
        return
            "<div class=\"filters\">" +
            $"<label>Type: <select id=\"f-type\"><option value=\"\">All</option>{typeOpts}</select></label>" +
            $"<label>Person: <select id=\"f-person\"><option value=\"\">All</option>{personOpts}</select></label>" +
            "<label>State: <select id=\"f-state\">" +
            "<option value=\"\">All</option>" +
            "<option value=\"closed\">Closed only</option>" +
            "<option value=\"open\">Open only</option>" +
            "</select></label>" +
            "<input type=\"search\" id=\"f-q\" placeholder=\"Search title / issue / fix\">" +
            "</div>";
    }

    private static string ExploreThemes(List<ThemeCluster> themes)
    {
        var rows = new StringBuilder();
        foreach (var c in themes)
        {
            var ids = string.Join(", ", c.TicketIds.Take(8).Select(i => $"#{i}"));
            var more = c.TicketIds.Count > 8 ? $" +{c.TicketIds.Count - 8}" : "";
            rows.Append(
                "<div class=\"theme-row\">" +
                $"<strong>{H(c.Name)}</strong> " +
                $"<span class=\"theme-count-inline\">({c.TicketIds.Count} ticket(s))</span>" +
                $"<div>{H(c.Description)}</div>" +
                $"<div class=\"theme-ids\">{ids}{more}</div>" +
                "</div>");
        }
        return "<div class=\"theme-list\">" + rows + "</div>";
    }

    private static string RenderTicketList(IEnumerable<(string Person, List<ReportTicket> Items)> grouped)
    {
        var outp = new StringBuilder();
        foreach (var (person, items) in grouped)
        {
            outp.Append(
                $"<div class=\"person-group\"><h3>{H(person)} " +
                $"<span class=\"count\">({items.Count})</span></h3>");
            foreach (var t in items.OrderByDescending(x => x.ClosedAt ?? "", StringComparer.Ordinal))
            {
                var stateClass = IsClosed(t.State) ? "closed" : "open";
                var fix = (t.Fix ?? "").Trim();
                var issue = Sanitize(t.IssueDescription) ?? "<em>none</em>";
                var fixHtml = fix.Length > 0
                    ? $"<div class=\"html\">{Sanitize(fix)}</div>"
                    : "<div class=\"missing\">⚠ Fix missing</div>";
                outp.Append(
                    $"<article class=\"ticket {stateClass}\" " +
                    $"data-type=\"{H(t.WorkItemType)}\" " +
                    $"data-person=\"{H(person)}\" " +
                    $"data-state=\"{stateClass}\">" +
                    $"<header><span class=\"id\">#{t.Id}</span> " +
                    $"<span class=\"type\">{H(t.WorkItemType)}</span> " +
                    $"<span class=\"state {stateClass}\">{H(t.State ?? "")}</span> " +
                    $"<h4>{H(t.Title)}</h4></header>" +
                    "<div class=\"body\">" +
                    $"<div class=\"field\"><b>Issue / Description</b><div class=\"html\">{issue}</div></div>" +
                    $"<div class=\"field\"><b>Fix</b>{fixHtml}</div>" +
                    "</div></article>");
            }
            outp.Append("</div>");
        }
        return outp.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Charts (inline SVG)
    // ════════════════════════════════════════════════════════════════════════════

    private static string DonutWithCards(List<(string Key, int Count)> data, int total)
    {
        if (total == 0)
            return "<div class=\"chart-empty\">No data</div>";
        var parts = new StringBuilder("<div class=\"chart-pair\">");
        parts.Append(DonutSvg(data, total));
        parts.Append("<div class=\"cards\">");
        for (var i = 0; i < data.Count; i++)
        {
            var (k, count) = data[i];
            var pct = count * 100.0 / total;
            var color = Palette[i % Palette.Length];
            parts.Append(
                "<div class=\"card\">" +
                $"<div class=\"k\"><span class=\"dot\" style=\"background:{color}\"></span>{H(k)}</div>" +
                $"<div class=\"v\">{count}</div>" +
                $"<div class=\"p\">{pct.ToString("F1", CultureInfo.InvariantCulture)}%</div>" +
                "</div>");
        }
        parts.Append("</div></div>");
        return parts.ToString();
    }

    private static string DonutSvg(List<(string Key, int Count)> data, int total)
    {
        if (total == 0)
            return "";
        const int size = 220;
        const int r = 80;
        const int cx = 110;
        const int cy = 110;
        var parts = new StringBuilder(
            $"<svg class=\"donut\" viewBox=\"0 0 {size} {size}\" width=\"{size}\" height=\"{size}\">");
        var a = -Math.PI / 2;
        for (var i = 0; i < data.Count; i++)
        {
            var (k, count) = data[i];
            var slice = 2 * Math.PI * count / total;
            var x1 = cx + r * Math.Cos(a);
            var y1 = cy + r * Math.Sin(a);
            var x2 = cx + r * Math.Cos(a + slice);
            var y2 = cy + r * Math.Sin(a + slice);
            var large = slice > Math.PI ? 1 : 0;
            var color = Palette[i % Palette.Length];
            parts.Append(
                $"<path d=\"M{cx},{cy} L{x1.ToString("F2", CultureInfo.InvariantCulture)},{y1.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"A{r},{r} 0 {large} 1 {x2.ToString("F2", CultureInfo.InvariantCulture)},{y2.ToString("F2", CultureInfo.InvariantCulture)} Z\" " +
                $"fill=\"{color}\"><title>{H(k)}: {count}</title></path>");
            a += slice;
        }
        parts.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"50\" fill=\"var(--card-bg)\"/>");
        parts.Append(
            $"<text x=\"{cx}\" y=\"{cy - 6}\" text-anchor=\"middle\" class=\"donut-num\">{total}</text>");
        parts.Append(
            $"<text x=\"{cx}\" y=\"{cy + 14}\" text-anchor=\"middle\" class=\"donut-lbl\">total</text>");
        parts.Append("</svg>");
        return parts.ToString();
    }

    private static string Bars(List<(string Key, int Count)> data, int total)
    {
        if (total == 0)
            return "<div class=\"chart-empty\">No data</div>";
        var rows = new StringBuilder("<div class=\"bars\">");
        for (var i = 0; i < data.Count; i++)
        {
            var (k, count) = data[i];
            var pct = count * 100.0 / total;
            var color = Palette[i % Palette.Length];
            rows.Append(
                "<div class=\"bar-row\">" +
                $"<div class=\"bar-label\">{H(k)}</div>" +
                "<div class=\"bar-track\">" +
                $"<div class=\"bar-fill\" style=\"--bar-w:{pct.ToString("F2", CultureInfo.InvariantCulture)}%; --bar-c:{color}\"></div>" +
                "</div>" +
                $"<div class=\"bar-num\"><b>{count}</b> ({pct.ToString("F1", CultureInfo.InvariantCulture)}%)</div>" +
                "</div>");
        }
        rows.Append("</div>");
        return rows.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Spotlight ticket modal (inlined data + dialog shell)
    // ════════════════════════════════════════════════════════════════════════════

    private static string FmtDate(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        try
        {
            var dto = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            var trimmed = s.Length > 16 ? s[..16] : s;
            return trimmed.Replace("T", " ");
        }
    }

    private static string ModalDataScript(
        IReadOnlyList<ReportTicket> tickets,
        List<(string Person, List<ReportTicket> Items)> spotlights,
        List<ThemeCluster> themes)
    {
        var ticketMap = new Dictionary<string, object>();
        var ticketIds = new HashSet<int>();
        foreach (var t in tickets)
        {
            ticketIds.Add(t.Id);
            var isBug = (t.WorkItemType ?? "").ToLowerInvariant() is "bug" or "defect" or "issue";
            var desc = Sanitize(t.Description) ?? Sanitize(t.IssueDescription) ?? "";
            var root = isBug ? Sanitize(t.RootCause) : null;
            ticketMap[t.Id.ToString(CultureInfo.InvariantCulture)] = new
            {
                id = t.Id,
                title = t.Title ?? "",
                type = t.WorkItemType ?? "",
                assignedTo = string.IsNullOrEmpty(t.AssignedTo) ? "Unassigned" : t.AssignedTo,
                state = t.State ?? "",
                isBug,
                descriptionHtml = desc,
                rootCauseHtml = root ?? "",
                comments = t.Comments.Select(c => new
                {
                    author = c.Author,
                    date = FmtDate(c.Date),
                    html = Sanitize(c.Html) ?? "",
                }).ToList(),
                timeline = t.Timeline.Select(e => new
                {
                    date = FmtDate(e.Date),
                    actor = e.Actor,
                    kind = e.Kind,
                    detail = e.Detail,
                }).ToList(),
            };
        }

        var spots = new Dictionary<string, List<int>>();
        for (var i = 0; i < spotlights.Count; i++)
            spots[i.ToString(CultureInfo.InvariantCulture)] = spotlights[i].Items.Select(t => t.Id).ToList();
        for (var i = 0; i < themes.Count; i++)
            spots[$"theme_{i}"] = themes[i].TicketIds.Where(tid => ticketIds.Contains(tid)).ToList();

        var payload = JsonSerializer.Serialize(
            new { tickets = ticketMap, spots },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        payload = payload.Replace("</", "<\\/");
        return $"<script>window.__REPORT__ = {payload};</script>";
    }

    private static string ModalDom()
        =>
        "<div class=\"modal\" id=\"ticket-modal\" hidden>" +
        "<div class=\"modal-overlay\" data-close=\"1\"></div>" +
        "<div class=\"modal-dialog\" role=\"dialog\" aria-modal=\"true\">" +
        "<button class=\"modal-x\" data-close=\"1\" type=\"button\" aria-label=\"Close\">✕</button>" +
        "<div class=\"modal-body\">" +
        "<aside class=\"modal-list\"><h4>Tickets</h4><ul id=\"modal-ticket-list\"></ul></aside>" +
        "<section class=\"modal-detail\" id=\"modal-detail\">" +
        "<div class=\"md-empty\">Select a ticket</div>" +
        "</section>" +
        "</div></div></div>";

    // ════════════════════════════════════════════════════════════════════════════
    // Core helpers (frozen behaviour — tests depend on these)
    // ════════════════════════════════════════════════════════════════════════════

    internal static bool IsClosed(string? state) => state is not null && ClosedStates.Contains(state);

    private static string? TypeBucket(string? workItemType)
    {
        var t = (workItemType ?? "").ToLowerInvariant();
        return t switch
        {
            "user story" or "story" => "story",
            "bug" => "bug",
            "defect" => "defect",
            "task" => "task",
            _ => null,
        };
    }

    /// <summary>Board = SECOND area-path segment (project root is first); fallback first, else "Other".</summary>
    internal static string BoardLabel(string? areaPath)
    {
        if (string.IsNullOrWhiteSpace(areaPath))
            return "Other";
        var parts = areaPath.Replace("/", "\\").Split('\\').Where(p => p.Trim().Length > 0).Select(p => p.Trim()).ToList();
        if (parts.Count >= 2) return parts[1];
        return parts.Count > 0 ? parts[0] : "Other";
    }

    private sealed class TypeAgg
    {
        public int O { get; set; }
        public int C { get; set; }
        public int N { get; set; }
    }

    private sealed class BoardDetail
    {
        public string Board { get; init; } = "";
        public int All { get; set; }
        public int Open { get; set; }
        public int Closed { get; set; }
        public Dictionary<string, TypeAgg> Types { get; } = new()
        {
            ["story"] = new(), ["bug"] = new(), ["defect"] = new(), ["task"] = new(),
        };
        public int NoRca { get; set; }
        public int OpenBd { get; set; }
        public List<(int N, string Label)> Issues { get; } = [];
    }

    private static List<BoardDetail> BoardDetails(IReadOnlyList<ReportTicket> tickets)
    {
        var agg = new Dictionary<string, BoardDetail>();
        var order = new List<string>();
        foreach (var t in tickets)
        {
            var b = BoardLabel(t.AreaPath);
            if (!agg.TryGetValue(b, out var d))
            {
                d = new BoardDetail { Board = b };
                agg[b] = d;
                order.Add(b);
            }
            var closed = IsClosed(t.State);
            d.All += 1;
            if (closed) d.Closed += 1; else d.Open += 1;
            var bucket = TypeBucket(t.WorkItemType);
            if (bucket != null)
            {
                var tt = d.Types[bucket];
                tt.N += 1;
                if (closed) tt.C += 1; else tt.O += 1;
            }
            if ((t.WorkItemType ?? "").ToLowerInvariant() is "bug" or "defect")
            {
                if (closed && string.IsNullOrWhiteSpace(t.RootCause))
                    d.NoRca += 1;
                if (!closed)
                    d.OpenBd += 1;
            }
        }
        var outp = order.Select(b => agg[b]).OrderByDescending(d => d.All).ToList();
        foreach (var d in outp)
        {
            if (d.NoRca != 0)
                d.Issues.Add((d.NoRca, "Closed Bug/Defect with no Root Cause recorded"));
            if (d.OpenBd != 0)
                d.Issues.Add((d.OpenBd, "Bug/Defect still open"));
        }
        return outp;
    }

    private static List<(string Key, int Count)> GroupCount(IReadOnlyList<ReportTicket> items, Func<ReportTicket, string> key)
        => items.GroupBy(key).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : ScriptRegex().Replace(html, "");

    // ════════════════════════════════════════════════════════════════════════════
    // CSS / JS (inlined — copied verbatim from the Python _BASE_CSS / _THEME_CSS / _JS)
    // ════════════════════════════════════════════════════════════════════════════

    private const string BaseCss = """""

* { box-sizing: border-box; }
body { font-family: var(--font-body); margin:0; color:var(--fg); background:var(--bg); }
.hdr { position:sticky; top:0; z-index:10; display:flex; justify-content:space-between;
       align-items:center; padding:.6rem 1rem; background:var(--hdr-bg);
       border-bottom:1px solid var(--border); color:var(--hdr-fg); }
.hdr .meta { color:var(--muted); margin-left:.5rem; font-weight:400; }
.toggle button { border:1px solid var(--border); background:var(--card-bg); color:var(--fg);
                  padding:.35rem .8rem; cursor:pointer; }
.toggle button.active { background:var(--primary); color:#fff; border-color:var(--primary); }
.toggle button:first-child { border-radius:6px 0 0 6px; }
.toggle button:last-child  { border-radius:0 6px 6px 0; }

body[data-mode="present"] .explore { display:none; }
body[data-mode="explore"] .present { display:none; }
body[data-mode="explore"] #btn-script { opacity:.5; pointer-events:none; }

.explore { padding:1rem; max-width:1200px; margin:0 auto; }
.filters { position:sticky; top:48px; background:var(--bg); padding:.5rem 0 .75rem;
           display:flex; gap:.75rem; flex-wrap:wrap; align-items:center; z-index:5; }
.filters label { display:inline-flex; align-items:center; gap:.4rem; font-size:.9rem; color:var(--muted); }
.filters select, .filters input { padding:.3rem .5rem; border:1px solid var(--border);
                                  border-radius:6px; background:var(--card-bg); color:var(--fg); }
#f-q { min-width:220px; }
.section { background:var(--card-bg); border:1px solid var(--border); border-radius:8px;
           padding:1rem 1.2rem; margin-bottom:1rem; }
.section > h2 { margin:0 0 .8rem; font-size:1.1rem; }
.ai-summary .headline { font-size:1.05rem; font-weight:500; margin:.2rem 0 .6rem; }
.theme-list .theme-row { padding:.5rem 0; border-bottom:1px dashed var(--border); }
.theme-list .theme-row:last-child { border-bottom:none; }
.theme-count-inline { color:var(--muted); font-size:.85rem; margin-left:.4rem; }
.theme-ids { color:var(--muted); font-size:.85rem; margin-top:.2rem; font-family:var(--font-mono); }

.chart-pair { display:flex; gap:1.5rem; flex-wrap:wrap; align-items:center; }
.cards { display:flex; gap:.6rem; flex-wrap:wrap; }
.card { background:var(--card-bg); border:1px solid var(--border); border-radius:8px;
        padding:.6rem .9rem; min-width:140px; }
.card .k { color:var(--muted); font-size:.85rem; display:flex; align-items:center; gap:.4rem; }
.card .v { font-size:1.6rem; font-weight:700; }
.card .p { color:var(--muted); font-size:.85rem; }
.card .dot { width:10px; height:10px; border-radius:2px; display:inline-block; }
.chart-empty { color:var(--muted); font-style:italic; }
.donut-num { font-size:1.6rem; font-weight:700; fill:var(--fg); }
.donut-lbl { font-size:.8rem; fill:var(--muted); }

.bars { display:flex; flex-direction:column; gap:.4rem; }
.bar-row { display:grid; grid-template-columns:180px 1fr 140px; gap:.6rem; align-items:center; }
.bar-label { color:var(--fg); font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.bar-track { background:var(--track-bg); height:14px; border-radius:7px; overflow:hidden; }
.bar-fill { height:100%; border-radius:7px; background:var(--bar-c); width:0;
            animation:barfill 700ms ease-out forwards; }
.bar-num { color:var(--muted); font-size:.9rem; }
@keyframes barfill { to { width:var(--bar-w); } }

.person-group { margin-bottom:1.4rem; }
.person-group h3 { font-size:1rem; margin:1rem 0 .5rem; padding-bottom:.3rem; border-bottom:1px solid var(--border); }
.person-group h3 .count { color:var(--muted); font-weight:400; font-size:.9rem; }
.ticket { background:var(--card-bg); border:1px solid var(--border); border-radius:8px;
          padding:.7rem .9rem; margin-bottom:.6rem; }
.ticket header { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; }
.ticket header .id { color:var(--muted); font-size:.85rem; }
.ticket header .type { background:var(--pill-bg); color:var(--pill-fg);
                        padding:.05rem .5rem; border-radius:999px; font-size:.75rem; }
.ticket header .state { padding:.05rem .5rem; border-radius:999px; font-size:.75rem; }
.ticket header .state.closed { background:var(--closed-bg); color:var(--closed-fg); }
.ticket header .state.open   { background:var(--open-bg);   color:var(--open-fg); }
.ticket h4 { margin:0; font-size:.98rem; flex:1 1 100%; }
.ticket .body { margin-top:.5rem; display:grid; gap:.5rem; grid-template-columns:1fr 1fr; }
.ticket .field b { display:block; color:var(--muted); font-size:.78rem; text-transform:uppercase; margin-bottom:.2rem; }
.ticket .html { font-size:.9rem; line-height:1.4; max-height:200px; overflow:auto;
                padding:.4rem; background:var(--inner-bg); border-radius:4px; }
.ticket .html img { max-width:100%; }
.missing { color:var(--missing-fg); font-weight:500; background:var(--missing-bg);
           padding:.3rem .5rem; border-radius:4px; display:inline-block; }
.ticket.hidden { display:none; }

/* Present mode */
.present { display:flex; flex-direction:column; }
.slide { min-height:calc(100vh - 56px); display:flex; align-items:center; justify-content:center;
         padding:2rem; scroll-snap-align:start; border-bottom:1px solid var(--border);
         position:relative; }
body[data-mode="present"] .present { scroll-snap-type:y mandatory;
                                      overflow-y:auto; height:calc(100vh - 56px); }
.slide-counter { position:absolute; top:1rem; right:1.5rem; color:var(--muted);
                  font-size:.85rem; letter-spacing:.05em; }
.slide-body { width:100%; max-width:1100px; animation:slideIn 400ms ease-out; }
.slide-body.wide { max-width:1300px; }
.slide-eyebrow { color:var(--accent); font-size:.85rem; letter-spacing:.18em;
                  text-transform:uppercase; margin-bottom:.5rem; font-weight:600; }
.slide-h { font-family:var(--font-display); font-size:2.2rem; margin:0 0 1.5rem; line-height:1.15; }
.big-bullets { font-size:1.4rem; line-height:1.5; padding-left:1.2rem; }
.big-bullets li { margin-bottom:.7rem; }
@keyframes slideIn { from { opacity:0; transform:translateY(12px); } to { opacity:1; transform:none; } }

.slide-cover { text-align:center; }
.cover-eyebrow { color:var(--accent); font-size:1rem; letter-spacing:.3em;
                  text-transform:uppercase; margin-bottom:1rem; font-weight:600; }
.cover-title { font-family:var(--font-display); font-size:3rem; margin:0 0 1rem; line-height:1.1; }
.cover-headline { font-size:1.25rem; color:var(--muted); margin:0 auto 2rem; max-width:800px; }
.cover-meta { display:flex; gap:2rem; justify-content:center; align-items:baseline; margin:2rem 0; }
.cover-num { font-family:var(--font-display); font-size:5rem; font-weight:700; color:var(--accent);
              line-height:1; display:block; animation:countup 800ms ease-out; }
.cover-num-lbl { display:block; color:var(--muted); font-size:1rem; text-transform:uppercase;
                  letter-spacing:.2em; margin-top:.3rem; }
.cover-range { font-size:1.2rem; color:var(--fg); }
.cover-sub { color:var(--muted); margin:.5rem 0; }
.cover-sub.muted { font-size:.85rem; }
@keyframes countup { from { opacity:0; transform:scale(.7); } to { opacity:1; transform:scale(1); } }

.theme-grid { display:grid; gap:1rem; grid-template-columns:repeat(auto-fit, minmax(280px, 1fr)); }
.theme-card { background:var(--inner-bg); border:1px solid var(--border); border-radius:8px;
              padding:1rem; }
.theme-card h3 { margin:0 0 .4rem; color:var(--accent); }
.theme-card p { margin:0 0 .6rem; color:var(--muted); font-size:.95rem; }
.theme-examples { list-style:none; padding:0; margin:0; font-size:.85rem; color:var(--fg); }
.theme-examples li { padding:.15rem 0; border-top:1px dashed var(--border); }
.theme-count { color:var(--muted); font-size:.8rem; margin-top:.5rem; text-transform:uppercase;
               letter-spacing:.1em; }

.slide-risks .risk-block { background:var(--inner-bg); border-left:4px solid var(--accent-warn);
                            padding:.8rem 1rem; border-radius:4px; margin-bottom:1rem; }
.slide-risks .risk-block h3 { margin:0 0 .5rem; font-size:1.05rem; color:var(--accent-warn); }
.slide-risks ul { margin:0; padding-left:1.2rem; }
.slide-risks ul.tight li { line-height:1.5; }
.tid { color:var(--muted); margin-right:.3rem; font-family:var(--font-mono); }
.state-pill { padding:.05rem .5rem; border-radius:999px; font-size:.7rem; margin-left:.3rem; }
.state-pill.open { background:var(--open-bg); color:var(--open-fg); }
.state-pill.closed { background:var(--closed-bg); color:var(--closed-fg); }

.slide-wins .big-bullets li::marker { color:var(--accent-good); }

.spot-count { font-size:.95rem; color:var(--muted); font-weight:400; font-family:var(--font-body); }
.spot-list { list-style:none; padding:0; margin:0; display:flex; flex-direction:column; gap:.4rem; }
.spot-ticket { display:flex; gap:.6rem; align-items:center; padding:.5rem .8rem;
                background:var(--inner-bg); border-radius:6px;
                border-left:3px solid var(--border); }
.spot-ticket.closed { border-left-color:var(--accent-good); }
.spot-ticket.open   { border-left-color:var(--accent-warn); }
.spot-ticket .type { background:var(--pill-bg); color:var(--pill-fg);
                      padding:.05rem .5rem; border-radius:999px; font-size:.7rem; }
.spot-ticket .title { flex:1; font-size:.95rem; }
.spot-more { color:var(--muted); padding:.3rem .8rem; font-style:italic; }

.closing-bullets { font-size:1.1rem; color:var(--muted); margin:1rem auto;
                    max-width:700px; text-align:left; }

.speaker { position:fixed; right:1rem; bottom:1rem; max-width:340px;
            background:var(--card-bg); border:1px solid var(--border); border-radius:8px;
            padding:.8rem; box-shadow:0 4px 14px rgba(0,0,0,.18); font-size:.85rem;
            display:none; }
.speaker h4 { margin:0 0 .3rem; font-size:.75rem; text-transform:uppercase;
               letter-spacing:.15em; color:var(--accent); }
.speaker p { margin:0; line-height:1.4; }
body[data-script="on"][data-mode="present"] .speaker { display:block; }

/* Safety: a tall slide body (many people/tickets) must scroll internally, never
   bleed into adjacent slides (the inner lists carry their own max-height/scroll). */
.present .slide { overflow:hidden; }

/* ── Merged overview slide ── */
.slide-overview .overview-grid { display:grid; grid-template-columns:auto 1fr; gap:2.5rem;
                                  align-items:center; }
.slide-overview .donut { width:260px; height:260px; }
.type-table { width:100%; border-collapse:collapse; font-size:1.15rem; }
.type-table th { text-align:left; color:var(--muted); font-size:.8rem; text-transform:uppercase;
                 letter-spacing:.1em; padding:.3rem .6rem; border-bottom:1px solid var(--border); }
.type-table td { padding:.5rem .6rem; border-bottom:1px solid var(--border); }
.type-table td .dot { width:11px; height:11px; border-radius:3px; display:inline-block;
                      margin-right:.5rem; vertical-align:middle; }
.type-table .num { text-align:right; font-variant-numeric:tabular-nums; font-weight:700; }
.type-table .num.muted { color:var(--muted); font-weight:400; }
.overview-bullets { font-size:1.15rem; line-height:1.5; margin:1.5rem 0 0; padding-left:1.2rem;
                    color:var(--fg); }
.overview-bullets li { margin-bottom:.4rem; }

/* ── Bigger By-Person bars (present mode only) ── */
.slide .bars { gap:.9rem; max-height:64vh; overflow:auto; padding-right:.5rem; }
.slide .bar-row { grid-template-columns:minmax(180px, 22vw) 1fr minmax(130px, 12vw); gap:1rem;
                  align-items:center; }
.slide .bar-label { font-size:1.25rem; font-weight:600; }
.slide .bar-track { height:26px; border-radius:13px; }
.slide .bar-fill { border-radius:13px; }
.slide .bar-num { font-size:1.1rem; }
.slide .bar-num b { font-size:1.3rem; }

/* ── Spotlight header + open button + scroll lists ── */
.spot-head { display:flex; align-items:flex-start; justify-content:space-between; gap:1rem; }
.spot-open { border:1px solid var(--accent); background:transparent; color:var(--accent);
             font:inherit; font-size:.95rem; font-weight:600; padding:.5rem 1rem;
             border-radius:8px; cursor:pointer; white-space:nowrap; transition:background .15s,color .15s; }
.spot-open:hover { background:var(--accent); color:#fff; }
.scroll-list { max-height:62vh; overflow:auto; }

/* ── Ticket modal ── */
.modal[hidden] { display:none; }
.modal { position:fixed; inset:0; z-index:100; display:flex; align-items:center; justify-content:center; }
.modal-overlay { position:absolute; inset:0; background:rgba(0,0,0,.55); backdrop-filter:blur(3px); }
.modal-dialog { position:relative; width:min(1100px, 94vw); height:min(82vh, 760px);
                background:var(--card-bg); color:var(--fg); border:1px solid var(--border);
                border-radius:14px; box-shadow:0 24px 70px rgba(0,0,0,.4); overflow:hidden;
                animation:slideIn 220ms ease-out; }
.modal-x { position:absolute; top:.6rem; right:.7rem; z-index:2; border:none; background:transparent;
           color:var(--muted); font-size:1.1rem; cursor:pointer; padding:.3rem .5rem; border-radius:6px; }
.modal-x:hover { background:var(--inner-bg); color:var(--fg); }
.modal-body { display:grid; grid-template-columns:300px 1fr; height:100%; }
.modal-list { border-right:1px solid var(--border); background:var(--inner-bg); overflow:auto;
              padding:1rem .8rem; }
.modal-list h4 { margin:0 0 .6rem; font-size:.75rem; text-transform:uppercase; letter-spacing:.15em;
                 color:var(--muted); }
.modal-list ul { list-style:none; margin:0; padding:0; }
.modal-list li { padding:.55rem .6rem; border-radius:8px; cursor:pointer; margin-bottom:.3rem;
                 border:1px solid transparent; font-size:.9rem; line-height:1.35; }
.modal-list li:hover { background:var(--card-bg); }
.modal-list li.active { background:var(--card-bg); border-color:var(--accent); }
.modal-list li .ml-meta { display:block; color:var(--muted); font-size:.72rem; margin-bottom:.15rem;
                          font-family:var(--font-mono); }
.modal-detail { overflow:auto; padding:1.5rem 1.6rem; }
.md-empty { color:var(--muted); font-style:italic; padding:2rem; text-align:center; }
.md-title { font-family:var(--font-display); font-size:1.5rem; margin:0 .5rem 0 0; line-height:1.2; }
.md-head-meta { display:flex; gap:.6rem; flex-wrap:wrap; align-items:center; margin:.7rem 0 1rem; }
.md-kv { font-size:.85rem; color:var(--muted); }
.md-kv b { color:var(--fg); font-weight:600; }
.md-tabs { display:flex; gap:.4rem; border-bottom:1px solid var(--border); margin-bottom:1rem; }
.md-tab { border:none; background:transparent; color:var(--muted); font:inherit; font-weight:600;
          padding:.5rem .9rem; cursor:pointer; border-bottom:2px solid transparent; margin-bottom:-1px; }
.md-tab.active { color:var(--accent); border-bottom-color:var(--accent); }
.md-pane { display:none; }
.md-pane.active { display:block; }
.md-section-h { font-size:.78rem; text-transform:uppercase; letter-spacing:.1em; color:var(--muted);
                margin:1.2rem 0 .4rem; }
.md-section-h:first-child { margin-top:0; }
.md-html { font-size:.92rem; line-height:1.5; background:var(--inner-bg); border-radius:6px;
           padding:.6rem .8rem; }
.md-html img { max-width:100%; }
.md-comment { border-left:3px solid var(--border); padding:.4rem .8rem; margin-bottom:.7rem; }
.md-comment .md-c-meta { font-size:.78rem; color:var(--muted); margin-bottom:.25rem; }
.md-comment .md-c-meta b { color:var(--fg); }
.md-filter { display:flex; gap:.4rem; flex-wrap:wrap; margin-bottom:1rem; }
.md-chip { border:1px solid var(--border); background:var(--card-bg); color:var(--muted);
           font:inherit; font-size:.8rem; padding:.25rem .7rem; border-radius:999px; cursor:pointer; }
.md-chip.active { background:var(--accent); color:#fff; border-color:var(--accent); }
.md-tl { list-style:none; margin:0; padding:0; }
.md-tl li { display:grid; grid-template-columns:130px 1fr; gap:.8rem; padding:.5rem 0;
            border-bottom:1px dashed var(--border); font-size:.9rem; }
.md-tl .md-tl-date { color:var(--muted); font-family:var(--font-mono); font-size:.78rem; }
.md-tl .md-tl-kind { display:inline-block; font-size:.68rem; text-transform:uppercase;
                     letter-spacing:.08em; padding:.05rem .4rem; border-radius:999px;
                     background:var(--pill-bg); color:var(--pill-fg); margin-right:.5rem; }
.md-muted { color:var(--muted); font-style:italic; }
@media (max-width:680px) {
  .modal-body { grid-template-columns:1fr; }
  .modal-list { max-height:30%; }
}

/* ════════════════════════════════════════════════════════════════════════
   DESIGN REFRESH — editorial telemetry. Later cascade overrides the above.
   ════════════════════════════════════════════════════════════════════════ */
body { background:var(--bg); background-image:var(--bg-grad); background-attachment:fixed;
       -webkit-font-smoothing:antialiased; text-rendering:optimizeLegibility;
       letter-spacing:.005em; }
::selection { background:var(--accent); color:#fff; }
*::-webkit-scrollbar { width:10px; height:10px; }
*::-webkit-scrollbar-thumb { background:var(--border); border-radius:999px;
       border:2px solid transparent; background-clip:content-box; }
*::-webkit-scrollbar-thumb:hover { background:var(--muted); background-clip:content-box; }

/* Header — frosted, refined */
.hdr { padding:.7rem 1.4rem; backdrop-filter:saturate(140%) blur(10px);
       background:var(--hdr-bg); border-bottom:1px solid var(--border); }
.hdr-title strong { font-family:var(--font-display); font-weight:700; letter-spacing:.01em; }
.toggle { display:inline-flex; background:var(--inner-bg); border:1px solid var(--border);
          border-radius:999px; padding:3px; gap:2px; }
.toggle button { border:none; background:transparent; color:var(--muted); font-weight:600;
          font-size:.82rem; letter-spacing:.04em; padding:.4rem .95rem; border-radius:999px;
          transition:background .18s, color .18s; }
.toggle button.active { background:var(--accent); color:#fff; box-shadow:var(--shadow); }
.toggle button:first-child, .toggle button:last-child { border-radius:999px; }

/* Eyebrows — small caps with a leading accent rule */
.slide-eyebrow, .cover-eyebrow { display:inline-flex; align-items:center; gap:.6rem;
       color:var(--accent); font-weight:700; font-size:.72rem; letter-spacing:.22em; }
.slide-eyebrow::before { content:""; width:26px; height:2px; background:var(--accent); border-radius:2px; }

/* Display headings — bigger, tighter */
.slide { padding:3.2rem 4vw; }
.slide-h { font-size:clamp(2rem, 3.4vw, 3.1rem); font-weight:700; letter-spacing:-.015em;
       line-height:1.08; margin:.5rem 0 1.8rem; }
.slide-body { animation:slideIn 520ms cubic-bezier(.2,.7,.2,1) both; }
.slide-counter { font-family:var(--font-mono); font-size:.72rem; letter-spacing:.12em;
       opacity:.6; top:1.3rem; right:1.8rem; }

/* Cover */
.slide-cover { display:flex; flex-direction:column; align-items:center; }
.cover-eyebrow { justify-content:center; font-size:clamp(.95rem, 1.7vw, 1.6rem); letter-spacing:.3em;
       margin-bottom:clamp(.8rem, 1.8vw, 1.8rem); }
.cover-eyebrow::before { width:clamp(28px, 3.5vw, 56px); height:2px; }
.cover-title { font-size:clamp(2.6rem, 6vw, 4.4rem); letter-spacing:-.02em; margin:0 0 1.1rem; }
.cover-headline { font-size:1.2rem; line-height:1.55; max-width:680px; }
.cover-meta { gap:3rem; margin:2.4rem 0 1rem; padding:1.6rem 0;
       border-top:1px solid var(--border); border-bottom:1px solid var(--border); }
.cover-num { font-size:clamp(3.4rem,7vw,5.4rem); color:var(--accent); letter-spacing:-.02em; }
.cover-range { font-family:var(--font-mono); font-size:1.05rem; letter-spacing:.02em; }

/* Cards / sections (explore + reused) */
.section { border-radius:var(--radius); box-shadow:var(--shadow); padding:1.4rem 1.6rem;
       background:var(--card-bg); }
.section > h2 { font-family:var(--font-display); font-size:1.2rem; letter-spacing:-.01em; }
.card { border-radius:14px; box-shadow:var(--shadow); padding:.9rem 1.1rem; transition:transform .18s, box-shadow .18s; }
.card:hover { transform:translateY(-2px); box-shadow:var(--shadow-lg); }
.card .v { font-family:var(--font-display); font-variant-numeric:tabular-nums; }

/* Donut + numerals */
.donut-num { font-family:var(--font-display); font-size:2rem; }
.donut-lbl { letter-spacing:.18em; text-transform:uppercase; font-size:.66rem; }

/* Bars — refined fill with subtle sheen */
.bar-track { background:var(--track-bg); }
.bar-fill { background:linear-gradient(90deg, var(--bar-c), color-mix(in srgb, var(--bar-c) 75%, #fff)); }
.bar-num b { font-family:var(--font-display); }

/* Overview type table */
.slide-overview .overview-grid { gap:3rem; }
.type-table { font-size:1.05rem; }
.type-table tbody tr { transition:background .15s; }
.type-table tbody tr:hover { background:var(--accent-soft); }
.type-table .num { font-family:var(--font-display); }

/* Theme / risk / wins cards */
.theme-card { border-radius:var(--radius); box-shadow:var(--shadow); background:var(--card-bg); }
.theme-card h3 { font-family:var(--font-display); }
.slide-risks .risk-block { background:var(--card-bg); border:1px solid var(--border);
       border-left:4px solid var(--accent-warn); border-radius:12px; box-shadow:var(--shadow); }
.big-bullets { font-size:1.3rem; }
.big-bullets li::marker { color:var(--accent); }

/* ── Spotlight stats board ── */
.slide-spot .spot-head { align-items:center; margin-bottom:.4rem; }
.spot-board { display:grid; grid-template-columns:2.2fr 1fr; gap:2rem; align-items:start; margin-top:1.6rem; }
.spot-board-h { font-size:.72rem; font-weight:700; letter-spacing:.2em; text-transform:uppercase;
       color:var(--muted); margin:0 0 .9rem; }
.stat-grid { display:grid; grid-template-columns:repeat(2, 1fr); gap:1.1rem; }
.stat-grid-2 { grid-template-columns:1fr; }
.stat-card { position:relative; background:var(--card-bg); border:1px solid var(--border);
       border-radius:var(--radius); padding:1.4rem 1.5rem; box-shadow:var(--shadow);
       overflow:hidden; transition:transform .18s, box-shadow .18s; }
.stat-card:hover { transform:translateY(-3px); box-shadow:var(--shadow-lg); }
.stat-card::before { content:""; position:absolute; left:0; top:0; bottom:0; width:5px;
       background:var(--kc, var(--accent)); }
.stat-card[data-kind="story"]  { --kc:var(--k-story); }
.stat-card[data-kind="bug"]    { --kc:var(--k-bug); }
.stat-card[data-kind="defect"] { --kc:var(--k-defect); }
.stat-card[data-kind="task"]   { --kc:var(--k-task); }
.stat-card[data-kind="open"]   { --kc:var(--open-fg); }
.stat-card[data-kind="closed"] { --kc:var(--accent-good); }
.stat-n { font-family:var(--font-display); font-size:2.8rem; font-weight:700; line-height:1;
       font-variant-numeric:tabular-nums; color:var(--fg); }
.stat-l { margin-top:.35rem; font-size:.74rem; font-weight:700; letter-spacing:.14em;
       text-transform:uppercase; color:var(--muted); }
.spot-open { border-radius:999px; box-shadow:var(--shadow); }
.spot-open:hover { box-shadow:var(--shadow-lg); }
@media (max-width:900px) { .spot-board { grid-template-columns:1fr; } }

/* Closing */
.closing-bullets { font-size:1.05rem; }

/* Modal polish */
.modal-overlay { background:rgba(8,10,18,.6); backdrop-filter:blur(5px) saturate(120%); }
.modal-dialog { border-radius:20px; box-shadow:var(--shadow-lg); }
.modal-list h4 { letter-spacing:.2em; }
.modal-list li.active { box-shadow:inset 3px 0 0 var(--accent); }
.md-title { font-size:1.6rem; letter-spacing:-.01em; }
.md-tab.active { border-bottom-width:2px; }
.md-chip { border-radius:999px; transition:background .15s, color .15s, border-color .15s; }

/* ── Cover · big-number hero ── */
.cover-hero { display:flex; flex-direction:column; align-items:center;
       margin:clamp(.6rem,1.6vw,1.6rem) 0 clamp(.9rem,2vw,1.8rem); }
.cover-hero-num { font-family:var(--font-display); font-size:clamp(4rem,11vh,9rem); font-weight:700;
       color:var(--accent); line-height:.9; letter-spacing:-.03em;
       animation:countup 800ms cubic-bezier(.2,.7,.2,1) both; }
.cover-hero-lbl { text-transform:uppercase; letter-spacing:.32em;
       font-size:clamp(.8rem,1.4vw,1.3rem); color:var(--muted); margin-top:clamp(.3rem,.8vw,.8rem); }
.cover-dates { display:flex; flex-direction:column; gap:clamp(.4rem,.8vw,.8rem);
       margin:0 auto clamp(.9rem,2vw,1.8rem);
       border-top:1px solid var(--border); border-bottom:1px solid var(--border);
       padding:clamp(.8rem,1.4vw,1.4rem) 0; width:min(clamp(340px,38vw,560px),86vw); }
.cover-date-row { display:flex; justify-content:space-between; align-items:baseline; gap:2rem; }
.cdr-k { text-transform:uppercase; letter-spacing:.18em; font-size:clamp(.72rem,1.2vw,1.15rem);
       color:var(--muted); font-weight:700; }
.cdr-v { font-family:var(--font-mono); font-size:clamp(1.05rem,1.9vw,1.8rem); color:var(--fg); }
.cover-boards { display:flex; gap:clamp(.5rem,1vw,1rem); flex-wrap:wrap; justify-content:center; }
.board-chip { background:var(--card-bg); border:1px solid var(--border); border-radius:999px;
       padding:clamp(.42rem,.8vw,.7rem) clamp(1rem,1.6vw,1.6rem);
       font-size:clamp(.85rem,1.3vw,1.25rem); font-weight:600; box-shadow:var(--shadow); }

/* ── Board slide · matrix + type donut + issues ── */
.slide-board .board-grid { display:grid; grid-template-columns:minmax(0,2.4fr) minmax(260px,1fr);
       gap:clamp(1.6rem,3vw,3.5rem); align-items:start; }
.board-table { width:100%; border-collapse:collapse; font-size:clamp(1.05rem,1.5vw,1.7rem); }
.board-table th { text-align:left; color:var(--muted); font-size:clamp(.72rem,1vw,1.05rem);
       text-transform:uppercase; letter-spacing:.1em;
       padding:clamp(.5rem,1.3vh,1.1rem) clamp(.5rem,.9vw,1rem); border-bottom:1px solid var(--border); }
.board-table th.num, .board-table td.num { text-align:right; }
.board-table td { padding:clamp(.45rem,1vh,.95rem) clamp(.5rem,.9vw,1rem);
       border-bottom:1px solid var(--border); }
.board-table .num { font-variant-numeric:tabular-nums; font-family:var(--font-display); font-weight:600; }
.board-table .bt-board { font-weight:600; }
.board-table tbody tr:hover { background:var(--accent-soft); }
.board-table .bt-total td { border-top:2px solid var(--border); border-bottom:none; font-weight:700; }
.board-table .bt-total .bt-board { color:var(--accent); }
.board-type { background:var(--card-bg); border:1px solid var(--border); border-radius:var(--radius);
       box-shadow:var(--shadow); padding:clamp(1rem,1.6vh,1.8rem) clamp(1.2rem,1.6vw,2rem);
       text-align:center; }
.board-type-h { font-size:clamp(.75rem,1vw,1.05rem); text-transform:uppercase; letter-spacing:.2em;
       color:var(--muted); font-weight:700; margin-bottom:clamp(.5rem,1vh,1rem); }
.board-type .donut { width:clamp(150px,16vh,240px); height:clamp(150px,16vh,240px); }
.board-legend { list-style:none; margin:clamp(.6rem,1.2vh,1.2rem) 0 0; padding:0; text-align:left; }
.board-legend li { display:flex; align-items:center; gap:.6rem; padding:clamp(.18rem,.4vh,.4rem) 0;
       font-size:clamp(.9rem,1.2vw,1.25rem); }
.board-legend .dot { width:clamp(10px,1vw,14px); height:clamp(10px,1vw,14px); border-radius:3px; }
.board-legend .lg-k { flex:1; }
.board-legend .lg-v { color:var(--muted); font-variant-numeric:tabular-nums; }
.issues { margin-top:clamp(.9rem,2vh,1.8rem); }
.issues-h { font-size:clamp(.75rem,1vw,1.05rem); text-transform:uppercase; letter-spacing:.2em;
       color:var(--accent-warn); font-weight:700; margin-bottom:clamp(.5rem,1.2vh,1rem); }
.issue-cards { display:flex; gap:clamp(.8rem,1.5vw,1.4rem); flex-wrap:wrap; }
.issue-card { display:flex; align-items:center; gap:clamp(.6rem,1.1vw,1rem);
       background:var(--missing-bg); border:1px solid var(--accent-warn);
       border-left:6px solid var(--accent-warn); border-radius:var(--radius);
       box-shadow:0 0 0 1px var(--accent-warn) inset, var(--shadow);
       padding:clamp(.85rem,1.5vh,1.3rem) clamp(1.1rem,1.7vw,1.7rem); }
.issue-ic { color:var(--accent-warn); font-size:clamp(1.1rem,1.5vw,1.5rem); line-height:1; }
.issue-n { font-family:var(--font-display); font-weight:800; color:var(--accent-warn); line-height:1;
       font-variant-numeric:tabular-nums; font-size:clamp(2rem,2.9vw,2.9rem); }
.issue-l { color:var(--missing-fg); font-weight:600; font-size:clamp(.9rem,1.1vw,1.2rem);
       max-width:20ch; line-height:1.25; }

/* Tabbed by-board slide */
.bd-head { display:flex; justify-content:space-between; align-items:center; gap:1.2rem;
       flex-wrap:wrap; margin-bottom:clamp(.9rem,2.2vh,1.8rem); }
.bd-tabs { display:flex; gap:.5rem; flex-wrap:wrap; }
.bd-tab { border:1px solid var(--border); background:var(--card-bg); color:var(--muted); font:inherit;
       font-weight:600; font-size:clamp(.8rem,1vw,1.1rem); padding:clamp(.4rem,.8vh,.6rem) clamp(.9rem,1.4vw,1.3rem);
       border-radius:999px; cursor:pointer; transition:background .15s, color .15s, border-color .15s; }
.bd-tab:hover { border-color:var(--accent); color:var(--accent); }
.bd-tab.active { background:var(--accent); color:#fff; border-color:var(--accent); box-shadow:var(--shadow); }
.bd-panel { display:none; }
.bd-panel.active { display:block; animation:slideIn 360ms ease-out; }
.bd-panel .slide-h { margin:0; }
.bd-sub { color:var(--muted); font-size:clamp(.95rem,1.4vw,1.5rem); font-variant-numeric:tabular-nums;
       margin:clamp(.3rem,.8vh,.6rem) 0 clamp(1rem,2.4vh,2rem); }
.bd-typecards { display:grid; grid-template-columns:1fr 1fr; gap:clamp(.9rem,1.6vw,1.5rem); }
.tc-card { background:var(--card-bg); border:1px solid var(--border);
       border-left:5px solid var(--kc,var(--accent)); border-radius:var(--radius); box-shadow:var(--shadow);
       padding:clamp(.9rem,1.7vh,1.5rem) clamp(1.1rem,1.6vw,1.7rem);
       display:flex; align-items:center; justify-content:space-between; gap:clamp(.8rem,1.4vw,1.4rem); }
.tc-card[data-kind="story"]  { --kc:var(--k-story); }
.tc-card[data-kind="bug"]    { --kc:var(--k-bug); }
.tc-card[data-kind="defect"] { --kc:var(--k-defect); }
.tc-card[data-kind="task"]   { --kc:var(--k-task); }
.tc-h { text-transform:uppercase; letter-spacing:.14em; font-size:clamp(.72rem,1vw,1rem);
       color:var(--muted); font-weight:700; }
.tc-n { font-family:var(--font-display); font-weight:700; line-height:1; margin:clamp(.15rem,.5vh,.4rem) 0;
       font-variant-numeric:tabular-nums; font-size:clamp(2.2rem,3.4vw,3.4rem); }
.tc-split { display:flex; flex-direction:column; gap:clamp(.35rem,.8vh,.6rem); align-items:stretch; }
.tc-st { display:inline-flex; align-items:center; gap:.35rem; border-radius:999px;
       padding:clamp(.25rem,.6vh,.45rem) clamp(.6rem,1vw,.9rem); font-weight:600;
       font-size:clamp(.82rem,1vw,1.1rem); white-space:nowrap; justify-content:center; }
.tc-st b { font-family:var(--font-display); font-size:1.15em; line-height:1; }
.tc-st.open { background:var(--open-bg); color:var(--open-fg); }
.tc-st.closed { background:var(--closed-bg); color:var(--closed-fg); }
@media (max-width:900px) { .slide-board .board-grid { grid-template-columns:1fr; }
       .bd-typecards { grid-template-columns:1fr 1fr; } }
/* Centered vertically; matrix scrolls internally if there are many boards (keeps the
   whole body within the viewport so centering never clips). */
.slide[data-key="overview"] { align-items:center;
       padding-top:clamp(1rem,2.6vh,2.4rem); padding-bottom:clamp(1rem,2vh,2rem); }
.slide-board .board-matrix { max-height:68vh; overflow:auto; }
.slide-board .slide-eyebrow { font-size:clamp(.85rem,1.3vw,1.3rem); }
.slide-board .slide-h { font-size:clamp(2.2rem,4vw,3.6rem); margin:0 0 clamp(.7rem,1.7vh,1.4rem); }

/* ── Module cards (themes) ── */
.module-grid { display:grid; gap:1.2rem; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); }
.module-card { background:var(--card-bg); border:1px solid var(--border); border-radius:var(--radius);
       box-shadow:var(--shadow); padding:1.4rem 1.5rem; display:flex; flex-direction:column;
       transition:transform .18s, box-shadow .18s; }
.module-card:hover { transform:translateY(-3px); box-shadow:var(--shadow-lg); }
.module-card h3 { margin:0; font-family:var(--font-display); font-size:1.15rem; line-height:1.25; }
.module-n { font-family:var(--font-display); font-size:2.8rem; font-weight:700; color:var(--accent);
       line-height:1; margin-top:.6rem; }
.module-l { text-transform:uppercase; letter-spacing:.16em; font-size:.7rem; color:var(--muted);
       margin-bottom:1.1rem; }
.module-btn { margin-top:auto; align-self:flex-start; }

/* ── Wins cards ── */
.win-grid { display:grid; gap:1.1rem; grid-template-columns:repeat(auto-fit,minmax(240px,1fr)); }
.win-card { background:var(--card-bg); border:1px solid var(--border);
       border-left:4px solid var(--accent-good); border-radius:var(--radius); box-shadow:var(--shadow);
       padding:1.2rem 1.3rem; }
.win-card .win-mark { color:var(--accent-good); font-size:1.1rem; }
.win-card p { margin:.4rem 0 0; font-size:1rem; line-height:1.45; }

/* ── Spotlight matrix ── */
.slide-spot .spot-table { max-width:560px; font-size:1.1rem; margin-top:1.4rem; }
.slide-spot .spot-table th, .slide-spot .spot-table td { padding:.7rem .85rem; }
.slide-spot .spot-count { font-size:.95rem; color:var(--muted); font-weight:400;
       font-family:var(--font-body); }
""""";

    private const string ThemeCss = """""

/* ── Executive · light editorial ── */
body[data-theme="executive"] {
  --font-display: 'Iowan Old Style', 'Palatino Linotype', 'Palatino', Georgia, serif;
  --font-body: 'Segoe UI', system-ui, -apple-system, Roboto, sans-serif;
  --font-mono: 'SF Mono', 'Cascadia Mono', Consolas, monospace;
  --bg:#f4f1ea; --fg:#1d1b16; --muted:#6f6a60;
  --card-bg:#fffdf8; --inner-bg:#f4f1ea; --border:#e4ded1; --track-bg:#eae5d9;
  --hdr-bg:rgba(255,253,248,.82); --hdr-fg:#1d1b16;
  --primary:#1d4ed8; --accent:#b1452c; --accent-2:#c98a1e;
  --accent-good:#2f7a55; --accent-warn:#b1452c; --accent-soft:rgba(177,69,44,.10);
  --pill-bg:#ece6d9; --pill-fg:#4a463d;
  --closed-bg:#e3efe6; --closed-fg:#235f41;
  --open-bg:#f6e6dd; --open-fg:#9a3b22;
  --missing-bg:#f6e6dd; --missing-fg:#9a3b22;
  --k-story:#1d4ed8; --k-bug:#b1452c; --k-defect:#7c3aed; --k-task:#6f6a60;
  --shadow:0 1px 2px rgba(40,33,20,.05), 0 10px 28px -14px rgba(40,33,20,.22);
  --shadow-lg:0 24px 60px -20px rgba(40,33,20,.30);
  --radius:16px;
  --bg-grad: radial-gradient(120% 80% at 0% 0%, rgba(177,69,44,.07), transparent 55%),
             radial-gradient(120% 90% at 100% 0%, rgba(29,78,216,.06), transparent 50%);
}

/* ── Standup · dark control-room ── */
body[data-theme="standup"] {
  --font-display: 'Segoe UI', system-ui, -apple-system, sans-serif;
  --font-body: 'Segoe UI', system-ui, -apple-system, sans-serif;
  --font-mono: 'Cascadia Mono', 'SF Mono', Consolas, monospace;
  --bg:#0a0f1d; --fg:#e6edf6; --muted:#8595ab;
  --card-bg:#141c2e; --inner-bg:#0d1322; --border:#243047; --track-bg:#1a2438;
  --hdr-bg:rgba(20,28,46,.78); --hdr-fg:#e6edf6;
  --primary:#22d3ee; --accent:#22d3ee; --accent-2:#a78bfa;
  --accent-good:#34d399; --accent-warn:#fb7185; --accent-soft:rgba(34,211,238,.12);
  --pill-bg:#243047; --pill-fg:#c3cedd;
  --closed-bg:rgba(52,211,153,.16); --closed-fg:#34d399;
  --open-bg:rgba(251,113,133,.16); --open-fg:#fb7185;
  --missing-bg:rgba(251,113,133,.16); --missing-fg:#fb7185;
  --k-story:#22d3ee; --k-bug:#fb7185; --k-defect:#a78bfa; --k-task:#8595ab;
  --shadow:0 1px 2px rgba(0,0,0,.4), 0 14px 34px -16px rgba(0,0,0,.7);
  --shadow-lg:0 30px 70px -22px rgba(0,0,0,.8);
  --radius:16px;
  --bg-grad: radial-gradient(120% 80% at 0% 0%, rgba(34,211,238,.10), transparent 52%),
             radial-gradient(120% 90% at 100% 0%, rgba(167,139,250,.10), transparent 50%);
}
body[data-theme="standup"] .ticket .html, body[data-theme="standup"] .md-html { background:#0a0f1d; }
body[data-theme="standup"] .filters select,
body[data-theme="standup"] .filters input { background:#141c2e; color:#e6edf6; }

/* ── Branded · corporate ── */
body[data-theme="branded"] {
  --font-display: 'Segoe UI', 'Helvetica Neue', Arial, sans-serif;
  --font-body: 'Segoe UI', 'Helvetica Neue', Arial, sans-serif;
  --font-mono: 'SF Mono', Consolas, monospace;
  --bg:#eef3f9; --fg:#0b2f57; --muted:#5a7290;
  --card-bg:#ffffff; --inner-bg:#f1f6fc; --border:#d6e2f0; --track-bg:#e1ebf7;
  --hdr-bg:#0b2f57; --hdr-fg:#ffffff;
  --primary:#0b6cd2; --accent:#0b6cd2; --accent-2:#11a8b8;
  --accent-good:#1f8a5b; --accent-warn:#d1495b; --accent-soft:rgba(11,108,210,.10);
  --pill-bg:#e1ebf7; --pill-fg:#0b3a6f;
  --closed-bg:#e1f0e8; --closed-fg:#1f6f49;
  --open-bg:#fdeaec; --open-fg:#b23545;
  --missing-bg:#fdeaec; --missing-fg:#b23545;
  --k-story:#0b6cd2; --k-bug:#d1495b; --k-defect:#7048c0; --k-task:#5a7290;
  --shadow:0 1px 2px rgba(11,47,87,.06), 0 12px 30px -16px rgba(11,47,87,.25);
  --shadow-lg:0 26px 64px -20px rgba(11,47,87,.32);
  --radius:14px;
  --bg-grad: radial-gradient(120% 80% at 0% 0%, rgba(11,108,210,.08), transparent 55%),
             radial-gradient(120% 90% at 100% 0%, rgba(17,168,184,.07), transparent 50%);
}
body[data-theme="branded"] .hdr .meta { color:#cfd8e3; }
body[data-theme="branded"] .toggle button { background:rgba(255,255,255,.12); color:#fff; border-color:rgba(255,255,255,.3); }
body[data-theme="branded"] .toggle button.active { background:#fff; color:#0b2f57; border-color:#fff; }
body[data-theme="branded"] .slide-cover::before {
  content:"QA ACTIVITY REPORT";
  position:absolute; top:1.5rem; left:0; right:0;
  letter-spacing:.45em; font-size:.7rem; color:var(--accent);
  font-weight:700; text-align:center;
}
body[data-theme="branded"] .slide-cover { padding-top:3rem; }
""""";

    private const string Js = """""

const body = document.body;
document.getElementById('btn-explore').addEventListener('click', () => setMode('explore'));
document.getElementById('btn-present').addEventListener('click', () => setMode('present'));
// By-board tab switcher — show the active board's panel.
document.addEventListener('click', e => {
  const tab = e.target.closest('.bd-tab');
  if (!tab) return;
  const slide = tab.closest('.slide-board'); if (!slide) return;
  const i = tab.dataset.bt;
  slide.querySelectorAll('.bd-tab').forEach(b => b.classList.toggle('active', b === tab));
  slide.querySelectorAll('.bd-panel').forEach(p => p.classList.toggle('active', p.dataset.board === i));
});
function setMode(m) {
  body.dataset.mode = m;
  document.getElementById('btn-explore').classList.toggle('active', m === 'explore');
  document.getElementById('btn-present').classList.toggle('active', m === 'present');
}
function toggleFullscreen() {
  // Browsers reject fullscreen requests not initiated by a user gesture — keydown counts.
  const el = document.documentElement;
  if (!document.fullscreenElement) {
    el.requestFullscreen?.().catch(() => {});
  } else {
    document.exitFullscreen?.().catch(() => {});
  }
}
document.addEventListener('keydown', e => {
  if (body.dataset.mode !== 'present') return;
  const m = document.getElementById('ticket-modal');
  if (m && !m.hidden) return;  // modal owns the keyboard while open
  const slides = [...document.querySelectorAll('.present .slide')];
  const cur = slides.findIndex(s => s.getBoundingClientRect().top >= -10);

  // Navigation: ← ↑ PageUp = prev   |   → ↓ PageDown = next
  if (e.key === 'ArrowRight' || e.key === 'ArrowDown' || e.key === 'PageDown') {
    e.preventDefault();
    slides[Math.min(slides.length - 1, cur + 1)]?.scrollIntoView({ behavior:'smooth' });
  } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp' || e.key === 'PageUp') {
    e.preventDefault();
    slides[Math.max(0, cur - 1)]?.scrollIntoView({ behavior:'smooth' });
  } else if (e.key === ' ' || e.key === 'Tab') {
    // Space or Tab enters fullscreen presentation mode.
    e.preventDefault();
    toggleFullscreen();
  } else if (e.key === 'Escape') {
    // Esc exits fullscreen (browser does it natively in most cases; explicit is safe).
    if (document.fullscreenElement) document.exitFullscreen?.().catch(() => {});
  } else if (e.key === 's' || e.key === 'S') {
    body.dataset.script = body.dataset.script === 'on' ? 'off' : 'on';
  }
});
const fType = document.getElementById('f-type');
const fPerson = document.getElementById('f-person');
const fState = document.getElementById('f-state');
const fQ = document.getElementById('f-q');
function applyFilters() {
  const t = fType.value, p = fPerson.value, st = fState.value;
  const q = (fQ.value || '').toLowerCase();
  document.querySelectorAll('.explore .ticket').forEach(el => {
    let ok = true;
    if (t && el.dataset.type !== t) ok = false;
    if (p && el.dataset.person !== p) ok = false;
    if (st && el.dataset.state !== st) ok = false;
    if (q && !el.textContent.toLowerCase().includes(q)) ok = false;
    el.classList.toggle('hidden', !ok);
  });
  document.querySelectorAll('.explore .person-group').forEach(g => {
    const visible = [...g.querySelectorAll('.ticket')].some(t => !t.classList.contains('hidden'));
    g.style.display = visible ? '' : 'none';
  });
}
[fType, fPerson, fState].forEach(el => el.addEventListener('change', applyFilters));
fQ.addEventListener('input', applyFilters);

// ── Spotlight ticket modal ──
const REPORT = window.__REPORT__ || { tickets: {}, spots: {} };
const modal = document.getElementById('ticket-modal');
const modalList = document.getElementById('modal-ticket-list');
const modalDetail = document.getElementById('modal-detail');
const KIND_LABEL = { created:'Created', state:'State', assignment:'Assignment', comment:'Comments', other:'Other' };
let mdState = { ticket: null, filter: 'all', tab: 'details' };

function esc(s) {
  return (s == null ? '' : String(s))
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}
function modalOpen(spotIndex) {
  const ids = (REPORT.spots[spotIndex] || []);
  if (!ids.length) return;
  modalList.innerHTML = ids.map(id => {
    const t = REPORT.tickets[id] || {};
    return '<li data-id="' + id + '"><span class="ml-meta">#' + id + ' · ' +
      esc(t.type) + ' · ' + esc(t.state) + '</span>' + esc(t.title) + '</li>';
  }).join('');
  modal.hidden = false;
  modalSelect(ids[0]);
}
function modalClose() { modal.hidden = true; }
function modalSelect(id) {
  const t = REPORT.tickets[id];
  if (!t) return;
  mdState = { ticket: t, filter: 'all', tab: 'details' };
  [...modalList.children].forEach(li => li.classList.toggle('active', li.dataset.id == id));
  renderDetail();
}
function renderDetail() {
  const t = mdState.ticket;
  if (!t) return;
  const head =
    '<h2 class="md-title">' + esc(t.title) + '</h2>' +
    '<div class="md-head-meta">' +
      '<span class="md-kv">Type: <b>' + esc(t.type) + '</b></span>' +
      '<span class="md-kv">Assigned to: <b>' + esc(t.assignedTo) + '</b></span>' +
      '<span class="md-kv">Status: <b>' + esc(t.state) + '</b></span>' +
    '</div>' +
    '<div class="md-tabs">' +
      '<button class="md-tab' + (mdState.tab === 'details' ? ' active' : '') + '" data-tab="details" type="button">Details</button>' +
      '<button class="md-tab' + (mdState.tab === 'timeline' ? ' active' : '') + '" data-tab="timeline" type="button">Timeline</button>' +
    '</div>';

  let details = '<div class="md-section-h">Description</div>' +
    '<div class="md-html">' + (t.descriptionHtml || '<span class="md-muted">None</span>') + '</div>';
  if (t.isBug && t.rootCauseHtml) {
    details += '<div class="md-section-h">Root cause</div>' +
      '<div class="md-html">' + t.rootCauseHtml + '</div>';
  }
  details += '<div class="md-section-h">Discussion</div>';
  if (t.comments && t.comments.length) {
    details += t.comments.map(c =>
      '<div class="md-comment"><div class="md-c-meta"><b>' + esc(c.author) + '</b> · ' +
      esc(c.date) + '</div><div class="md-html">' + (c.html || '') + '</div></div>').join('');
  } else {
    details += '<div class="md-muted">No comments</div>';
  }

  const kinds = [...new Set((t.timeline || []).map(e => e.kind))];
  const chips = ['all'].concat(kinds);
  const filterRow = '<div class="md-filter">' + chips.map(k =>
    '<button class="md-chip' + (mdState.filter === k ? ' active' : '') + '" data-filter="' + k + '" type="button">' +
    (k === 'all' ? 'All' : (KIND_LABEL[k] || k)) + '</button>').join('') + '</div>';
  const evs = (t.timeline || []).filter(e => mdState.filter === 'all' || e.kind === mdState.filter);
  const tlBody = evs.length ? '<ul class="md-tl">' + evs.map(e =>
    '<li><span class="md-tl-date">' + esc(e.date) + '</span><span>' +
    '<span class="md-tl-kind">' + (KIND_LABEL[e.kind] || e.kind) + '</span>' +
    esc(e.detail) + ' <span class="md-muted">— ' + esc(e.actor) + '</span></span></li>').join('') + '</ul>'
    : '<div class="md-muted">No events</div>';

  const dAct = mdState.tab === 'details' ? ' active' : '';
  const tAct = mdState.tab === 'timeline' ? ' active' : '';
  modalDetail.innerHTML = head +
    '<div class="md-pane' + dAct + '" data-pane="details">' + details + '</div>' +
    '<div class="md-pane' + tAct + '" data-pane="timeline">' + filterRow + tlBody + '</div>';
}
document.addEventListener('click', e => {
  const openBtn = e.target.closest('.spot-open');
  if (openBtn) { modalOpen(openBtn.dataset.spot); return; }
  if (e.target.closest('[data-close]')) { modalClose(); return; }
  const li = e.target.closest('#modal-ticket-list li');
  if (li) { modalSelect(li.dataset.id); return; }
  const tab = e.target.closest('.md-tab');
  if (tab) { mdState.tab = tab.dataset.tab; renderDetail(); return; }
  const chip = e.target.closest('.md-chip');
  if (chip) { mdState.filter = chip.dataset.filter; renderDetail(); }
});
document.addEventListener('keydown', e => {
  if (!modal.hidden && e.key === 'Escape') { e.stopPropagation(); modalClose(); }
}, true);
""""";

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();
}
