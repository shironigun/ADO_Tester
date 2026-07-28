using System.Text.Json.Serialization;

namespace QaAutomation.Core.Models;

/// <summary>One Author-Tests pipeline run: work item → Analyst → Tester → review → ADO push.
/// Table: TestSessions.</summary>
public sealed class TestSession
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Analyzing;
    /// <summary>Human-readable description of what the pipeline is doing right now.</summary>
    public string? CurrentActivity { get; set; }
    public string? AnalysisJson { get; set; }   // serialized AnalystOutput
    public string? ErrorMessage { get; set; }
    /// <summary>Persisted narrative of the live console (non-token events) as a JSON array; never tokens.</summary>
    public string? EventLogJson { get; set; }

    // ── Free-form guidance + code awareness (Author Tests enhancements) ──
    /// <summary>Free-text context the tester supplies at start (routes/screens/notes not in the story);
    /// feeds the code-discovery search hints and the Analyst prompt.</summary>
    public string? AdditionalContext { get; set; }
    /// <summary>Repo code snippets discovered for the feature (linked PRs/commits + hint search),
    /// assembled by the code-context service and fed to the Analyst. Mirrors <c>BugSession.CodeContext</c>.</summary>
    public string? CodeContext { get; set; }
    /// <summary>Free-text context the tester supplies at the Analyst→Tester gate; feeds the Tester prompt.</summary>
    public string? TesterContext { get; set; }

    // ── Existing-test dedup: optional reference Test Plan the Tester considers ──
    /// <summary>Optional reference Test Plan whose existing test cases the Tester should NOT duplicate.</summary>
    public int? ReferenceTestPlanId { get; set; }
    /// <summary>Optional suite scope within <see cref="ReferenceTestPlanId"/> (null = whole plan).</summary>
    public int? ReferenceTestSuiteId { get; set; }
    /// <summary>Optional free-text hint on how to navigate the reference plan's suites/sub-folders.</summary>
    public string? PlanNavigationHint { get; set; }
    /// <summary>Set when part of an Epic/Feature run (a Batch); NULL for single runs. Plain indexed
    /// int — there is NO FK constraint in the DB (spec §5.3).</summary>
    public int? BatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<TestCaseDraft> Drafts { get; set; } = [];
}

/// <summary>One generated test case, editable before ADO push. Table: TestCaseDrafts.</summary>
public sealed class TestCaseDraft
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Preconditions { get; set; } = string.Empty;
    public string StepsJson { get; set; } = "[]";   // JSON array of {action, expected}
    public DraftStatus Status { get; set; } = DraftStatus.Draft;
    public int? AdoTestCaseId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A single test-case step (Action / Expected Result). Shape of each StepsJson element.</summary>
public sealed class TestCaseStep
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("expected")] public string Expected { get; set; } = string.Empty;
}
