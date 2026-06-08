"""Pydantic request/response schemas — camelCase JSON keys to match the .NET API."""
from app.schemas.ado import AdoWorkItemSchema, WorkItemOption
from app.schemas.author_tests import (
    AcceptedResponse,
    ApproveAllResponse,
    CancelResponse,
    PushRequest,
    StartRequest,
    StartResponse,
    TestCaseDraftSchema,
    TestPlanSchema,
    TestSessionDetailSchema,
    TestSessionList,
    TestSessionSummary,
    TestSuiteSchema,
    UpdateDraftRequest,
)
from app.schemas.base import CamelModel
from app.schemas.diagnostics import DiagnosticsStatus, LlmPingResponse, ProviderInfo, SetProviderRequest
from app.schemas.find_bugs import (
    BugDraftSchema,
    BugLinkedRefs,
    BugRunRequest,
    BugSessionDetailSchema,
    BugSessionList,
    BugSessionSummary,
    BugStartRequest,
    BugStartResponse,
    UpdateBugDraftRequest,
)
from app.schemas.knowledgebase import KnowledgebaseDocumentSchema, KnowledgebaseHitSchema
from app.schemas.reports import GenerateReportRequest, GenerateReportResponse, ReportSchema

__all__ = [
    "AcceptedResponse",
    "AdoWorkItemSchema",
    "ApproveAllResponse",
    "BugDraftSchema",
    "BugLinkedRefs",
    "BugRunRequest",
    "BugSessionDetailSchema",
    "BugSessionList",
    "BugSessionSummary",
    "BugStartRequest",
    "BugStartResponse",
    "CamelModel",
    "CancelResponse",
    "DiagnosticsStatus",
    "GenerateReportRequest",
    "GenerateReportResponse",
    "KnowledgebaseDocumentSchema",
    "KnowledgebaseHitSchema",
    "LlmPingResponse",
    "ProviderInfo",
    "PushRequest",
    "ReportSchema",
    "SetProviderRequest",
    "StartRequest",
    "StartResponse",
    "TestCaseDraftSchema",
    "TestPlanSchema",
    "TestSessionDetailSchema",
    "TestSessionList",
    "TestSessionSummary",
    "TestSuiteSchema",
    "UpdateBugDraftRequest",
    "UpdateDraftRequest",
    "WorkItemOption",
]
