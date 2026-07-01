namespace QaAutomation.Core.Models;

/// <summary>One QA Loop scan+generate run. Table: ApiTestSessions.</summary>
public sealed class ApiTestSession
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Module { get; set; } = string.Empty;
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public int? WorkItemId { get; set; }
    public string? WorkItemTitle { get; set; }
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public ApiSessionStatus Status { get; set; } = ApiSessionStatus.Analyzing;
    /// <summary>Serialized InventoryDoc JSON.</summary>
    public string? InventoryJson { get; set; }
    /// <summary>Serialized SurfaceMatrix JSON.</summary>
    public string? AnalysisJson { get; set; }
    /// <summary>Persisted narrative of live SSE events (JSON array); replayed on Last-Event-ID.</summary>
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>When true, pipeline proceeds from Analyzing directly to Drafting without a review gate.</summary>
    public bool AutoHandoff { get; set; }
    /// <summary>Captures qaloop.activity_enabled at row creation; false suppresses verbose streaming.</summary>
    public bool ActivityEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ApiTestDraft> Drafts { get; set; } = [];
}

/// <summary>One generated Playwright spec + ADO Test Case draft. Table: ApiTestDrafts.</summary>
public sealed class ApiTestDraft
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public TestLevel Level { get; set; } = TestLevel.Contract;
    public string Title { get; set; } = string.Empty;
    public string Preconditions { get; set; } = "";
    public string StepsJson { get; set; } = "[]";
    /// <summary>Zod schema JSON (Contract level only).</summary>
    public string? SchemaJson { get; set; }
    /// <summary>Generated Playwright spec TypeScript source.</summary>
    public string SpecCode { get; set; } = string.Empty;
    /// <summary>Absolute path where SpecCode was written; null if write failed.</summary>
    public string? SpecFilePath { get; set; }
    /// <summary>QAL-067: index of this draft's case within a shared multi-case spec file (Endpoint's
    /// 200/401/400 cases share ONE physical file). Null for Contract/Workflow (always single-case).
    /// Lets push-time rewrite find and replace only THIS case's [<tcId-pending:N>] placeholder.</summary>
    public int? CaseIndex { get; set; }
    public string? Tags { get; set; }
    /// <summary>JSON array of "METHOD /route" strings this draft covers.</summary>
    public string? SourceRoutesJson { get; set; }
    public string? ErrorMessage { get; set; }
    public ApiDraftStatus Status { get; set; } = ApiDraftStatus.Draft;
    public int? AdoTestCaseId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
