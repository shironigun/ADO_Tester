using System.Text.Json.Serialization;

namespace QaAutomation.Core.Models;

public enum SessionStatus { Analyzing, Drafting, Drafted, Pushing, Done, Failed, Cancelled }
public enum DraftStatus  { Draft, Approved, Pushed, Rejected }

/// <summary>
/// One pipeline run: a work item → Analyst → Tester → reviewer → ADO push.
/// </summary>
public sealed class TestSession
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Analyzing;
    /// <summary>Human-readable description of what the pipeline is doing right now.</summary>
    public string? CurrentActivity { get; set; }
    public string? AnalysisJson { get; set; }   // serialised AnalystOutput
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<TestCaseDraft> Drafts { get; set; } = [];
}

/// <summary>One generated test case, editable before ADO push.</summary>
public sealed class TestCaseDraft
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Preconditions { get; set; } = string.Empty;
    public string StepsJson { get; set; } = "[]";  // List<TestCaseStep>
    public DraftStatus Status { get; set; } = DraftStatus.Draft;
    public int? AdoTestCaseId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A single step in a test case (Action / Expected Result).</summary>
public sealed class TestCaseStep
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("expected")]
    public string Expected { get; set; } = string.Empty;
}
