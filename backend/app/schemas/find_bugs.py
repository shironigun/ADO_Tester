from __future__ import annotations

from datetime import datetime

from app.models import BugDraftStatus, BugSessionStatus, BugSeverity
from app.schemas.base import CamelModel


class BugStartRequest(CamelModel):
    work_item_id: int


class BugStartResponse(CamelModel):
    session_id: int


class BugRunRequest(CamelModel):
    # Manual description path (retained): observed actual behavior + optional pasted code.
    actual_behavior: str = ""
    code_context: str | None = None
    # Code-analysis path: verify the story against generated test cases by reading the repo.
    verify_code: bool = False
    commit_ids: list[str] = []   # manually-entered commit SHAs to include
    pr_ids: list[str] = []       # manually-entered PR ids to include
    selected_test_cases: list[str] = []  # case keys to verify (empty = all attached cases)


class BugLinkedChange(CamelModel):
    """A linked PR/commit with a title + browser URL, for display in the verify UI."""
    kind: str   # "pr" | "commit"
    id: str
    title: str
    url: str


class BugLinkedRefs(CamelModel):
    """Commits/PRs linked to the story (auto-fetched), to prefill the verify UI."""
    pull_request_ids: list[str] = []
    commit_shas: list[str] = []
    changes: list[BugLinkedChange] = []


class BugAnalysisUpdate(CamelModel):
    """User-edited Analyst output, saved back over the session's analysis_json."""
    module: str = ""
    feature: str = ""
    summary: str = ""
    happy_path: str = ""
    acceptance_criteria: list[str] = []
    business_rules: list[str] = []
    edge_cases: list[str] = []
    negative_cases: list[str] = []
    permissions: list[str] = []
    cross_flow: list[str] = []
    open_questions: list[str] = []
    question_answers: list[str] = []  # answers aligned by index to open_questions


class BugDraftSchema(CamelModel):
    id: int
    session_id: int
    title: str
    severity: BugSeverity
    preconditions: str
    repro_steps_json: str
    expected_behavior: str
    actual_behavior: str
    suggested_fix: str
    status: BugDraftStatus
    ado_bug_id: int | None
    sort_order: int


class BugSessionDetailSchema(CamelModel):
    id: int
    project_id: int
    work_item_id: int
    work_item_title: str
    status: BugSessionStatus
    current_activity: str | None
    analysis_json: str | None
    actual_behavior: str | None
    code_context: str | None
    error_message: str | None
    event_log_json: str | None = None
    code_verification_json: str | None = None
    created_at: datetime
    updated_at: datetime
    drafts: list[BugDraftSchema] = []


class BugSessionSummary(CamelModel):
    id: int
    work_item_id: int
    work_item_title: str
    status: BugSessionStatus
    created_at: datetime
    error_message: str | None


class BugSessionList(CamelModel):
    total: int
    items: list[BugSessionSummary]


class UpdateBugDraftRequest(CamelModel):
    title: str | None = None
    severity: BugSeverity | None = None
    preconditions: str | None = None
    repro_steps_json: str | None = None
    expected_behavior: str | None = None
    actual_behavior: str | None = None
    suggested_fix: str | None = None
    status: BugDraftStatus | None = None


class BugTestCaseStep(CamelModel):
    action: str = ""
    expected: str = ""


class BugTestCase(CamelModel):
    """An attached test case for the verification report (the pre-run list). No verdict here —
    the frontend overlays verdicts from the session's codeVerificationJson."""
    key: str               # stable id for selection (e.g. "plan#12345" or "draft|<title>")
    title: str
    preconditions: str = ""
    source: str            # "plan" | "draft"
    ado_test_case_id: int | None = None
    steps: list[BugTestCaseStep] = []


class BugFromVerdictStep(CamelModel):
    action: str = ""
    expected: str = ""
    status: str = "inconclusive"  # pass | fail | inconclusive
    actual: str = ""


class BugFromVerdictRequest(CamelModel):
    """Create ONE bug draft from a single verified (failed) test case."""
    test_case: str = ""
    source: str | None = None
    ado_test_case_id: int | None = None
    severity: BugSeverity = BugSeverity.Medium
    evidence: str = ""
    reasoning: str = ""
    failed_step_index: int | None = None
    steps: list[BugFromVerdictStep] = []
    mode: str | None = None  # "instant" | "agent"; defaults to the configured setting
