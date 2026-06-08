"""
Report Summarizer — uses the active LLM provider to turn raw ticket data into structured
insights (TL;DR, themes, speaker script). Always runs as part of report generation.

The output is a small JSON blob persisted alongside the report row, then consumed by the
HTML builder to populate AI-narrative slides. Failure to summarize never blocks the report —
on error we return a minimal summary so generation still produces a useful deck.
"""
from __future__ import annotations

import json
import logging
import re
from dataclasses import dataclass, field

from app.services.ado_client import ReportTicket
from app.services.llm import get_llm_client

log = logging.getLogger("qa.report-summarizer")

_HTML_TAG_RE = re.compile(r"<[^>]+>")


def _strip_html(s: str | None) -> str:
    if not s:
        return ""
    return _HTML_TAG_RE.sub(" ", s).strip()


@dataclass
class ThemeCluster:
    """A cluster of tickets the model believes share a theme."""
    name: str = ""
    description: str = ""
    ticket_ids: list[int] = field(default_factory=list)


@dataclass
class ReportSummary:
    """Structured insights consumed by the HTML builder. All optional — builder degrades gracefully."""
    tldr: list[str] = field(default_factory=list)
    headline: str = ""
    themes: list[ThemeCluster] = field(default_factory=list)
    speaker_script: dict[str, str] = field(default_factory=dict)   # slide-key → talking points
    risks: list[str] = field(default_factory=list)
    wins: list[str] = field(default_factory=list)

    def to_json(self) -> str:
        return json.dumps({
            "tldr": self.tldr,
            "headline": self.headline,
            "themes": [
                {"name": t.name, "description": t.description, "ticketIds": t.ticket_ids}
                for t in self.themes
            ],
            "speakerScript": self.speaker_script,
            "risks": self.risks,
            "wins": self.wins,
        })

    @staticmethod
    def from_json(text: str | None) -> "ReportSummary":
        if not text:
            return ReportSummary()
        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            return ReportSummary()
        out = ReportSummary(
            tldr=[str(x) for x in (data.get("tldr") or []) if x],
            headline=str(data.get("headline") or ""),
            speaker_script={str(k): str(v) for k, v in (data.get("speakerScript") or {}).items()},
            risks=[str(x) for x in (data.get("risks") or []) if x],
            wins=[str(x) for x in (data.get("wins") or []) if x],
        )
        for t in data.get("themes") or []:
            if not isinstance(t, dict):
                continue
            ids = t.get("ticketIds") or t.get("ticket_ids") or []
            out.themes.append(ThemeCluster(
                name=str(t.get("name") or ""),
                description=str(t.get("description") or ""),
                ticket_ids=[int(i) for i in ids if str(i).isdigit()],
            ))
        return out


SYSTEM_PROMPT = """You are the Report Insights agent. You analyze a week of QA / development
tickets and produce a structured JSON summary that drives a stakeholder presentation.

Output rules:
- Return ONLY a JSON object — no markdown fences, no prose.
- Be factual and neutral. No emoji, no marketing language, no hedging ("might", "could be").
- Reference ticket IDs (numeric only) when grouping into themes.
- Speaker script entries are short (1–2 sentences max) — what a BA would SAY when this slide is on screen.

Required keys:
{
  "headline": "string — one sentence summarizing the week's QA story",
  "tldr": ["3–5 short bullets, each one fact + implication"],
  "themes": [
    { "name": "short label", "description": "1 sentence", "ticketIds": [int, ...] }
  ],
  "risks": ["open critical/high items, no-fix-recorded items, repeated regressions"],
  "wins": ["closed-with-fix items, high-impact resolutions"],
  "speakerScript": {
    "cover":      "what to say on the cover slide",
    "tldr":       "what to say on the TL;DR slide",
    "by_type":    "what to say on the by-type stats slide",
    "by_person":  "what to say on the by-person stats slide",
    "themes":     "what to say on the themes slide (if any)",
    "risks":      "what to say on the risks slide (if any)",
    "wins":       "what to say on the wins slide (if any)",
    "closing":    "what to say on the closing slide"
  }
}
Every list field MUST contain ONLY plain strings — never objects, never nested arrays.
"""


class ReportSummarizer:
    def __init__(self, max_tickets_in_prompt: int = 80) -> None:
        # Cap tickets in the prompt to keep token cost predictable — the rest of the deck still
        # renders the full ticket list, but the AI insights are based on this sampled view.
        self._cap = max_tickets_in_prompt

    async def summarize(self, tickets: list[ReportTicket]) -> ReportSummary:
        if not tickets:
            return ReportSummary(headline="No tickets in range.")

        try:
            llm = await get_llm_client()
        except Exception as e:  # noqa: BLE001
            log.warning("LLM not available for report summary: %s", e)
            return self._fallback(tickets)

        prompt = self._build_user_prompt(tickets)
        try:
            raw = await llm.complete(SYSTEM_PROMPT, prompt)
        except Exception as e:  # noqa: BLE001
            log.warning("Summarizer LLM call failed (%s) — falling back", e)
            return self._fallback(tickets)

        cleaned = _strip_fences(raw)
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError as e:
            log.warning("Summarizer returned invalid JSON: %s", e)
            return self._fallback(tickets)
        if not isinstance(data, dict):
            return self._fallback(tickets)

        return ReportSummary.from_json(json.dumps(data))

    # ── helpers ─────────────────────────────────────────────────────────────

    def _build_user_prompt(self, tickets: list[ReportTicket]) -> str:
        sample = tickets[: self._cap]
        lines: list[str] = []
        lines.append(f"# Ticket Sample ({len(sample)} of {len(tickets)})")
        for t in sample:
            assignee = t.assigned_to or "Unassigned"
            state = t.state or "?"
            issue = _strip_html(t.issue_description)[:280]
            fix = _strip_html(t.fix)[:200]
            fix_label = fix if fix else "(no fix recorded)"
            lines.append(
                f"- #{t.id} [{t.work_item_type} · {state} · {assignee}] {t.title}\n"
                f"  issue: {issue}\n"
                f"  fix:   {fix_label}"
            )
        lines.append("")
        lines.append("Produce the JSON described in the system prompt.")
        return "\n".join(lines)

    def _fallback(self, tickets: list[ReportTicket]) -> ReportSummary:
        """No-LLM minimal summary so the deck still renders something useful."""
        total = len(tickets)
        no_fix = sum(1 for t in tickets if not (t.fix or "").strip())
        types = {t.work_item_type for t in tickets if t.work_item_type}
        return ReportSummary(
            headline=f"{total} ticket(s) across {len(types)} type(s); {no_fix} missing a recorded fix.",
            tldr=[
                f"{total} ticket(s) touched in the period.",
                f"{no_fix} closed/changed without a recorded fix." if no_fix else "All tickets have a recorded fix.",
                f"Spans {len(types)} work-item type(s): {', '.join(sorted(types)) or '—'}.",
            ],
            speaker_script={
                "cover": "This week's QA snapshot for the team.",
                "tldr": "High-level overview of what moved.",
                "by_type": "Volume by ticket type.",
                "by_person": "Where the work landed by assignee.",
                "closing": "Questions and follow-ups.",
            },
        )


_FENCE_RE = re.compile(r"^```[a-zA-Z]*\s*|\s*```$", re.MULTILINE)


def _strip_fences(text: str) -> str:
    s = text.strip()
    if s.startswith("```"):
        s = _FENCE_RE.sub("", s).strip()
    # Trim anything before the first { or after the last }
    first = s.find("{")
    last = s.rfind("}")
    if first >= 0 and last > first:
        s = s[first : last + 1]
    return s
