"""
Code Verifier agent — static analysis of the repo implementation against generated test cases.

Given the Analyst's expected behavior, the generated test cases (Author-Tests), and a bounded
slice of the actual repository code, the agent reasons (WITHOUT executing anything) about whether
the implementation satisfies each test case, returning a per-test-case verdict with evidence.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field

from app.services.agents.analyst import AnalystOutput
from app.services.agents.json_utils import strip_fences
from app.services.ado_client import AdoWorkItem
from app.services.llm.base import LlmClient


SYSTEM_PROMPT = """You are the Code Verifier agent — a senior SQA engineer doing STATIC code review
for TargetCRM / ConstellationDealer. You are given a user story, its expected behavior, a set of
test cases, and excerpts of the ACTUAL repository code (React/TypeScript frontend and/or .NET C#
backend). You do NOT run anything — you reason about the code as written.

For EACH test case, decide whether the supplied code satisfies it:
- "pass"         — the code clearly implements the behavior the test case asserts.
- "fail"         — the code clearly contradicts / is missing the asserted behavior (a real defect signal).
- "inconclusive" — the supplied code does not contain enough to judge (do NOT guess; prefer this when unsure).

Rules:
- Base every verdict ONLY on the provided code. Never invent files, functions, or behavior.
- Cite concrete evidence: file path (and symbol/line if visible) + a one-line reason. If inconclusive,
  say what code would be needed.
- Be conservative: only "fail" when the code genuinely shows the defect; otherwise "inconclusive".

Output ONLY a JSON array (no markdown fences, no prose). Each item:
[
  {
    "testCase": "string — the test case title verbatim",
    "status": "pass | fail | inconclusive",
    "evidence": "string — file path + brief proof (e.g. 'customer/ImportWizard.tsx: Save enabled with empty name')",
    "reasoning": "string — one or two sentences"
  }
]
If a MANUAL DESCRIPTION is provided, add one extra item with testCase = "Manual: <short>" assessing it.
"""


@dataclass
class CodeVerdict:
    test_case: str = ""
    status: str = "inconclusive"  # pass | fail | inconclusive
    evidence: str = ""
    reasoning: str = ""

    @staticmethod
    def from_dict(d: dict) -> "CodeVerdict":
        status = str(d.get("status") or "inconclusive").strip().lower()
        if status not in {"pass", "fail", "inconclusive"}:
            status = "inconclusive"
        return CodeVerdict(
            test_case=str(d.get("testCase") or d.get("test_case") or ""),
            status=status,
            evidence=str(d.get("evidence") or ""),
            reasoning=str(d.get("reasoning") or ""),
        )

    def to_dict(self) -> dict:
        return {
            "testCase": self.test_case, "status": self.status,
            "evidence": self.evidence, "reasoning": self.reasoning,
        }


@dataclass
class TestCaseInput:
    title: str = ""
    preconditions: str = ""
    steps: list[dict] = field(default_factory=list)  # [{action, expected}]


class CodeVerifierAgent:
    def __init__(self, llm: LlmClient) -> None:
        self._llm = llm

    async def verify(
        self,
        work_item: AdoWorkItem,
        analysis: AnalystOutput,
        test_cases: list[TestCaseInput],
        code_context: str,
        manual_description: str | None = None,
        progress=None,
        emit=None,
    ) -> list[CodeVerdict]:
        prompt = self._build_prompt(work_item, analysis, test_cases, code_context, manual_description)
        raw = await self._llm.complete(SYSTEM_PROMPT, prompt, progress=progress, emit=emit)
        cleaned = strip_fences(raw, opener="[")
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"Code Verifier returned invalid JSON: {e}") from None
        if not isinstance(data, list):
            raise RuntimeError("Code Verifier returned non-array JSON.")
        return [CodeVerdict.from_dict(x) for x in data if isinstance(x, dict)]

    @staticmethod
    def _build_prompt(
        wi: AdoWorkItem, a: AnalystOutput, test_cases: list[TestCaseInput],
        code_context: str, manual_description: str | None,
    ) -> str:
        lines: list[str] = []
        lines.append(f"# Story #{wi.id} — {wi.title}")
        if a.module or a.feature:
            lines.append(f"Module: {a.module} · Feature: {a.feature}")
        if a.summary:
            lines.append(f"Expected (summary): {a.summary}")
        if a.happy_path:
            lines.append(f"Happy path: {a.happy_path}")

        lines.append("")
        lines.append("## Test cases to verify")
        if not test_cases:
            lines.append("(No generated test cases found — verify the story's expected behavior instead.)")
        for i, tc in enumerate(test_cases, 1):
            lines.append(f"{i}. {tc.title}")
            if tc.preconditions:
                lines.append(f"   preconditions: {tc.preconditions}")
            for st in tc.steps[:12]:
                act = str(st.get("action") or st.get("Action") or "")
                exp = str(st.get("expected") or st.get("Expected") or "")
                lines.append(f"   - {act} → {exp}")

        if manual_description and manual_description.strip():
            lines.append("")
            lines.append("## Manual description (observed behavior to also assess)")
            lines.append(manual_description.strip())

        lines.append("")
        lines.append("## Actual repository code (excerpts)")
        lines.append(code_context or "(no code retrieved)")

        lines.append("")
        lines.append("Return ONLY the JSON array of verdicts described in the system prompt.")
        return "\n".join(lines)
