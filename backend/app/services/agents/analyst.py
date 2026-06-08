"""Analyst agent — ports the .NET AnalystAgent prompt + parsing verbatim."""
from __future__ import annotations

import json
from dataclasses import dataclass, field

from app.services.agents.json_utils import coerce_string, coerce_string_list, strip_fences
from app.services.knowledgebase import KnowledgebaseHit
from app.services.llm.base import LlmClient, StreamProgress
from app.services.ado_client import AdoWorkItem


SYSTEM_PROMPT = """You are the Analyst agent for a QA automation system.

Given an Azure DevOps work item and relevant domain knowledge chunks, produce a structured
expected-behavior analysis that the Tester agent will use to write test cases.

Work item fields provided: Title, Description, Acceptance Criteria, Work Item Type, State.
Domain knowledge: Markdown chunks from the project's knowledgebase (may be empty).

Your output MUST be a valid JSON object — no markdown fences, no explanation, JSON only.
Every list field MUST contain ONLY plain strings — never objects, never nested arrays.
Schema:
{
  "module":             "string — product module/area (e.g. 'Purchasing', 'Sales', 'Settings')",
  "feature":            "string — specific feature (e.g. 'Purchase Order Receiving Report')",
  "summary":            "string — one paragraph: what this story builds and why",
  "happyPath":          "string — the primary end-to-end success flow",
  "acceptanceCriteria": ["string — each AC as a clear, testable statement"],
  "businessRules":      ["string — stated and implied rules; each rule is one item"],
  "edgeCases":          ["string — boundary values, empty states, missing/deleted data"],
  "negativeCases":      ["string — invalid inputs, blocked actions, system enforcement"],
  "permissions":        ["string — role/permission considerations; empty array if none"],
  "crossFlow":          ["string — other modules/features this story touches; empty if none"],
  "openQuestions":      ["string — genuine ambiguities in the story; empty if none"]
}
"""


@dataclass
class AnalystOutput:
    module: str = ""
    feature: str = ""
    summary: str = ""
    happy_path: str = ""
    acceptance_criteria: list[str] = field(default_factory=list)
    business_rules: list[str] = field(default_factory=list)
    edge_cases: list[str] = field(default_factory=list)
    negative_cases: list[str] = field(default_factory=list)
    permissions: list[str] = field(default_factory=list)
    cross_flow: list[str] = field(default_factory=list)
    open_questions: list[str] = field(default_factory=list)

    def to_json(self) -> str:
        return json.dumps({
            "module": self.module,
            "feature": self.feature,
            "summary": self.summary,
            "happyPath": self.happy_path,
            "acceptanceCriteria": self.acceptance_criteria,
            "businessRules": self.business_rules,
            "edgeCases": self.edge_cases,
            "negativeCases": self.negative_cases,
            "permissions": self.permissions,
            "crossFlow": self.cross_flow,
            "openQuestions": self.open_questions,
        })

    @staticmethod
    def from_dict(d: dict) -> "AnalystOutput":
        # Accept any casing the LLM happened to use.
        lower = {k.lower(): v for k, v in d.items()}

        def gets(key: str) -> str:
            return coerce_string(lower.get(key.lower(), ""))

        def getl(*keys: str) -> list[str]:
            for k in keys:
                if k.lower() in lower:
                    return coerce_string_list(lower[k.lower()])
            return []

        return AnalystOutput(
            module=gets("module"),
            feature=gets("feature"),
            summary=gets("summary"),
            happy_path=gets("happyPath"),
            acceptance_criteria=getl("acceptanceCriteria", "acceptance_criteria"),
            business_rules=getl("businessRules", "business_rules"),
            edge_cases=getl("edgeCases", "edge_cases"),
            negative_cases=getl("negativeCases", "negative_cases"),
            permissions=getl("permissions"),
            cross_flow=getl("crossFlow", "cross_flow"),
            open_questions=getl("openQuestions", "open_questions"),
        )


class AnalystAgent:
    def __init__(self, llm: LlmClient) -> None:
        self._llm = llm

    async def analyze(
        self,
        work_item: AdoWorkItem,
        context: list[KnowledgebaseHit],
        progress: "StreamProgress | None" = None,
        emit=None,
    ) -> AnalystOutput:
        prompt = self._build_user_prompt(work_item, context)
        raw = await self._llm.complete(SYSTEM_PROMPT, prompt, progress=progress, emit=emit)
        cleaned = strip_fences(raw, opener="{")
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"Analyst returned invalid JSON: {e}") from None
        if not isinstance(data, dict):
            raise RuntimeError("Analyst returned non-object JSON.")
        return AnalystOutput.from_dict(data)

    @staticmethod
    def _build_user_prompt(wi: AdoWorkItem, context: list[KnowledgebaseHit]) -> str:
        lines: list[str] = []
        lines.append("## Work Item")
        lines.append(f"**ID:** {wi.id}")
        lines.append(f"**Type:** {wi.work_item_type}")
        lines.append(f"**Title:** {wi.title}")
        if wi.description and wi.description.strip():
            lines.append("**Description:**")
            lines.append(wi.description)
        if wi.acceptance_criteria and wi.acceptance_criteria.strip():
            lines.append("**Acceptance Criteria:**")
            lines.append(wi.acceptance_criteria)

        if context:
            lines.append("")
            lines.append("## Domain Knowledge (relevant chunks)")
            for hit in context:
                lines.append(f"### From: {hit.file_name}")
                lines.append(hit.content)

        return "\n".join(lines)
