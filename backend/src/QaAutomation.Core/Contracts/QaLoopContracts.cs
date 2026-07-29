using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// Request/response DTOs for the QA Loop API. camelCase wire via global STJ policy;
// enum Status fields serialize by exact name via JsonStringEnumConverter.

// ── Scan / Session ────────────────────────────────────────────────────────────

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
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public bool AutoHandoff { get; set; } = false;
    /// <summary>Free-text at scan start — steers the Analyzer and generators (what to focus on / skip).</summary>
    public string? AdditionalContext { get; set; }
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
    public string? FrontendModule { get; set; }
    public string? FrontendPath { get; set; }
    public string? SourceDirsJson { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<ApiTestDraftDto> Drafts { get; set; } = [];

    public static ApiSessionDetail From(ApiTestSession s, IEnumerable<ApiTestDraft> drafts) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, Module = s.Module, Repository = s.Repository,
        Branch = s.Branch, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        TestPlanId = s.TestPlanId, TestSuiteId = s.TestSuiteId,
        FrontendModule = s.FrontendModule, FrontendPath = s.FrontendPath, SourceDirsJson = s.SourceDirsJson,
        Status = s.Status,
        InventoryJson = s.InventoryJson, AnalysisJson = s.AnalysisJson, EventLogJson = s.EventLogJson,
        AdditionalContext = s.AdditionalContext, GenerateContext = s.GenerateContext, LastFeedback = s.LastFeedback,
        HasUiPhase = s.HasUiPhase, UiStatus = s.UiStatus?.ToString(),
        ErrorMessage = s.ErrorMessage, AutoHandoff = s.AutoHandoff, ActivityEnabled = s.ActivityEnabled,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        Drafts = drafts.OrderBy(d => d.SortOrder).Select(ApiTestDraftDto.From).ToList(),
    };
}
