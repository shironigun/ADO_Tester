"""
Themed, dynamic, AI-narrative HTML report builder.

Three themes (data-theme attribute on <body>):
  - executive : light, minimal, serif headlines, big numbers
  - standup   : dark slate, neon accents, dense status-board
  - branded   : YourOrg corporate palette + logo lockup, "official" feel

Sections are emitted dynamically — empty sections collapse, the slide list adapts to the data.
Present mode is a real deck (cover, TL;DR, breakdowns, themes, risks, wins, per-person spotlights,
closing); Explore mode is a scrollable filterable page using the same source data.

The output remains fully self-contained — all CSS / JS / SVG inlined, no external deps.
"""
from __future__ import annotations

import html as html_lib
import math
import re
from collections import defaultdict
from datetime import datetime

from app.models import ReportTheme
from app.services.ado_client import ReportTicket
from app.services.reports.summarizer import ReportSummary, ThemeCluster

_PALETTE = [
    "#1565c0", "#2e7d32", "#ef6c00", "#c62828", "#6a1b9a",
    "#00838f", "#558b2f", "#4527a0", "#37474f",
]

_SCRIPT_RE = re.compile(r"<script[^>]*>.*?</script>", re.IGNORECASE | re.DOTALL)


def _h(s: str | None) -> str:
    return html_lib.escape(s or "")


def _sanitize_html(html_text: str | None) -> str | None:
    if not html_text or not html_text.strip():
        return None
    return _SCRIPT_RE.sub("", html_text)


def _is_closed(state: str | None) -> bool:
    return (state or "").lower() in {"closed", "done", "resolved", "completed"}


def _group_count(items, key) -> list[tuple[str, int]]:
    counts: dict[str, int] = defaultdict(int)
    for it in items:
        counts[key(it)] += 1
    return sorted(counts.items(), key=lambda kv: -kv[1])


# ════════════════════════════════════════════════════════════════════════════
# Public entry point
# ════════════════════════════════════════════════════════════════════════════

def build_report_html(
    title: str,
    from_utc: datetime,
    to_utc: datetime,
    boards_label: str,
    tickets: list[ReportTicket],
    *,
    theme: ReportTheme = ReportTheme.Executive,
    summary: ReportSummary | None = None,
) -> str:
    """Render the full self-contained HTML."""
    summary = summary or ReportSummary()

    total = len(tickets)
    by_type = _group_count(tickets, lambda t: t.work_item_type or "Unknown")
    by_person = _group_count(tickets, lambda t: t.assigned_to or "Unassigned")
    no_fix_tickets = [t for t in tickets if not (t.fix or "").strip()]
    open_critical = [
        t for t in tickets
        if not _is_closed(t.state)
        and (t.work_item_type or "").lower() in {"bug", "issue"}
    ]

    grouped_by_person: dict[str, list[ReportTicket]] = defaultdict(list)
    for t in tickets:
        grouped_by_person[t.assigned_to or "Unassigned"].append(t)

    # Per-person spotlights — only people with >=3 tickets get a slide; rest roll up.
    spotlights: list[tuple[str, list[ReportTicket]]] = []
    others: list[ReportTicket] = []
    for person, items in sorted(grouped_by_person.items(), key=lambda kv: -len(kv[1])):
        if len(items) >= 3 and len(spotlights) < 6:
            spotlights.append((person, items))
        else:
            others.extend(items)
    if others:
        spotlights.append((f"Others ({len(others)})", others))

    valid_themes = [c for c in summary.themes if c.name and (c.ticket_ids or c.description)]

    theme_attr = theme.value.lower()  # executive / standup / branded
    out: list[str] = []
    out.append('<!DOCTYPE html><html lang="en"><head><meta charset="utf-8">')
    out.append(f"<title>{_h(title)}</title>")
    out.append('<meta name="viewport" content="width=device-width, initial-scale=1">')
    out.append(f"<style>{_BASE_CSS}{_THEME_CSS}</style></head>")
    out.append(f'<body data-mode="explore" data-theme="{theme_attr}">')

    # Header / mode toggle
    out.append('<header class="hdr">')
    out.append(
        '<div class="hdr-title">'
        f'<strong>{_h(title)}</strong> '
        f'<span class="meta">{from_utc:%Y-%m-%d} → {to_utc:%Y-%m-%d}'
    )
    if boards_label:
        out.append(f" · {_h(boards_label)}")
    out.append(f" · {total} ticket(s)</span></div>")
    out.append(
        '<div class="toggle">'
        '<button id="btn-explore" class="active">Explore</button>'
        '<button id="btn-present" title="Present mode · ← → to navigate · Space or Tab for fullscreen · Esc to exit">Present</button>'
        '<button id="btn-script" title="Toggle speaker script (S)">Script</button>'
        '</div></header>'
    )

    # Build the ordered slide list dynamically — empty sections skip themselves.
    slides: list[tuple[str, str]] = []
    slides.append(("cover", _slide_cover(title, from_utc, to_utc, boards_label, total, summary)))
    if summary.tldr:
        slides.append(("tldr", _slide_tldr(summary)))
    if len(by_type) > 1:
        slides.append(("by_type", _slide_by_type(by_type, total)))
    if len(by_person) > 1:
        slides.append(("by_person", _slide_by_person(by_person, total)))
    if valid_themes:
        slides.append(("themes", _slide_themes(valid_themes, tickets)))
    if summary.risks or no_fix_tickets or open_critical:
        slides.append(("risks", _slide_risks(summary, no_fix_tickets, open_critical)))
    if summary.wins:
        slides.append(("wins", _slide_wins(summary)))
    for person, items in spotlights:
        slides.append((f"spot_{person}", _slide_spotlight(person, items)))
    slides.append(("closing", _slide_closing(summary, total)))

    # Present-mode section
    out.append('<section class="present">')
    for i, (key, body) in enumerate(slides):
        script_key = key.split("_")[0] if key.startswith("spot_") else key
        script = summary.speaker_script.get(script_key, "")
        counter = f'<div class="slide-counter">{i + 1} / {len(slides)}</div>'
        speaker = (
            f'<aside class="speaker"><h4>Speaker notes</h4><p>{_h(script)}</p></aside>'
            if script else ""
        )
        out.append(f'<div class="slide" data-key="{_h(key)}">{counter}{body}{speaker}</div>')
    out.append('</section>')

    # Explore-mode section
    out.append('<section class="explore">')
    out.append(_filters(by_type, by_person))
    if summary.tldr or summary.headline:
        out.append(_explore_summary(summary))
    if by_type:
        out.append(_explore_section("By Ticket Type", _donut_with_cards(by_type, total)))
    if by_person:
        out.append(_explore_section("By Assigned Person", _bars(by_person, total)))
    if valid_themes:
        out.append(_explore_section("AI-Detected Themes", _explore_themes(valid_themes)))
    out.append(_explore_section("Tickets", _render_ticket_list(grouped_by_person.items())))
    out.append('</section>')

    out.append(f"<script>{_JS}</script></body></html>")
    return "".join(out)


# ════════════════════════════════════════════════════════════════════════════
# Slide builders (Present mode)
# ════════════════════════════════════════════════════════════════════════════

def _slide_cover(
    title: str, from_utc: datetime, to_utc: datetime, boards_label: str,
    total: int, summary: ReportSummary,
) -> str:
    parts: list[str] = ['<div class="slide-body slide-cover">']
    parts.append('<div class="cover-eyebrow">QA Weekly Report</div>')
    parts.append(f'<h1 class="cover-title">{_h(title)}</h1>')
    if summary.headline:
        parts.append(f'<p class="cover-headline">{_h(summary.headline)}</p>')
    parts.append(
        '<div class="cover-meta">'
        f'<div><span class="cover-num">{total}</span>'
        f'<span class="cover-num-lbl">tickets</span></div>'
        f'<div class="cover-range">{from_utc:%b %d, %Y} → {to_utc:%b %d, %Y}</div>'
        '</div>'
    )
    if boards_label:
        parts.append(f'<p class="cover-sub">{_h(boards_label)}</p>')
    parts.append(
        f'<p class="cover-sub muted">Generated {datetime.utcnow():%Y-%m-%d %H:%M} UTC</p>'
    )
    parts.append('</div>')
    return "".join(parts)


def _slide_tldr(summary: ReportSummary) -> str:
    bullets = "".join(f'<li>{_h(b)}</li>' for b in summary.tldr)
    return (
        '<div class="slide-body slide-tldr">'
        '<div class="slide-eyebrow">TL;DR</div>'
        f'<h2 class="slide-h">{_h(summary.headline) or "This week at a glance"}</h2>'
        f'<ul class="big-bullets">{bullets}</ul>'
        '</div>'
    )


def _slide_by_type(by_type: list[tuple[str, int]], total: int) -> str:
    return (
        '<div class="slide-body">'
        '<div class="slide-eyebrow">Breakdown</div>'
        '<h2 class="slide-h">By Ticket Type</h2>'
        f'<div class="stat-row">{_donut_with_cards(by_type, total)}</div>'
        '</div>'
    )


def _slide_by_person(by_person: list[tuple[str, int]], total: int) -> str:
    return (
        '<div class="slide-body">'
        '<div class="slide-eyebrow">Breakdown</div>'
        '<h2 class="slide-h">By Assigned Person</h2>'
        f'{_bars(by_person, total)}'
        '</div>'
    )


def _slide_themes(themes: list[ThemeCluster], tickets: list[ReportTicket]) -> str:
    title_by_id = {t.id: t.title for t in tickets}
    cards: list[str] = []
    for c in themes[:6]:
        examples = "".join(
            f'<li>#{tid} — {_h(title_by_id.get(tid, ""))}</li>'
            for tid in c.ticket_ids[:4]
        )
        cards.append(
            '<article class="theme-card">'
            f'<h3>{_h(c.name)}</h3>'
            f'<p>{_h(c.description)}</p>'
            f'<ul class="theme-examples">{examples}</ul>'
            f'<div class="theme-count">{len(c.ticket_ids)} ticket(s)</div>'
            '</article>'
        )
    return (
        '<div class="slide-body wide">'
        '<div class="slide-eyebrow">Patterns</div>'
        '<h2 class="slide-h">Themes the AI surfaced</h2>'
        f'<div class="theme-grid">{"".join(cards)}</div>'
        '</div>'
    )


def _slide_risks(
    summary: ReportSummary,
    no_fix_tickets: list[ReportTicket],
    open_critical: list[ReportTicket],
) -> str:
    sections: list[str] = []
    if summary.risks:
        items = "".join(f'<li>{_h(x)}</li>' for x in summary.risks[:6])
        sections.append(
            f'<div class="risk-block"><h3>Identified risks</h3><ul>{items}</ul></div>'
        )
    if no_fix_tickets:
        items = "".join(
            f'<li><span class="tid">#{t.id}</span> {_h(t.title)}</li>'
            for t in no_fix_tickets[:8]
        )
        more = f' <em>+{len(no_fix_tickets) - 8} more</em>' if len(no_fix_tickets) > 8 else ""
        sections.append(
            f'<div class="risk-block">'
            f'<h3>{len(no_fix_tickets)} closed without a recorded fix</h3>'
            f'<ul class="tight">{items}</ul>{more}</div>'
        )
    if open_critical:
        items = "".join(
            f'<li><span class="tid">#{t.id}</span> {_h(t.title)} '
            f'<span class="state-pill open">{_h(t.state or "")}</span></li>'
            for t in open_critical[:8]
        )
        sections.append(
            f'<div class="risk-block">'
            f'<h3>{len(open_critical)} open Bug/Issue(s)</h3>'
            f'<ul class="tight">{items}</ul></div>'
        )
    return (
        '<div class="slide-body wide slide-risks">'
        '<div class="slide-eyebrow">Watch list</div>'
        '<h2 class="slide-h">Risks &amp; Gaps</h2>'
        + "".join(sections)
        + '</div>'
    )


def _slide_wins(summary: ReportSummary) -> str:
    items = "".join(f'<li>{_h(w)}</li>' for w in summary.wins[:6])
    return (
        '<div class="slide-body slide-wins">'
        '<div class="slide-eyebrow">Highlights</div>'
        '<h2 class="slide-h">Wins this week</h2>'
        f'<ul class="big-bullets">{items}</ul>'
        '</div>'
    )


def _slide_spotlight(person: str, tickets: list[ReportTicket]) -> str:
    closed = sum(1 for t in tickets if _is_closed(t.state))
    cards: list[str] = []
    for t in tickets[:8]:
        state_class = "closed" if _is_closed(t.state) else "open"
        cards.append(
            f'<li class="spot-ticket {state_class}">'
            f'<span class="tid">#{t.id}</span> '
            f'<span class="type">{_h(t.work_item_type)}</span> '
            f'<span class="state-pill {state_class}">{_h(t.state or "")}</span> '
            f'<span class="title">{_h(t.title)}</span>'
            '</li>'
        )
    more = f'<li class="spot-more">+{len(tickets) - 8} more</li>' if len(tickets) > 8 else ""
    return (
        '<div class="slide-body wide">'
        '<div class="slide-eyebrow">Spotlight</div>'
        f'<h2 class="slide-h">{_h(person)} '
        f'<span class="spot-count">{len(tickets)} ticket(s) · {closed} closed</span></h2>'
        f'<ul class="spot-list">{"".join(cards)}{more}</ul>'
        '</div>'
    )


def _slide_closing(summary: ReportSummary, total: int) -> str:
    bullets = ""
    if summary.tldr:
        bullets = (
            '<ul class="closing-bullets">'
            + "".join(f'<li>{_h(b)}</li>' for b in summary.tldr[:3])
            + '</ul>'
        )
    return (
        '<div class="slide-body slide-cover">'
        '<div class="cover-eyebrow">Wrap-up</div>'
        '<h1 class="cover-title">Questions?</h1>'
        f'{bullets}'
        f'<p class="cover-sub muted">{total} ticket(s) reviewed</p>'
        '</div>'
    )


# ════════════════════════════════════════════════════════════════════════════
# Explore-mode pieces
# ════════════════════════════════════════════════════════════════════════════

def _explore_summary(summary: ReportSummary) -> str:
    bullets = "".join(f'<li>{_h(b)}</li>' for b in summary.tldr)
    return (
        '<div class="section ai-summary">'
        '<h2>Executive summary</h2>'
        + (f'<p class="headline">{_h(summary.headline)}</p>' if summary.headline else "")
        + (f'<ul>{bullets}</ul>' if bullets else "")
        + '</div>'
    )


def _explore_section(heading: str, body: str) -> str:
    return f'<div class="section"><h2>{_h(heading)}</h2>{body}</div>'


def _filters(by_type, by_person) -> str:
    type_opts = "".join(f'<option>{_h(k)}</option>' for k, _ in by_type)
    person_opts = "".join(f'<option>{_h(k)}</option>' for k, _ in by_person)
    return (
        '<div class="filters">'
        f'<label>Type: <select id="f-type"><option value="">All</option>{type_opts}</select></label>'
        f'<label>Person: <select id="f-person"><option value="">All</option>{person_opts}</select></label>'
        '<label>State: <select id="f-state">'
        '<option value="">All</option>'
        '<option value="closed">Closed only</option>'
        '<option value="open">Open only</option>'
        '</select></label>'
        '<input type="search" id="f-q" placeholder="Search title / issue / fix">'
        '</div>'
    )


def _explore_themes(themes: list[ThemeCluster]) -> str:
    rows: list[str] = []
    for c in themes:
        ids = ", ".join(f"#{i}" for i in c.ticket_ids[:8])
        more = f' +{len(c.ticket_ids) - 8}' if len(c.ticket_ids) > 8 else ""
        rows.append(
            '<div class="theme-row">'
            f'<strong>{_h(c.name)}</strong> '
            f'<span class="theme-count-inline">({len(c.ticket_ids)} ticket(s))</span>'
            f'<div>{_h(c.description)}</div>'
            f'<div class="theme-ids">{ids}{more}</div>'
            '</div>'
        )
    return '<div class="theme-list">' + "".join(rows) + '</div>'


def _render_ticket_list(grouped) -> str:
    out: list[str] = []
    for person, items in grouped:
        out.append(
            f'<div class="person-group"><h3>{_h(person)} '
            f'<span class="count">({len(items)})</span></h3>'
        )
        for t in sorted(items, key=lambda x: (x.closed_at or datetime.min), reverse=True):
            state_class = "closed" if _is_closed(t.state) else "open"
            fix = (t.fix or "").strip()
            issue = _sanitize_html(t.issue_description) or "<em>none</em>"
            fix_html = (
                f'<div class="html">{_sanitize_html(fix)}</div>'
                if fix else '<div class="missing">⚠ Fix missing</div>'
            )
            out.append(
                f'<article class="ticket {state_class}" '
                f'data-type="{_h(t.work_item_type)}" '
                f'data-person="{_h(person)}" '
                f'data-state="{state_class}">'
                f'<header><span class="id">#{t.id}</span> '
                f'<span class="type">{_h(t.work_item_type)}</span> '
                f'<span class="state {state_class}">{_h(t.state or "")}</span> '
                f'<h4>{_h(t.title)}</h4></header>'
                '<div class="body">'
                f'<div class="field"><b>Issue / Description</b><div class="html">{issue}</div></div>'
                f'<div class="field"><b>Fix</b>{fix_html}</div>'
                '</div></article>'
            )
        out.append('</div>')
    return "".join(out)


# ════════════════════════════════════════════════════════════════════════════
# Charts (inline SVG)
# ════════════════════════════════════════════════════════════════════════════

def _donut_with_cards(data: list[tuple[str, int]], total: int) -> str:
    if total == 0:
        return '<div class="chart-empty">No data</div>'
    parts: list[str] = ['<div class="chart-pair">']
    parts.append(_donut_svg(data, total))
    parts.append('<div class="cards">')
    for i, (k, count) in enumerate(data):
        pct = count * 100.0 / total
        color = _PALETTE[i % len(_PALETTE)]
        parts.append(
            '<div class="card">'
            f'<div class="k"><span class="dot" style="background:{color}"></span>{_h(k)}</div>'
            f'<div class="v">{count}</div>'
            f'<div class="p">{pct:.1f}%</div>'
            '</div>'
        )
    parts.append('</div></div>')
    return "".join(parts)


def _donut_svg(data: list[tuple[str, int]], total: int) -> str:
    if total == 0:
        return ""
    size, r, cx, cy = 220, 80, 110, 110
    parts: list[str] = [
        f'<svg class="donut" viewBox="0 0 {size} {size}" width="{size}" height="{size}">'
    ]
    a = -math.pi / 2
    for i, (k, count) in enumerate(data):
        slice_ = 2 * math.pi * count / total
        x1, y1 = cx + r * math.cos(a), cy + r * math.sin(a)
        x2, y2 = cx + r * math.cos(a + slice_), cy + r * math.sin(a + slice_)
        large = 1 if slice_ > math.pi else 0
        color = _PALETTE[i % len(_PALETTE)]
        parts.append(
            f'<path d="M{cx},{cy} L{x1:.2f},{y1:.2f} '
            f'A{r},{r} 0 {large} 1 {x2:.2f},{y2:.2f} Z" '
            f'fill="{color}"><title>{_h(k)}: {count}</title></path>'
        )
        a += slice_
    parts.append(f'<circle cx="{cx}" cy="{cy}" r="50" fill="var(--card-bg)"/>')
    parts.append(
        f'<text x="{cx}" y="{cy - 6}" text-anchor="middle" class="donut-num">{total}</text>'
    )
    parts.append(
        f'<text x="{cx}" y="{cy + 14}" text-anchor="middle" class="donut-lbl">total</text>'
    )
    parts.append('</svg>')
    return "".join(parts)


def _bars(data: list[tuple[str, int]], total: int) -> str:
    if total == 0:
        return '<div class="chart-empty">No data</div>'
    rows: list[str] = ['<div class="bars">']
    for i, (k, count) in enumerate(data):
        pct = count * 100.0 / total
        color = _PALETTE[i % len(_PALETTE)]
        rows.append(
            '<div class="bar-row">'
            f'<div class="bar-label">{_h(k)}</div>'
            '<div class="bar-track">'
            f'<div class="bar-fill" style="--bar-w:{pct:.2f}%; --bar-c:{color}"></div>'
            '</div>'
            f'<div class="bar-num"><b>{count}</b> ({pct:.1f}%)</div>'
            '</div>'
        )
    rows.append('</div>')
    return "".join(rows)


# ════════════════════════════════════════════════════════════════════════════
# CSS / JS (inlined)
# ════════════════════════════════════════════════════════════════════════════

_BASE_CSS = """
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
"""

_THEME_CSS = """
body[data-theme="executive"] {
  --font-display: 'Georgia', 'Times New Roman', serif;
  --font-body: -apple-system, 'Segoe UI', Roboto, Arial, sans-serif;
  --font-mono: 'SF Mono', Consolas, monospace;
  --bg:#fafafa; --fg:#1a1a1a; --muted:#6b7280;
  --card-bg:#fff; --inner-bg:#fafafa; --border:#e5e7eb; --track-bg:#eef0f3;
  --hdr-bg:#fff; --hdr-fg:#1a1a1a;
  --primary:#1565c0; --accent:#1565c0; --accent-good:#2e7d32; --accent-warn:#c62828;
  --pill-bg:#eef0f3; --pill-fg:#374151;
  --closed-bg:#e6f4ea; --closed-fg:#1b5e20;
  --open-bg:#fff3e0; --open-fg:#bf360c;
  --missing-bg:#fff3e0; --missing-fg:#bf360c;
}

body[data-theme="standup"] {
  --font-display: 'Inter', -apple-system, 'Segoe UI', sans-serif;
  --font-body: 'Inter', -apple-system, 'Segoe UI', sans-serif;
  --font-mono: 'SF Mono', Consolas, monospace;
  --bg:#0f172a; --fg:#e2e8f0; --muted:#94a3b8;
  --card-bg:#1e293b; --inner-bg:#0f172a; --border:#334155; --track-bg:#1e293b;
  --hdr-bg:#1e293b; --hdr-fg:#e2e8f0;
  --primary:#38bdf8; --accent:#38bdf8; --accent-good:#4ade80; --accent-warn:#f87171;
  --pill-bg:#334155; --pill-fg:#cbd5e1;
  --closed-bg:rgba(74,222,128,0.18); --closed-fg:#4ade80;
  --open-bg:rgba(248,113,113,0.18); --open-fg:#f87171;
  --missing-bg:rgba(248,113,113,0.18); --missing-fg:#f87171;
}
body[data-theme="standup"] .ticket .html { background:#0b1224; }
body[data-theme="standup"] .filters select,
body[data-theme="standup"] .filters input { background:#1e293b; color:#e2e8f0; }

body[data-theme="branded"] {
  --font-display: 'Helvetica Neue', 'Arial', sans-serif;
  --font-body: 'Helvetica Neue', 'Arial', sans-serif;
  --font-mono: 'SF Mono', Consolas, monospace;
  --bg:#ffffff; --fg:#0b3a6f; --muted:#5a6c80;
  --card-bg:#fff; --inner-bg:#f3f7fb; --border:#d3deeb; --track-bg:#e3ecf6;
  --hdr-bg:#0b3a6f; --hdr-fg:#ffffff;
  --primary:#0b6cd2; --accent:#0b6cd2; --accent-good:#2e7d32; --accent-warn:#c62828;
  --pill-bg:#e3ecf6; --pill-fg:#0b3a6f;
  --closed-bg:#e6f4ea; --closed-fg:#1b5e20;
  --open-bg:#fff3e0; --open-fg:#bf360c;
  --missing-bg:#fff3e0; --missing-fg:#bf360c;
}
body[data-theme="branded"] .hdr .meta { color:#cfd8e3; }
body[data-theme="branded"] .toggle button { background:#fff; color:#0b3a6f; border-color:#cfd8e3; }
body[data-theme="branded"] .toggle button.active { background:#0b6cd2; color:#fff; }
body[data-theme="branded"] .slide-cover::before {
  content:"CONSTELLATIONDEALER";
  position:absolute; top:1.5rem; left:0; right:0;
  letter-spacing:.4em; font-size:.7rem; color:var(--accent);
  font-weight:700; text-align:center;
}
body[data-theme="branded"] .slide-cover { padding-top:3rem; }
"""

_JS = """
const body = document.body;
document.getElementById('btn-explore').addEventListener('click', () => setMode('explore'));
document.getElementById('btn-present').addEventListener('click', () => setMode('present'));
document.getElementById('btn-script').addEventListener('click', () => {
  if (body.dataset.mode !== 'present') return;
  body.dataset.script = body.dataset.script === 'on' ? 'off' : 'on';
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
"""
