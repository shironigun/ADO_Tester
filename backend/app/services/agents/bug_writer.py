"""Bug Writer agent — verbatim prompt + parsing from .NET BugWriterAgent."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field

from app.services.agents.analyst import AnalystOutput
from app.services.agents.json_utils import coerce_string_list, strip_fences
from app.services.ado_client import AdoWorkItem
from app.services.llm.base import LlmClient


SYSTEM_PROMPT = """You are the Bug Writer agent — a senior SQA analyst writing bug reports for YourProduct /
YourOrg in Mahmood Ahmad's QA voice. Every output rule below is mandatory.

## TITLE FORMAT
[Module] - [Feature] - <specific failure or unexpected behavior>.
Examples:
  Deals - Pipeline - SAVE button stays enabled when required Name field is empty.
  Settings - Roles - Non-Admin user can access pipeline settings page.
NO "should", NO question phrasing, NO speculation in the title.

## SEVERITY (single value)
Pick exactly one of: Critical, High, Medium, Low.
  - Critical: data loss, security exposure, full module blocked
  - High:     core feature broken, no clean workaround
  - Medium:   feature works but is incorrect / partial
  - Low:      cosmetic, copy, minor UX deviation

## REPRO STEPS (JSON array of plain strings)
- Imperative, third-person, atomic. ONE action per item.
  "Click the SAVE button." (not "Click SAVE and verify the dialog")
- Childlike granularity: navigations are separate steps.
- Reference exact UI labels from the story / Analyst input.
- Include any precondition state inline as the first steps (e.g. "Login as a user with the
  Sales Manager role.") OR put it in the `preconditions` field.

## EXPECTED vs ACTUAL (two short, definitive strings)
- `expected`: pulled from Analyst's expected behavior. Definitive ("The Save button is
  disabled.") — never "should be".
- `actual`:   pulled from observed behavior (provided by user, or Playwright trace).
  Same definitive style describing what really happens.

## SUGGESTED FIX (one or two sentences, optional)
- Concrete and code-aware if code context was provided.
  "Wire `pipelineName` required validator before enabling Save." — not "Add validation."
- If you have no high-confidence suggestion, leave the field as an empty string.

## VOICE
- Definitive, neutral. No emoji, no greetings, no "Note:", no hedging.
- Exact UI labels verbatim.
- One bug = one issue. If the actual contains multiple deviations, output multiple bugs.

## OUTPUT FORMAT
Return ONLY a JSON array of bug objects. No markdown fences. No prose.
Each object MUST have these exact keys (camelCase):
[
  {
    "title": "string",
    "severity": "Critical|High|Medium|Low",
    "preconditions": "string (may be empty)",
    "reproSteps": ["string", ...],
    "expected": "string",
    "actual": "string",
    "suggestedFix": "string (may be empty)"
  }
]
Every list field MUST contain ONLY plain strings — never objects, never nested arrays.
"""

_HTML_TAG_RE = re.compile(r"<[^>]+>")


def _strip_html(html: str) -> str:
    return _HTML_TAG_RE.sub(" ", html).strip()


@dataclass
class GeneratedBug:
    title: str = ""
    severity: str = "Medium"
    preconditions: str = ""
    repro_steps: list[str] = field(default_factory=list)
    expected: str = ""
    actual: str = ""
    suggested_fix: str = ""


class BugWriterAgent:
    def __init__(self, llm: LlmClient) -> None:
        self._llm = llm

    async def generate(
        self,
        analysis: AnalystOutput,
        work_item: AdoWorkItem,
        actual_behavior: str,
        code_context: str | None,
    ) -> list[GeneratedBug]:
        prompt = self._build_user_prompt(analysis, work_item, actual_behavior, code_context)
        raw = await self._llm.complete(SYSTEM_PROMPT, prompt)
        cleaned = strip_fences(raw, opener="[")
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"Bug Writer returned invalid JSON: {e}") from None
        if not isinstance(data, list):
            raise RuntimeError("Bug Writer returned non-array JSON.")

        out: list[GeneratedBug] = []
        for item in data:
            if not isinstance(item, dict):
                continue
            sev = str(item.get("severity") or "Medium").strip()
            if sev not in {"Critical", "High", "Medium", "Low"}:
                # Tolerate stylings like "1 - Critical" → "Critical"
                low = sev.lower()
                if "crit" in low: sev = "Critical"
                elif "high" in low: sev = "High"
                elif "low" in low: sev = "Low"
                else: sev = "Medium"

            out.append(GeneratedBug(
                title=str(item.get("title") or ""),
                severity=sev,
                preconditions=str(item.get("preconditions") or ""),
                repro_steps=coerce_string_list(item.get("reproSteps")),
                expected=str(item.get("expected") or ""),
                actual=str(item.get("actual") or ""),
                suggested_fix=str(item.get("suggestedFix") or ""),
            ))
        return out

    @staticmethod
    def _build_user_prompt(
        a: AnalystOutput, wi: AdoWorkItem, actual: str, code: str | None,
    ) -> str:
        lines: list[str] = []
        lines.append(f"# Ticket #{wi.id} — {wi.title}")
        if wi.acceptance_criteria and wi.acceptance_criteria.strip():
            lines.append("## Acceptance Criteria (from ticket)")
            lines.append(_strip_html(wi.acceptance_criteria))

        lines.append("## Analyst — Expected Behavior")
        if a.module:    lines.append(f"Module:  {a.module}")
        if a.feature:   lines.append(f"Feature: {a.feature}")
        if a.summary:   lines.append(f"Summary: {a.summary}")
        if a.happy_path:lines.append(f"Happy path: {a.happy_path}")

        def append_list(label: str, items: list[str]) -> None:
            if not items: return
            lines.append(f"{label}:")
            for i in items: lines.append(f"- {i}")

        append_list("Acceptance criteria", a.acceptance_criteria)
        append_list("Business rules",      a.business_rules)
        append_list("Edge cases",          a.edge_cases)
        append_list("Negative cases",      a.negative_cases)
        append_list("Permissions",         a.permissions)
        append_list("Cross-flow impacts",  a.cross_flow)

        lines.append("")
        lines.append("## Observed Actual Behavior")
        lines.append((actual or "").strip())

        if code and code.strip():
            lines.append("")
            lines.append("## Code Context (snippets from the repo)")
            lines.append(code.strip())

        lines.append("")
        lines.append("## Task")
        lines.append("Identify each discrepancy between Expected and Observed Actual. For each,")
        lines.append("draft ONE bug in the required JSON shape. Do not invent bugs that aren't")
        lines.append("supported by the observed actual. Return ONLY the JSON array.")
        return "\n".join(lines)
