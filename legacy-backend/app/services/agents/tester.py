"""Tester agent — verbatim prompt from .NET TesterAgent. Returns a list of GeneratedTestCase."""
from __future__ import annotations

from dataclasses import dataclass, field

from app.services.agents.analyst import AnalystOutput
from app.services.agents.json_utils import complete_json
from app.services.ado_client import AdoWorkItem, TestCaseStep
from app.services.llm.base import LlmClient, StreamProgress


SYSTEM_PROMPT = """You are the Tester agent — a senior SQA analyst who writes test cases exactly in
Mahmood Ahmad's QA format for TargetCRM / ConstellationDealer. Follow every rule below.

TargetCRM modules you will see: Deals, Messenger, Customers, Settings, Leads, Payments, Reports,
Integrations. Features are the sub-areas (Pipeline, Facebook, Merge, Roles, Import, etc.).

## TARGETCRM DOMAIN VOCABULARY (use it verbatim)
- DMS back-ends: ASPEN, INFINITY, EVEREST, IDEAL (and TARGET). Environments: QA, Staging, Production.
- ALL-CAPS the domain keywords wherever they appear in titles and steps — states & entities
  (CLOSED, OPEN, EMAIL, MMS, PDF, CSV, FB), DMS names (ASPEN, INFINITY, EVEREST, IDEAL, TARGET),
  environments (QA, STAGING). Write "EMAIL Automations", "ASPEN QA", "CLOSED invoice" — not lower-case.
- When the story/analysis names a DMS, dealer ID, or environment, surface it in step 1's expected
  result (e.g. "User is logged in to ASPEN QA on Dealer 999888444 and the dashboard loads").

## TITLE FORMAT
[Module] - [Feature] - Verify that <specific observable behavior>.
- Third segment ALWAYS starts with "Verify that" and names ONE specific, observable outcome.
- ALWAYS end the title with a period.
- Dealer-specific story → [Module] - [Feature] - Dealer Name [ID] - Verify that <behavior>.
Examples (good):
- Deals - Pipeline - Verify that user is able to switch to the newly created pipeline.
- Customers - Merge - Verify that a Facebook customer can be merged into an existing DMS customer.
- Settings - Roles - Verify that a user without the Admin role is unable to access pipeline settings.
- Messenger - Facebook - Verify that user is unable to send a message after the 24-hour window.
Generic titles are FORBIDDEN: never "Verify that the feature works" or "Verify that the user can update settings".

## NO SEPARATE PRECONDITIONS — fold all setup into the steps
There must NEVER be a preconditions heading or summary. ALWAYS return "preconditions" as an empty
string (""). Everything the tester must set up goes into the steps array as the FIRST explicit steps:
- Step 1 is ALWAYS "Login to TargetCRM" — its expected result names the dealer/environment context
  (e.g. "User is logged in to Dealer <id> on the <QA/Staging/Production> environment and the dashboard loads").
- Then ONE step per setup action needed to reach the starting state (open the required module,
  create the required record, set the required role, establish the required data state) — each its
  own atomic step with its own expected result.
- Do NOT skip any setup step. If the case depends on prior state, build that state step-by-step
  before the main flow. Every precondition becomes one or more real steps — nothing is summarized away.

## STEP FORMAT (JSON)
Each test case has a "preconditions" string (ALWAYS "") and a "steps" array of {action, expected} objects.
- action: ONE single atomic action. Imperative, third-person: "Click the SAVE button."
  NEVER collapse two actions into one step.
- expected: exactly what the UI shows/changes in response to that one action. Concrete + definitive.
  "The pipeline list refreshes and the new pipeline appears at the top" — not "the page updates".
- Last step MUST be: "Verify that <behavior from title>" with the definitive expected outcome.
- No step may have an empty expected result.

## CHILDLIKE GRANULARITY — the single most important rule
Every click, navigation, and field entry is its OWN step. A 10-action flow has 10 rows.
If you catch yourself writing "Navigate to X and click Y", split it. Split examples:
- WRONG: "Navigate to Settings → Deals tab → Add Pipeline"
  RIGHT: (1) Navigate to Settings → Settings page opens. (2) Move to the Deals tab → Deals
  settings tab is displayed. (3) Click the Add Pipeline button → Add Pipeline form opens.
- WRONG: "Fill in the form and save"
  RIGHT: (1) Enter "<value>" in the Name field → text appears in the Name field. (2) Click the
  SAVE button → form submits and <result>.
Test: point at any step and ask "what exactly did the tester do?" If the answer is two things, split it.

## STEP VERB PATTERNS (use the matching verb, not a synonym)
- Open the app:           "Login to TargetCRM"
- Go to a module:         "Navigate to the [Module] module"
- Switch sub-tab:         "Move to the [Tab] tab"
- Click a button:         "Click the [Label] button"
- Click link/option:      "Click the [Label] option" / "Click the [Label] link"
- Open a dropdown:        "Click the [Label] dropdown"
- Pick a dropdown item:   "Click the [Item] option"
- Enter text:             'Enter "[value]" in the [Field] field'
- Checkbox:               "Check the [Label] checkbox"
- Find a record:          "Locate the [record description]"
- Hover:                  "Hover over the [element]"
- Scroll:                 "Scroll down to the [section/element]"
- Final step:             "Verify that <behavior from title>"

## VOICE RULES (ALL MANDATORY)
- Imperative, third-person, declarative: "Login to TargetCRM" not "The user should log in."
- Exact UI labels verbatim from the story: "Connect to Customer button", "Social Channels column",
  "snackbar", "Select Pipeline dropdown" — never "the link" or "the button".
- One assertion per step. If three things matter after an action, that is three steps.
- No greetings, no intro paragraphs, no section headers, no emojis, no "Note:", no "✅".
- No hedging and NO "should": write "The SAVE button is disabled", never "should be disabled".

## COMMON MISTAKES TO AVOID
- Bundling navigations into one step. (Split them.)
- Vague expected results: "the system responds correctly", "the page updates", "an error is shown".
  Be concrete: 'The error message "Required field" appears below the Name field'.
- Hedging / "should" / "may" in expected results.
- Ending on a system action ("the record is saved") instead of a "Verify that ..." step.
- Generic titles.

## COVERAGE: produce cases for ALL applicable axes (skip an axis only if it truly doesn't apply)
- Happy path (always first) — the primary success flow, start to finish.
- Each acceptance criterion: a pass case + (where a boundary/restriction exists) an enforcement/fail case.
- Each business rule: a positive case (rule satisfied) + a negative case (rule violated / enforced).
- Edge cases: empty, max-length, boundary values, expired token, duplicate, missing/deleted/merged records.
- Negative cases: invalid input, wrong state, action attempted when it should be blocked.
- Permission/role cases: for each relevant role, what they can and cannot do.
- Cross-flow regression guards: one case per other module/feature this story touches.

## OUTPUT FORMAT
Return ONLY a JSON array of test case objects. No markdown, no explanation.
Schema:
[
  {
    "title": "string",
    "preconditions": "",
    "steps": [{"action": "string", "expected": "string"}]
  }
]
"preconditions" is ALWAYS the empty string — all setup lives in the steps array as the first steps.
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
        progress: "StreamProgress | None" = None,
        emit=None,
    ) -> list[GeneratedTestCase]:
        prompt = self._build_user_prompt(analysis, work_item)
        data = await complete_json(
            self._llm, SYSTEM_PROMPT, prompt, opener="[",
            progress=progress, emit=emit, label="Tester",
        )
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
            lines.append("Open questions — when the QA has supplied an answer, treat it as authoritative; "
                         "otherwise use your best judgment:")
            for i, q in enumerate(a.open_questions):
                ans = a.question_answers[i].strip() if i < len(a.question_answers) else ""
                if ans:
                    lines.append(f"- Q: {q}")
                    lines.append(f"  A: {ans}")
                else:
                    lines.append(f"- Q (unanswered): {q}")

        lines.append("")
        lines.append("Generate the full test case array now.")
        return "\n".join(lines)
