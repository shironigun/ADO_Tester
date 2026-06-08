"""Tester agent — verbatim prompt from .NET TesterAgent. Returns a list of GeneratedTestCase."""
from __future__ import annotations

import json
from dataclasses import dataclass, field

from app.services.agents.analyst import AnalystOutput
from app.services.agents.json_utils import strip_fences
from app.services.ado_client import AdoWorkItem, TestCaseStep
from app.services.llm.base import LlmClient


SYSTEM_PROMPT = """You are the Tester agent — a senior SQA analyst who writes test cases exactly in
Mahmood Ahmad's QA format for TargetCRM / ConstellationDealer. Follow every rule below.

## TITLE FORMAT
[Module] - [Feature] - Verify that <specific observable behavior>.
Third segment ALWAYS starts with "Verify that".
Examples:
- Deals - Pipeline - Verify that user is able to switch to the newly created pipeline.
- Settings - Roles - Verify that a user without the Admin role cannot access pipeline settings.

## STEP FORMAT (JSON)
Each test case has a "preconditions" string and a "steps" array of {action, expected} objects.
- action: one single atomic action. Imperative, third-person: "Click the SAVE button."
  NEVER collapse two actions into one step.
- expected: exactly what the UI shows/changes in response to that one action. Definitive.
  "The SAVE button is disabled" NOT "The button should be disabled."
- Last step MUST be: "Verify that <behavior from title>" with the definitive outcome.
- No step may have an empty expected result.

## VOICE RULES (ALL MANDATORY)
- Childlike granularity: every click, navigation, and field entry = its own step.
  "Navigate to Settings → Deals tab → Add Pipeline" = 3 steps, not 1.
- Imperative third-person: "Login to TargetCRM" not "The user should log in."
- Exact UI labels verbatim from the story. "Connect to Customer button" not "the link."
- One assertion per step.
- No greetings, no intro paragraphs, no section headers, no emojis, no "Note:", no "✅".
- No hedging. Results are definitive.

## COVERAGE: produce cases for ALL applicable axes
- Happy path (always first)
- Each acceptance criterion: pass case + enforcement/boundary case
- Each business rule: positive + negative
- Edge cases (empty, max-length, boundary values, missing/deleted data)
- Negative cases (invalid input, blocked actions)
- Permission/role cases if relevant
- Cross-flow regression guards if other modules are touched

## OUTPUT FORMAT
Return ONLY a JSON array of test case objects. No markdown, no explanation.
Schema:
[
  {
    "title": "string",
    "preconditions": "string",
    "steps": [{"action": "string", "expected": "string"}]
  }
]
"""


@dataclass
class GeneratedTestCase:
    title: str = ""
    preconditions: str = ""
    steps: list[TestCaseStep] = field(default_factory=list)


class TesterAgent:
    def __init__(self, llm: LlmClient) -> None:
        self._llm = llm

    async def generate(
        self, analysis: AnalystOutput, work_item: AdoWorkItem,
    ) -> list[GeneratedTestCase]:
        prompt = self._build_user_prompt(analysis, work_item)
        raw = await self._llm.complete(SYSTEM_PROMPT, prompt)
        cleaned = strip_fences(raw, opener="[")
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"Tester returned invalid JSON: {e}") from None
        if not isinstance(data, list):
            raise RuntimeError("Tester returned non-array JSON.")

        out: list[GeneratedTestCase] = []
        for item in data:
            if not isinstance(item, dict):
                continue
            steps_raw = item.get("steps") or item.get("Steps") or []
            steps: list[TestCaseStep] = []
            if isinstance(steps_raw, list):
                for s in steps_raw:
                    if isinstance(s, dict):
                        steps.append(TestCaseStep(
                            action=str(s.get("action") or s.get("Action") or ""),
                            expected=str(s.get("expected") or s.get("Expected") or ""),
                        ))
            out.append(GeneratedTestCase(
                title=str(item.get("title") or item.get("Title") or ""),
                preconditions=str(item.get("preconditions") or item.get("Preconditions") or ""),
                steps=steps,
            ))
        return out

    @staticmethod
    def _build_user_prompt(a: AnalystOutput, wi: AdoWorkItem) -> str:
        lines: list[str] = []
        lines.append(f"Work item #{wi.id}: {wi.title}")
        lines.append("")
        lines.append(f"Module: {a.module}")
        lines.append(f"Feature: {a.feature}")
        lines.append("")
        lines.append(f"Summary: {a.summary}")
        lines.append("")
        lines.append(f"Happy path: {a.happy_path}")

        def append_list(label: str, items: list[str]) -> None:
            if not items:
                return
            lines.append("")
            lines.append(f"{label}:")
            for i in items:
                lines.append(f"- {i}")

        append_list("Acceptance criteria", a.acceptance_criteria)
        append_list("Business rules", a.business_rules)
        append_list("Edge cases", a.edge_cases)
        append_list("Negative cases", a.negative_cases)
        append_list("Permissions", a.permissions)
        append_list("Cross-flow", a.cross_flow)
        if a.open_questions:
            lines.append("")
            lines.append("Open questions (use your best judgment):")
            for q in a.open_questions:
                lines.append(f"- {q}")

        lines.append("")
        lines.append("Generate the full test case array now.")
        return "\n".join(lines)
