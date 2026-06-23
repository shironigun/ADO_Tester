namespace QaAutomation.Core.Models;

/// <summary>One Find-Bugs run: work item → Analyst → Code-Verify → Bug-Writer → ADO push.
/// Table: BugSessions.</summary>
public sealed class BugSession
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = string.Empty;
    public BugSessionStatus Status { get; set; } = BugSessionStatus.Analyzing;
    public string? CurrentActivity { get; set; }
    public string? AnalysisJson { get; set; }
    public string? ActualBehavior { get; set; }
    public string? CodeContext { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EventLogJson { get; set; }
    /// <summary>Per-test-case static code-verification verdicts as a JSON array.</summary>
    public string? CodeVerificationJson { get; set; }
    /// <summary>Batch membership — plain indexed int, NO FK in the DB (spec §5.3).</summary>
    public int? BatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<BugDraft> Drafts { get; set; } = [];
}

/// <summary>One generated bug, editable before ADO push. Table: BugDrafts.</summary>
public sealed class BugDraft
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public BugSeverity Severity { get; set; } = BugSeverity.Medium;
    public string Preconditions { get; set; } = string.Empty;
    public string ReproStepsJson { get; set; } = "[]";   // JSON array of plain strings
    public string ExpectedBehavior { get; set; } = string.Empty;
    public string ActualBehavior { get; set; } = string.Empty;
    public string SuggestedFix { get; set; } = string.Empty;
    public BugDraftStatus Status { get; set; } = BugDraftStatus.Draft;
    public int? AdoBugId { get; set; }
    public int SortOrder { get; set; }
}
