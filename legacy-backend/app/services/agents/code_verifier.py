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


# Pass 2 (optional) — per-STEP verdicts. Runs only when settings ask for step-level detail.
# A batch of test cases is judged in one call (the code context is shared), returning, per case,
# one verdict per step IN ORDER. {ACTUAL_RULE} is filled depending on the detail level.
STEP_SYSTEM_PROMPT = """You are the Code Verifier agent — a senior SQA engineer doing STATIC code review
for TargetCRM / ConstellationDealer. You are given a user story, several test cases (each with numbered
steps as "action → expected"), and excerpts of the ACTUAL repository code. You do NOT run anything —
you reason about the code as written.

For EVERY step of EVERY test case, decide — based ONLY on the provided code — whether the code satisfies
that step's expected result:
- "pass"         — the code clearly implements what the step asserts.
- "fail"         — the code clearly contradicts or is missing it (a real defect signal).
- "inconclusive" — the provided code is insufficient to judge (prefer this over guessing).

Rules:
- Base every verdict ONLY on the provided code. Never invent files, functions, or behavior.
- Be conservative: only "fail" when the code genuinely shows the defect; otherwise "inconclusive".
- {ACTUAL_RULE}

Output ONLY a JSON array (no markdown fences, no prose), one object per test case, in the SAME order and
numbering you were given. Each object:
[
  {
    "case": 1,
    "steps": [
      { "status": "pass | fail | inconclusive", "actual": "string" }
    ]
  }
]
Each "steps" array MUST have EXACTLY one item per step of that test case, in the given order.
"""

_ACTUAL_RULE_FULL = (
    "For \"actual\", write a concise, code-grounded note of what the code actually does at that step "
    "(name the file). Fill it for every step."
)
_ACTUAL_RULE_LITE = (
    "For \"actual\", write a one-line, code-grounded note ONLY for steps you mark \"fail\" or "
    "\"inconclusive\"; use an empty string \"\" for \"pass\" steps."
)


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
class StepVerdict:
    """Per-step result of a test case during code verification (pass 2)."""
    action: str = ""
    expected: str = ""
    status: str = "inconclusive"  # pass | fail | inconclusive
    actual: str = ""

    @staticmethod
    def from_result(step: dict, result: dict | None) -> "StepVerdict":
        """Combine a source step ({action, expected}) with the LLM's per-step result."""
        r = result or {}
        status = str(r.get("status") or "inconclusive").strip().lower()
        if status not in {"pass", "fail", "inconclusive"}:
            status = "inconclusive"
        return StepVerdict(
            action=str(step.get("action") or step.get("Action") or ""),
            expected=str(step.get("expected") or step.get("Expected") or ""),
            status=status,
            actual=str(r.get("actual") or ""),
        )

    def to_dict(self) -> dict:
        return {"action": self.action, "expected": self.expected,
                "status": self.status, "actual": self.actual}


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

    async def verify_steps(
        self,
        work_item: AdoWorkItem,
        analysis: AnalystOutput,
        cases: list[TestCaseInput],
        code_context: str,
        *,
        full: bool,
        progress=None,
        emit=None,
    ) -> list[list[StepVerdict]]:
        """Pass 2 — judge EACH step of each given case against the code. `cases` is a batch
        (the code context is shared across them). Returns one list[StepVerdict] per input case,
        in the same order. On any parse trouble a case degrades to all-inconclusive steps rather
        than raising, so the report always renders."""
        if not cases:
            return []
        system = STEP_SYSTEM_PROMPT.replace("{ACTUAL_RULE}", _ACTUAL_RULE_FULL if full else _ACTUAL_RULE_LITE)
        prompt = self._build_steps_prompt(work_item, analysis, cases, code_context)
        raw = await self._llm.complete(system, prompt, progress=progress, emit=emit)

        # Key step-lists by the model's 1-based `case` number when it's a valid in-range int; also
        # keep them in array order so we can fall back to positional mapping if the numbering drifts.
        by_case: dict[int, list[dict]] = {}
        positional: list[list[dict]] = []
        try:
            data = json.loads(strip_fences(raw, opener="["))
            if isinstance(data, list):
                for item in data:
                    if not isinstance(item, dict):
                        continue
                    raw_steps = item.get("steps")
                    steps = [s for s in raw_steps if isinstance(s, dict)] if isinstance(raw_steps, list) else []
                    positional.append(steps)
                    try:
                        num = int(item.get("case"))
                    except (TypeError, ValueError):
                        continue
                    if 1 <= num <= len(cases):
                        by_case[num] = steps
        except json.JSONDecodeError:
            pass
        # No usable case numbers but the array lines up 1:1 → map by position.
        if not by_case and len(positional) == len(cases):
            by_case = {i + 1: positional[i] for i in range(len(cases))}

        # One StepVerdict list per INPUT case, in order (a stepless case yields []), so the caller's
        # positional zip stays aligned no matter which cases carried steps.
        out: list[list[StepVerdict]] = []
        for i, case in enumerate(cases, start=1):
            results = by_case.get(i, [])
            out.append([
                StepVerdict.from_result(step, results[j] if j < len(results) else None)
                for j, step in enumerate(case.steps)
            ])
        return out

    @staticmethod
    def _build_steps_prompt(
        wi: AdoWorkItem, a: AnalystOutput, cases: list[TestCaseInput], code_context: str,
    ) -> str:
        lines: list[str] = []
        lines.append(f"# Story #{wi.id} — {wi.title}")
        if a.module or a.feature:
            lines.append(f"Module: {a.module} · Feature: {a.feature}")
        lines.append("")
        lines.append("## Test cases (judge every step)")
        for i, tc in enumerate(cases, 1):
            lines.append(f"### Case {i}: {tc.title}")
            if tc.preconditions:
                lines.append(f"preconditions: {tc.preconditions}")
            for j, st in enumerate(tc.steps, 1):
                act = str(st.get("action") or st.get("Action") or "")
                exp = str(st.get("expected") or st.get("Expected") or "")
                lines.append(f"  {j}. {act} → {exp}")
        lines.append("")
        lines.append("## Actual repository code (excerpts)")
        lines.append(code_context or "(no code retrieved)")
        lines.append("")
        lines.append("Return ONLY the JSON array of per-case, per-step verdicts described in the system prompt.")
        return "\n".join(lines)

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
