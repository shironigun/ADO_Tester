using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// Request/response DTOs for the QA Loop API. camelCase wire via global STJ policy;
// enum Status fields serialize by exact name via JsonStringEnumConverter.

// ── Scan / Session ────────────────────────────────────────────────────────────

/// <summary>Scan-scoped markdown attachment (relative path + raw content). Not uploaded to KB.</summary>
public sealed class ContextAttachmentDto
{
    public string RelativePath { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ScanStartRequest
{
    public string Module { get; set; } = "";
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    /// <summary>Selected frontend module route (e.g. "/deals"); set by the module dropdown.</summary>
    public string? FrontendModule { get; set; }
    /// <summary>Repo-relative frontend component folder (e.g. "web-app/.../components/deals") — the
    /// scan reads its apis/ to trace the backend. Set by the module dropdown.</summary>
    public string? FrontendPath { get; set; }
    public List<string>? SourceDirs { get; set; }
    public List<string>? Extensions { get; set; }
    public int? WorkItemId { get; set; }
    /// <summary>Required — complete ADO Test Plan id for Plan Intake (INT-201).</summary>
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public bool AutoHandoff { get; set; } = false;
    /// <summary>Enables the optional, downstream UI Phase for this session.</summary>
    public bool HasUiPhase { get; set; }
    /// <summary>Free-text at scan start — steers the Analyzer and generators (what to focus on / skip).</summary>
    public string? AdditionalContext { get; set; }
    /// <summary>Markdown files attached at scan start — merged into scan context for analyzer/generators/UI author.</summary>
    public List<ContextAttachmentDto>? ContextAttachments { get; set; }
}

public sealed class ScanStartResponse
{
    public int SessionId { get; set; }
    /// <summary>Set when a non-terminal session for the same (project, module) already exists.</summary>
    public int? ExistingSessionId { get; set; }
}

/// <summary>Full-replace PATCH for the session's SurfaceMatrix/InventoryJson analysis.
/// Omitted fields take defaults and overwrite — NOT skip-null.</summary>
public sealed class ApiAnalysisUpdate
{
    public string Module { get; set; } = "";
    public List<SurfaceRow> Rows { get; set; } = [];

    public SurfaceMatrix ToMatrix() => new(Module, Rows);
}

/// <summary>Per-field skip-null PATCH for a session's plan/suite/work-item (null = leave unchanged).</summary>
public sealed class UpdateSessionPlanRequest
{
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public int? WorkItemId { get; set; }
}

/// <summary>Optional body for session push — dual API/UI lanes.</summary>
public sealed class QaLoopPushRequest
{
    /// <summary><c>api</c> or <c>ui</c>. Omitted → inferred from session status.</summary>
    public string? Lane { get; set; }
}

/// <summary>Per-field skip-null PATCH for a draft (null = leave unchanged).</summary>
public sealed class UpdateApiDraftRequest
{
    public ApiDraftStatus? Status { get; set; }
    public string? Title { get; set; }
    public string? Preconditions { get; set; }
    public string? StepsJson { get; set; }
    public string? SpecCode { get; set; }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class ApiTestDraftDto
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Level { get; set; } = "";
    public string Title { get; set; } = "";
    public string Preconditions { get; set; } = "";
    public string StepsJson { get; set; } = "[]";
    public string? SchemaJson { get; set; }
    public string SpecCode { get; set; } = "";
    public string? SpecFilePath { get; set; }
    public string? Tags { get; set; }
    public string? SourceRoutesJson { get; set; }
    public string? ErrorMessage { get; set; }
    public ApiDraftStatus Status { get; set; }
    public int? AdoTestCaseId { get; set; }
    public int SortOrder { get; set; }
    public string? DraftKind { get; init; }
    public string? Bucket { get; init; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static ApiTestDraftDto From(ApiTestDraft d) => new()
    {
        Id = d.Id, SessionId = d.SessionId, Level = d.Level.ToWire(),
        Title = d.Title, Preconditions = d.Preconditions, StepsJson = d.StepsJson,
        SchemaJson = d.SchemaJson, SpecCode = d.SpecCode, SpecFilePath = d.SpecFilePath,
        Tags = d.Tags, SourceRoutesJson = d.SourceRoutesJson, ErrorMessage = d.ErrorMessage,
        Status = d.Status, AdoTestCaseId = d.AdoTestCaseId, SortOrder = d.SortOrder,
        DraftKind = d.DraftKind?.ToWire(), Bucket = d.Bucket,
        CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
    };
}

public sealed class ApiSessionSummary
{
    public int Id { get; set; }
    public string Module { get; set; } = "";
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public ApiSessionStatus Status { get; set; }
    public int DraftCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public bool ActivityEnabled { get; set; }

    public static ApiSessionSummary From(ApiTestSession s, int draftCount = 0) => new()
    {
        Id = s.Id, Module = s.Module, Repository = s.Repository, Branch = s.Branch,
        Status = s.Status, DraftCount = draftCount, CreatedAt = s.CreatedAt,
        ErrorMessage = s.ErrorMessage, ActivityEnabled = s.ActivityEnabled,
    };
}

public sealed class ApiSessionList
{
    public int Total { get; set; }
    public IReadOnlyList<ApiSessionSummary> Items { get; set; } = [];
}

/// <summary>Request body for POST /sessions/{id}/rerun-analyze.</summary>
public sealed class RerunAnalyzeRequest { public string Feedback { get; set; } = ""; }

/// <summary>Per-draft feedback entry for RefineDraftsRequest.</summary>
public sealed class ApiDraftFeedback { public int DraftId { get; set; }  public string? Feedback { get; set; }  public bool Remove { get; set; } }

/// <summary>Request body for POST /sessions/{id}/refine-drafts.</summary>
public sealed class RefineDraftsRequest
{
    public string? OverallFeedback { get; set; }
    public List<ApiDraftFeedback> PerDraft { get; set; } = [];
}

/// <summary>Request body for POST /sessions/{id}/generate-drafts.</summary>
public sealed class GenerateDraftsRequest
{
    /// <summary>Free-text at the Analyze→Generate gate — steers all three generator agents.</summary>
    public string? GenerateContext { get; set; }
}

/// <summary>Request body for POST /sessions/{id}/author-ui-cases (currently empty — params come from session).</summary>
public sealed class AuthorUiCasesRequest { }

/// <summary>Request body for POST /sessions/{id}/heal-specs.</summary>
public sealed class HealSpecsRequest
{
    /// <summary>IDs of the UiScript drafts to heal. Each draft's ErrorMessage is used as the failure input.</summary>
    public int[] DraftIds { get; set; } = [];
    /// <summary>Optional user context appended to the failure message before healing (KBG-108).</summary>
    public string? UserContext { get; set; }
}

public sealed class ApiSessionDetail
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Module { get; set; } = "";
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public int? WorkItemId { get; set; }
    public string? WorkItemTitle { get; set; }
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    /// <summary>Serialized PlanSuiteUnitMap JSON (null until Plan Intake maps suites).</summary>
    public string? SuiteUnitMapJson { get; set; }
    /// <summary>Serialized PlanImportMeta JSON (null until a Plan import runs).</summary>
    public string? PlanImportMetaJson { get; set; }
    public string? FrontendModule { get; set; }
    public string? FrontendPath { get; set; }
    public string? SourceDirsJson { get; set; }
    public string? DiscoveryJson { get; set; }
    public ApiSessionStatus Status { get; set; }
    public string? InventoryJson { get; set; }
    public string? AnalysisJson { get; set; }
    public string? AdditionalContext { get; set; }
    public string? GenerateContext { get; set; }
    public string? LastFeedback { get; set; }
    public bool HasUiPhase { get; set; }
    public string? UiStatus { get; set; }
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    public bool AutoHandoff { get; set; }
    public bool ActivityEnabled { get; set; }
    // KBG-108: KB-grounded fields (KbContextJson + CodeScanJson excluded — too large)
    public string? FrsSlug { get; set; }
    public string? UiAnalysisJson { get; set; }
    public int? UiTestSuiteId { get; set; }
    public int? LastUiRunId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<ApiTestDraftDto> Drafts { get; set; } = [];

    public static ApiSessionDetail From(ApiTestSession s, IEnumerable<ApiTestDraft> drafts) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, Module = s.Module, Repository = s.Repository,
        Branch = s.Branch, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        TestPlanId = s.TestPlanId, TestSuiteId = s.TestSuiteId,
        SuiteUnitMapJson = s.SuiteUnitMapJson, PlanImportMetaJson = s.PlanImportMetaJson,
        FrontendModule = s.FrontendModule, FrontendPath = s.FrontendPath, SourceDirsJson = s.SourceDirsJson,
        DiscoveryJson = s.DiscoveryJson,
        Status = s.Status,
        InventoryJson = s.InventoryJson, AnalysisJson = s.AnalysisJson, EventLogJson = s.EventLogJson,
        AdditionalContext = s.AdditionalContext, GenerateContext = s.GenerateContext, LastFeedback = s.LastFeedback,
        HasUiPhase = s.HasUiPhase, UiStatus = s.UiStatus?.ToString(),
        ErrorMessage = s.ErrorMessage, AutoHandoff = s.AutoHandoff, ActivityEnabled = s.ActivityEnabled,
        // KBG-108: KB-grounded fields
        FrsSlug = s.FrsSlug, UiAnalysisJson = s.UiAnalysisJson,
        UiTestSuiteId = s.UiTestSuiteId, LastUiRunId = s.LastUiRunId,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        Drafts = drafts.OrderBy(d => d.SortOrder).Select(ApiTestDraftDto.From).ToList(),
    };
}

// ── KBG-108: new request/response contracts ───────────────────────────────────

/// <summary>Body for POST sessions/{id}/rerun-ui-analysis.</summary>
public sealed record RerunUiAnalysisRequest(string Feedback);

/// <summary>Body for PATCH sessions/{id}/ui-analysis — editable UI Analyst findings (camelCase JSON).</summary>
public sealed class UiAnalysisUpdateRequest
{
    public List<UiAnalystFeatureDto> Features { get; set; } = [];
    public List<UiAnalystFlowDto> Flows { get; set; } = [];
    public List<UiAnalystScreenDto> Screens { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> MustTest { get; set; } = [];
    public List<string> MustNotTest { get; set; } = [];
    public string AuthPermissionsSummary { get; set; } = "";
    public string TenantVariantsSummary { get; set; } = "";

    public QaLoopUiAnalystOutput ToOutput() => new(
        Features.Select(f => new UiAnalystFeature(f.Id ?? "", f.Name ?? "", f.Description ?? "")).ToArray(),
        Flows.Select(f => new UiAnalystFlow(f.Name ?? "", f.Trigger ?? "", f.Steps?.ToArray() ?? [], f.Breaks?.ToArray() ?? [])).ToArray(),
        Screens.Select(s => new UiAnalystScreen(s.Name ?? "", s.Route ?? "", s.Components?.ToArray() ?? [])).ToArray(),
        OpenQuestions.ToArray(),
        MustTest.ToArray(),
        MustNotTest.ToArray(),
        AuthPermissionsSummary ?? "",
        TenantVariantsSummary ?? "");
}

public sealed class UiAnalystFeatureDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class UiAnalystFlowDto
{
    public string? Name { get; set; }
    public string? Trigger { get; set; }
    public List<string>? Steps { get; set; }
    public List<string>? Breaks { get; set; }
}

public sealed class UiAnalystScreenDto
{
    public string? Name { get; set; }
    public string? Route { get; set; }
    public List<string>? Components { get; set; }
}

/// <summary>Body for POST sessions/{id}/explain-failure.</summary>
public sealed record ExplainFailureRequest(int DraftId);

/// <summary>Response from POST sessions/{id}/explain-failure.</summary>
public sealed record ExplainFailureResponse(int DraftId, string Explanation);

/// <summary>Body for PATCH sessions/{id}/ui-suite.</summary>
public sealed record PatchUiSuiteRequest(int? SuiteId);
