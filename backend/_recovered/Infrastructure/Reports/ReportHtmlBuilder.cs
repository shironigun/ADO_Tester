using System.Text;
using System.Web;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Renders a self-contained HTML report (all CSS/JS/SVG inlined, no external deps).
/// Dual-mode: Present (1→4 full-screen slides) + Explore (scrollable, sticky filters).
/// </summary>
public static class ReportHtmlBuilder
{
    public static string Build(
        string title, DateTime fromUtc, DateTime toUtc, string boardsLabel,
        IReadOnlyList<ReportTicket> tickets)
    {
        // ── stats ─────────────────────────────────────────────────────────────
        var totalCount = tickets.Count;
        var byType = tickets.GroupBy(t => string.IsNullOrWhiteSpace(t.WorkItemType) ? "Unknown" : t.WorkItemType)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToList();
        var byPerson = tickets.GroupBy(t => string.IsNullOrWhiteSpace(t.AssignedTo) ? "Unassigned" : t.AssignedTo!)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToList();

        var groupedByPerson = tickets
            .GroupBy(t => string.IsNullOrWhiteSpace(t.AssignedTo) ? "Unassigned" : t.AssignedTo!)
            .OrderByDescending(g => g.Count())
            .ToList();

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append($"<title>{H(title)}</title>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<style>").Append(Css).Append("</style></head>");
        sb.Append("<body data-mode=\"explore\">");

        // ── header / mode toggle ──────────────────────────────────────────────
        sb.Append("<header class=\"hdr\">");
        sb.Append($"<div><strong>{H(title)}</strong> <span class=\"meta\">{fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(boardsLabel)) sb.Append($" · {H(boardsLabel)}");
        sb.Append($" · {totalCount} ticket(s)</span></div>");
        sb.Append("<div class=\"toggle\"><button id=\"btn-explore\" class=\"active\">Explore</button>");
        sb.Append("<button id=\"btn-present\">Present</button></div></header>");

        // ── present mode slides ───────────────────────────────────────────────
        sb.Append("<section class=\"present\">");
        // slide 1 — title
        sb.Append("<div class=\"slide\"><div class=\"slide-body slide-title\">");
        sb.Append($"<h1>{H(title)}</h1>");
        sb.Append($"<p class=\"hero\">{fromUtc:MMM d, yyyy} → {toUtc:MMM d, yyyy}</p>");
        if (!string.IsNullOrWhiteSpace(boardsLabel)) sb.Append($"<p class=\"hero-sub\">{H(boardsLabel)}</p>");
        sb.Append($"<p class=\"hero-sub\">{totalCount} ticket(s) · Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>");
        sb.Append("</div></div>");

        // slide 2 — by type
        sb.Append("<div class=\"slide\"><div class=\"slide-body\"><h2>By Ticket Type</h2>");
        sb.Append("<div class=\"stat-row\">").Append(Donut(byType.Select(x => (x.Key, x.Count)).ToList()));
        sb.Append("<div class=\"cards\">");
        foreach (var x in byType)
        {
            var pct = totalCount == 0 ? 0 : x.Count * 100.0 / totalCount;
            sb.Append($"<div class=\"card\"><div class=\"k\">{H(x.Key)}</div>" +
                      $"<div class=\"v\">{x.Count}</div>" +
                      $"<div class=\"p\">{pct:0.0}%</div></div>");
        }
        sb.Append("</div></div></div></div>");

        // slide 3 — by person
        sb.Append("<div class=\"slide\"><div class=\"slide-body\"><h2>By Assigned Person</h2>");
        sb.Append(Bars(byPerson.Select(x => (x.Key, x.Count)).ToList(), totalCount));
        sb.Append("</div></div>");

        // slide 4 — per-ticket
        sb.Append("<div class=\"slide\"><div class=\"slide-body wide\"><h2>Per Ticket</h2>");
        sb.Append(RenderTicketList(groupedByPerson));
        sb.Append("</div></div>");
        sb.Append("</section>");

        // ── explore mode ──────────────────────────────────────────────────────
        sb.Append("<section class=\"explore\">");
        sb.Append("<div class=\"filters\">");
        sb.Append("<label>Type: <select id=\"f-type\"><option value=\"\">All</option>");
        foreach (var x in byType) sb.Append($"<option>{H(x.Key)}</option>");
        sb.Append("</select></label>");
        sb.Append("<label>Person: <select id=\"f-person\"><option value=\"\">All</option>");
        foreach (var x in byPerson) sb.Append($"<option>{H(x.Key)}</option>");
        sb.Append("</select></label>");
        sb.Append("<label>State: <select id=\"f-state\"><option value=\"\">All</option>");
        sb.Append("<option value=\"closed\">Closed only</option>");
        sb.Append("<option value=\"open\">Open only</option></select></label>");
        sb.Append("<input type=\"search\" id=\"f-q\" placeholder=\"Search title / issue / fix\">");
        sb.Append("</div>");

        sb.Append("<div class=\"section\"><h2>By Ticket Type</h2><div class=\"stat-row\">");
        sb.Append(Donut(byType.Select(x => (x.Key, x.Count)).ToList()));
        sb.Append("<div class=\"cards\">");
        foreach (var x in byType)
        {
            var pct = totalCount == 0 ? 0 : x.Count * 100.0 / totalCount;
            sb.Append($"<div class=\"card\"><div class=\"k\">{H(x.Key)}</div>" +
                      $"<div class=\"v\">{x.Count}</div><div class=\"p\">{pct:0.0}%</div></div>");
        }
        sb.Append("</div></div></div>");

        sb.Append("<div class=\"section\"><h2>By Assigned Person</h2>");
        sb.Append(Bars(byPerson.Select(x => (x.Key, x.Count)).ToList(), totalCount));
        sb.Append("</div>");

        sb.Append("<div class=\"section\"><h2>Tickets</h2>");
        sb.Append(RenderTicketList(groupedByPerson));
        sb.Append("</div>");
        sb.Append("</section>");

        sb.Append("<script>").Append(Js).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ── per-ticket render ────────────────────────────────────────────────────

    private static string RenderTicketList(IEnumerable<IGrouping<string, ReportTicket>> grouped)
    {
        var sb = new StringBuilder();
        foreach (var group in grouped)
        {
            sb.Append($"<div class=\"person-group\"><h3>{H(group.Key)} " +
                      $"<span class=\"count\">({group.Count()})</span></h3>");
            foreach (var t in group.OrderByDescending(x => x.ClosedAt ?? DateTime.MinValue))
            {
                var stateClass = (t.State?.ToLowerInvariant() == "closed"
                    || t.State?.ToLowerInvariant() == "done"
                    || t.State?.ToLowerInvariant() == "resolved") ? "closed" : "open";
                sb.Append($"<article class=\"ticket {stateClass}\" " +
                          $"data-type=\"{H(t.WorkItemType)}\" " +
                          $"data-person=\"{H(group.Key)}\" " +
                          $"data-state=\"{stateClass}\">");
                sb.Append($"<header><span class=\"id\">#{t.Id}</span> " +
                          $"<span class=\"type\">{H(t.WorkItemType)}</span> " +
                          $"<span class=\"state {stateClass}\">{H(t.State ?? "")}</span> " +
                          $"<h4>{H(t.Title)}</h4></header>");

                sb.Append("<div class=\"body\">");
                sb.Append($"<div class=\"field\"><b>Issue / Description</b>" +
                          $"<div class=\"html\">{SanitizeHtml(t.IssueDescription) ?? "<em>none</em>"}</div></div>");

                var fixOk = !string.IsNullOrWhiteSpace(t.Fix);
                sb.Append($"<div class=\"field\"><b>Fix</b>");
                if (fixOk) sb.Append($"<div class=\"html\">{SanitizeHtml(t.Fix)}</div>");
                else sb.Append("<div class=\"missing\">⚠ Fix missing</div>");
                sb.Append("</div>");

                sb.Append("</div></article>");
            }
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    // ── inline-SVG charts ────────────────────────────────────────────────────

    private static readonly string[] Palette =
    [
        "#1565c0","#2e7d32","#ef6c00","#c62828","#6a1b9a","#00838f","#558b2f","#4527a0","#37474f",
    ];

    private static string Donut(IReadOnlyList<(string Key, int Count)> data)
    {
        int total = data.Sum(d => d.Count);
        if (total == 0) return "<div class=\"chart-empty\">No data</div>";
        const int size = 220, r = 80, cx = 110, cy = 110;
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"donut\" viewBox=\"0 0 {size} {size}\" width=\"{size}\" height=\"{size}\">");
        double a = -Math.PI / 2;
        for (int i = 0; i < data.Count; i++)
        {
            var slice = 2 * Math.PI * data[i].Count / total;
            var x1 = cx + r * Math.Cos(a);
            var y1 = cy + r * Math.Sin(a);
            var x2 = cx + r * Math.Cos(a + slice);
            var y2 = cy + r * Math.Sin(a + slice);
            var large = slice > Math.PI ? 1 : 0;
            var color = Palette[i % Palette.Length];
            sb.Append($"<path d=\"M{cx},{cy} L{x1:0.##},{y1:0.##} " +
                      $"A{r},{r} 0 {large} 1 {x2:0.##},{y2:0.##} Z\" " +
                      $"fill=\"{color}\"><title>{H(data[i].Key)}: {data[i].Count}</title></path>");
            a += slice;
        }
        // inner hole + total
        sb.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"50\" fill=\"#fff\"/>");
        sb.Append($"<text x=\"{cx}\" y=\"{cy - 6}\" text-anchor=\"middle\" class=\"donut-num\">{total}</text>");
        sb.Append($"<text x=\"{cx}\" y=\"{cy + 14}\" text-anchor=\"middle\" class=\"donut-lbl\">total</text>");
        sb.Append("</svg>");

        // legend
        sb.Append("<ul class=\"legend\">");
        for (int i = 0; i < data.Count; i++)
        {
            var pct = data[i].Count * 100.0 / total;
            sb.Append($"<li><span class=\"sw\" style=\"background:{Palette[i % Palette.Length]}\"></span>" +
                      $"{H(data[i].Key)} — <b>{data[i].Count}</b> ({pct:0.0}%)</li>");
        }
        sb.Append("</ul>");
        return sb.ToString();
    }

    private static string Bars(IReadOnlyList<(string Key, int Count)> data, int total)
    {
        if (total == 0) return "<div class=\"chart-empty\">No data</div>";
        var sb = new StringBuilder("<div class=\"bars\">");
        for (int i = 0; i < data.Count; i++)
        {
            var pct = data[i].Count * 100.0 / total;
            var color = Palette[i % Palette.Length];
            sb.Append("<div class=\"bar-row\">");
            sb.Append($"<div class=\"bar-label\">{H(data[i].Key)}</div>");
            sb.Append($"<div class=\"bar-track\">" +
                      $"<div class=\"bar-fill\" style=\"width:{pct:0.##}%; background:{color}\"></div></div>");
            sb.Append($"<div class=\"bar-num\"><b>{data[i].Count}</b> ({pct:0.0}%)</div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string H(string s) => HttpUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>Very light sanitizer — ADO HTML is mostly trusted; strip script tags defensively.</summary>
    private static string? SanitizeHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return System.Text.RegularExpressions.Regex.Replace(
            html, "<script[^>]*>.*?</script>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    // ── inline CSS / JS ──────────────────────────────────────────────────────

    private const string Css = """
        :root { --fg:#1a1a1a; --muted:#6b7280; --bg:#fafafa; --card:#fff; --border:#e5e7eb; --primary:#1565c0; }
        * { box-sizing: border-box; }
        body { font-family: -apple-system, "Segoe UI", Roboto, Arial, sans-serif; margin:0; color:var(--fg); background:var(--bg); }
        .hdr { position:sticky; top:0; z-index:10; display:flex; justify-content:space-between; align-items:center;
               padding:.6rem 1rem; background:#fff; border-bottom:1px solid var(--border); }
        .hdr .meta { color:var(--muted); margin-left:.5rem; font-weight:400; }
        .toggle button { border:1px solid var(--border); background:#fff; padding:.35rem .8rem; cursor:pointer; }
        .toggle button.active { background:var(--primary); color:#fff; border-color:var(--primary); }
        .toggle button:first-child { border-radius:6px 0 0 6px; }
        .toggle button:last-child  { border-radius:0 6px 6px 0; }

        /* Explore (default) */
        body[data-mode="present"] .explore { display:none; }
        body[data-mode="explore"] .present { display:none; }
        .explore { padding:1rem; max-width:1200px; margin:0 auto; }
        .filters { position:sticky; top:48px; background:var(--bg); padding:.5rem 0 .75rem;
                    display:flex; gap:.75rem; flex-wrap:wrap; align-items:center; z-index:5; }
        .filters label { display:inline-flex; align-items:center; gap:.4rem; font-size:.9rem; color:var(--muted); }
        .filters select, .filters input { padding:.3rem .5rem; border:1px solid var(--border); border-radius:6px; background:#fff; }
        #f-q { min-width:220px; }
        .section { background:#fff; border:1px solid var(--border); border-radius:8px; padding:1rem 1.2rem; margin-bottom:1rem; }
        .section > h2 { margin:0 0 .8rem; font-size:1.1rem; }

        .stat-row { display:flex; gap:1.5rem; flex-wrap:wrap; align-items:center; }
        .cards { display:flex; gap:.6rem; flex-wrap:wrap; }
        .card { background:#fff; border:1px solid var(--border); border-radius:8px; padding:.6rem .9rem; min-width:120px; }
        .card .k { color:var(--muted); font-size:.85rem; }
        .card .v { font-size:1.6rem; font-weight:700; }
        .card .p { color:var(--muted); font-size:.85rem; }
        .chart-empty { color:var(--muted); font-style:italic; }

        .donut { display:block; }
        .donut-num { font-size:1.6rem; font-weight:700; }
        .donut-lbl { font-size:.8rem; fill:var(--muted); }
        .legend { list-style:none; padding:0; margin:0; }
        .legend li { padding:.15rem 0; }
        .legend .sw { display:inline-block; width:12px; height:12px; border-radius:3px; vertical-align:middle; margin-right:.5rem; }

        .bars { display:flex; flex-direction:column; gap:.4rem; }
        .bar-row { display:grid; grid-template-columns: 180px 1fr 140px; gap:.6rem; align-items:center; }
        .bar-label { color:var(--fg); font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        .bar-track { background:#eef0f3; height:14px; border-radius:7px; overflow:hidden; }
        .bar-fill { height:100%; border-radius:7px; transition:width 300ms; }
        .bar-num { color:var(--muted); font-size:.9rem; }

        .person-group { margin-bottom:1.4rem; }
        .person-group h3 { font-size:1rem; margin:1rem 0 .5rem; padding-bottom:.3rem; border-bottom:1px solid var(--border); }
        .person-group h3 .count { color:var(--muted); font-weight:400; font-size:.9rem; }
        .ticket { background:#fff; border:1px solid var(--border); border-radius:8px; padding:.7rem .9rem; margin-bottom:.6rem; }
        .ticket header { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; }
        .ticket header .id { color:var(--muted); font-size:.85rem; }
        .ticket header .type { background:#eef0f3; color:#374151; padding:.05rem .5rem; border-radius:999px; font-size:.75rem; }
        .ticket header .state { padding:.05rem .5rem; border-radius:999px; font-size:.75rem; }
        .ticket header .state.closed { background:#e6f4ea; color:#1b5e20; }
        .ticket header .state.open   { background:#fff3e0; color:#bf360c; }
        .ticket h4 { margin:0; font-size:.98rem; flex:1 1 100%; }
        .ticket .body { margin-top:.5rem; display:grid; gap:.5rem; grid-template-columns: 1fr 1fr; }
        .ticket .field b { display:block; color:var(--muted); font-size:.78rem; text-transform:uppercase; margin-bottom:.2rem; }
        .ticket .html { font-size:.9rem; line-height:1.4; max-height:200px; overflow:auto; padding:.4rem; background:#fafafa; border-radius:4px; }
        .ticket .html img { max-width:100%; }
        .missing { color:#bf360c; font-weight:500; background:#fff3e0; padding:.3rem .5rem; border-radius:4px; display:inline-block; }
        .ticket.hidden { display:none; }

        /* Present mode — slide deck */
        .present { display:flex; flex-direction:column; }
        .slide { min-height:calc(100vh - 56px); display:flex; align-items:center; justify-content:center; padding:2rem;
                 scroll-snap-align:start; border-bottom:1px solid var(--border); }
        body[data-mode="present"] .present { scroll-snap-type:y mandatory; overflow-y:auto; height:calc(100vh - 56px); }
        .slide-body { width:100%; max-width:1100px; }
        .slide-body.wide { max-width:1200px; }
        .slide-body h2 { font-size:1.8rem; margin:0 0 1.5rem; }
        .slide-title { text-align:center; }
        .slide-title h1 { font-size:3rem; margin:0 0 1rem; }
        .slide-title .hero { font-size:1.5rem; color:var(--muted); margin:.3rem 0; }
        .slide-title .hero-sub { color:var(--muted); margin:.2rem 0; }
        body[data-mode="present"] .ticket .html { max-height:140px; }
        """;

    private const string Js = """
        const body = document.body;
        document.getElementById('btn-explore').addEventListener('click', () => setMode('explore'));
        document.getElementById('btn-present').addEventListener('click', () => setMode('present'));
        function setMode(m) {
          body.dataset.mode = m;
          document.getElementById('btn-explore').classList.toggle('active', m === 'explore');
          document.getElementById('btn-present').classList.toggle('active', m === 'present');
        }
        // Arrow-key navigation in present mode
        document.addEventListener('keydown', e => {
          if (body.dataset.mode !== 'present') return;
          const slides = [...document.querySelectorAll('.present .slide')];
          const cur = slides.findIndex(s => s.getBoundingClientRect().top >= -10);
          if (e.key === 'ArrowDown' || e.key === 'PageDown' || e.key === ' ') {
            e.preventDefault();
            slides[Math.min(slides.length - 1, cur + 1)]?.scrollIntoView({ behavior:'smooth' });
          } else if (e.key === 'ArrowUp' || e.key === 'PageUp') {
            e.preventDefault();
            slides[Math.max(0, cur - 1)]?.scrollIntoView({ behavior:'smooth' });
          }
        });
        // Filters
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
          // hide empty person groups
          document.querySelectorAll('.explore .person-group').forEach(g => {
            const visible = [...g.querySelectorAll('.ticket')].some(t => !t.classList.contains('hidden'));
            g.style.display = visible ? '' : 'none';
          });
        }
        [fType, fPerson, fState].forEach(el => el.addEventListener('change', applyFilters));
        fQ.addEventListener('input', applyFilters);
        """;
}
