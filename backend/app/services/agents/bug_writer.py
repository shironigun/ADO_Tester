"""Bug Writer agent — verbatim prompt + parsing from .NET BugWriterAgent."""
from __future__ import annotations

import re
from dataclasses import dataclass, field

from app.services.agents.analyst import AnalystOutput
from app.services.agents.json_utils import coerce_string_list, complete_json
from app.services.ado_client import AdoWorkItem
from app.services.llm.base import LlmClient


SYSTEM_PROMPT = """You are the Bug Writer agent — a senior SQA analyst writing bug reports for TargetCRM /
ConstellationDealer in Mahmood Ahmad's QA voice. Every output rule below is mandatory.

TargetCRM modules you will see: Deals, Messenger, Customers, Settings, Leads, Payments, Reports,
Integrations. Features are the sub-areas (Pipeline, Facebook, Merge, Roles, Import, etc.).

## TARGETCRM DOMAIN VOCABULARY (use it verbatim)
- DMS back-ends: ASPEN, INFINITY, EVEREST, IDEAL (and TARGET). Environments: QA, Staging, Production.
- ALL-CAPS the domain keywords wherever they appear — states & entities (CLOSED, OPEN, EMAIL, MMS,
  PDF, CSV, FB), DMS names (ASPEN, INFINITY, EVEREST, IDEAL, TARGET), environments (QA, STAGING).
- A sync issue moves data DMS <-> TargetCRM (e.g. "CLOSED invoices syncing as OPEN from ASPEN").

## TITLE FORMAT — declarative negative-state statement (NOT [Module] - [Feature])
State plainly what is broken, name the module/screen, and END with the environment. ALL-CAPS the
domain keywords. Use breadcrumb arrows for nested screens (Settings > EMAIL). Add a bracketed
dealer ID when dealer-specific. Pick the frame that fits:
  - "<thing> not working" / "<thing> is/are not <verb>ing"
  - "Unable to <verb>"        - "<thing> is not <correct>"
  - "Error <doing X>"          - "App crashed on <action>"
  - "Able to <do invalid thing>" (for things that should be blocked but aren't)
Examples (house style — match these):
  EMAIL Automations are not working on QA environment
  Unable to CREATE/UPDATE customers on ASPEN QA environment.
  Able to add email without any type for ASPEN customers
  PDF Preview/Icon is not displayed in MMS broadcast PREVIEW screen
  CLOSED invoices are syncing as OPEN from ASPEN to TARGET
  App crashed on uploading attachment in Settings > EMAIL on QA environment.
NO "should", NO question phrasing, NO speculation, NO "[Module] - [Feature]" prefixing. State the
observed failure flatly as a sentence.

## SEVERITY (single value)
Pick exactly one of: Critical, High, Medium, Low.
  - Critical: data loss, security exposure, full module blocked
  - High:     core feature broken, no clean workaround
  - Medium:   feature works but is incorrect / partial
  - Low:      cosmetic, copy, minor UX deviation

## REPRO STEPS (JSON array of plain strings) — EVERYTHING goes here
There must NEVER be a separate preconditions heading. ALWAYS return "preconditions" as an empty
string (""); ALL setup belongs in reproSteps as the first steps.
- Imperative, third-person, atomic. ONE action per item.
  "Click the SAVE button." (not "Click SAVE and verify the dialog")
- Childlike granularity: every click, navigation, and field entry is its own step. A
  navigation like "Navigate to Settings → Deals tab → Add Pipeline" is THREE steps, not one.
- Reference exact UI labels from the story / Analyst input verbatim ("Connect to Customer
  button", "Social Channels column", "snackbar") — never "the link" or "the button".
- ENVIRONMENT-FIRST: step 1 names the DMS + environment (and dealer ID when known), e.g.
  "Go to ASPEN QA environment." or "Login to TargetCRM as <role> on Dealer <id> on the QA environment.",
  followed by one step per setup action (open the module, create/locate the required record, set
  the required data state). Do NOT skip any setup step and do NOT summarize them into a preconditions line.
- These steps render as the repro bullet list; `actual` is rendered below them under a bold
  "OBSERVATION:" header, so keep the steps to what the tester DOES — leave the broken result for `actual`.

## EXPECTED vs ACTUAL (two short, definitive strings)
- `expected`: pulled from Analyst's expected behavior. Definitive ("The Save button is
  disabled.") — never "should be" or "may". This becomes the bug's acceptance line, rendered as
  "I know this bug is resolved when: <expected>", so phrase it as the correct resolved state.
- `actual`:   pulled from observed behavior (provided by user, or Playwright trace). This becomes
  the bold "OBSERVATION:" of what really happens — concrete and definitive, not "the page is wrong".

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
    "preconditions": "",
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
        progress=None,
        emit=None,
    ) -> list[GeneratedBug]:
        prompt = self._build_user_prompt(analysis, work_item, actual_behavior, code_context)
        data = await complete_json(
            self._llm, SYSTEM_PROMPT, prompt, opener="[",
            progress=progress, emit=emit, label="Bug Writer",
        )
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

        # QA-supplied answers to the Analyst's open questions — authoritative clarifications.
        answered = [
            (q, a.question_answers[i].strip())
            for i, q in enumerate(a.open_questions)
            if i < len(a.question_answers) and a.question_answers[i].strip()
        ]
        if answered:
            lines.append("")
            lines.append("## Clarifications (QA answers to the Analyst's open questions — treat as authoritative)")
            for q, ans in answered:
                lines.append(f"- Q: {q}")
                lines.append(f"  A: {ans}")

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
