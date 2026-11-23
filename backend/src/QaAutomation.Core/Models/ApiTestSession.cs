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
    /// <summary>Serialized <see cref="PlanSuiteUnitMap"/> — full Plan suite→automation-unit map (INT-201/202).</summary>
    public string? SuiteUnitMapJson { get; set; }
    /// <summary>Serialized <see cref="PlanImportMeta"/> — last Plan import batch counters (INT-201/203).</summary>
    public string? PlanImportMetaJson { get; set; }
    /// <summary>JSON array of repo-relative directories to scan (e.g. ["Lead/src/.../Deal"]).
    /// When null/empty, FetchSourceAsync falls back to treating Module as a top-level scopePath.
    /// For frontend-module-driven scans this is DERIVED (backend dirs traced from the frontend apis/).</summary>
    public string? SourceDirsJson { get; set; }
    /// <summary>Selected frontend module route (e.g. "/deals"), when the scan was module-driven.</summary>
    public string? FrontendModule { get; set; }
    /// <summary>Repo-relative frontend component folder whose apis/ the scan traces to the backend.</summary>
    public string? FrontendPath { get; set; }
    /// <summary>Serialized ModuleSourceDiscovery evidence selected for this scan.</summary>
    public string? DiscoveryJson { get; set; }
    public ApiSessionStatus Status { get; set; } = ApiSessionStatus.Analyzing;
    /// <summary>Serialized InventoryDoc JSON.</summary>
    public string? InventoryJson { get; set; }
    /// <summary>Serialized SurfaceMatrix JSON.</summary>
    public string? AnalysisJson { get; set; }
    /// <summary>Free-text provided at scan start — feeds the Analyzer and the generators (what to focus on, what to skip).</summary>
    public string? AdditionalContext { get; set; }
    /// <summary>Serialized list of scan-scoped markdown attachments (<see cref="QaAutomation.Core.Contracts.ContextAttachmentDto"/>).</summary>
    public string? ContextAttachmentsJson { get; set; }
    /// <summary>Free-text provided at the Analyze→Generate gate — steers all three generator agents.</summary>
    public string? GenerateContext { get; set; }
    /// <summary>Most recent refine feedback (persisted for UI re-seed).</summary>
    public string? LastFeedback { get; set; }
    /// <summary>Persisted narrative of live SSE events (JSON array); replayed on Last-Event-ID.</summary>
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>When true, pipeline proceeds from Analyzing directly to Drafting without a review gate.</summary>
    public bool AutoHandoff { get; set; }
    /// <summary>Captures qaloop.activity_enabled at row creation; false suppresses verbose streaming.</summary>
    public bool ActivityEnabled { get; set; } = true;
    /// <summary>When true, this session runs the UI testing phase (Slab 2) after the API phase.</summary>
    public bool HasUiPhase { get; set; }
    /// <summary>Tracks the UI phase pipeline status. Null when HasUiPhase is false or UI phase not yet started.</summary>
    public ApiUiStatus? UiStatus { get; set; }
    // ── KBG-105: KB-grounded columns ──────────────────────────────────────────
    /// <summary>Resolved FRS atlas slug (e.g. "messaging"). Null when FRS KB is not configured.</summary>
    public string? FrsSlug { get; set; }
    /// <summary>Serialized FrsModuleContext. Internal use only — not surfaced in ApiSessionDetail.</summary>
    public string? KbContextJson { get; set; }
    /// <summary>Serialized CodeScanBundle. Internal use only — not surfaced in ApiSessionDetail.</summary>
    public string? CodeScanJson { get; set; }
    /// <summary>Serialized QaLoopUiAnalystOutput. Surfaced in ApiSessionDetail as uiAnalysisJson.</summary>
    public string? UiAnalysisJson { get; set; }
    /// <summary>ADO Test Suite id for UI test push. Falls back to TestSuiteId when null.</summary>
    public int? UiTestSuiteId { get; set; }
    /// <summary>Run id used for the last step-10b inline Playwright execution (screenshot path key).</summary>
    public int? LastUiRunId { get; set; }
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
    /// <summary>Draft kind — null = legacy API case; UiCase = manual UI test case; UiScript = Playwright spec.</summary>
    public ApiDraftKind? DraftKind { get; set; }
    /// <summary>UI test bucket: "smoke" | "regression" | "e2e". Null for API drafts.</summary>
    public string? Bucket { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
