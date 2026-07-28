using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// Request/response DTOs for the Author-Tests API (spec B9). camelCase on the wire via the global STJ
// policy; enum-typed Status fields serialize as their exact name via JsonStringEnumConverter.

public sealed class StartRequest
{
    public int WorkItemId { get; set; }
    public bool AutoHandoff { get; set; } = true;
    /// <summary>Free-text context to guide the run (routes/screens/notes not in the story).</summary>
    public string? AdditionalContext { get; set; }
    /// <summary>Optional reference Test Plan whose existing cases the Tester should not duplicate.</summary>
    public int? ReferenceTestPlanId { get; set; }
    public int? ReferenceTestSuiteId { get; set; }
    public string? PlanNavigationHint { get; set; }
}

/// <summary>Body for the run-tester (Analyst→Tester gate) call. All fields optional — an empty body
/// preserves the pre-existing no-body behavior.</summary>
public sealed class RunTesterRequest
{
    /// <summary>Extra free-text context for the Tester, entered at the gate.</summary>
    public string? AdditionalContext { get; set; }
    public int? ReferenceTestPlanId { get; set; }
    public int? ReferenceTestSuiteId { get; set; }
    public string? PlanNavigationHint { get; set; }
}

/// <summary>Re-run the Analyst with reviewer feedback to improve/fill gaps in the existing analysis.</summary>
public sealed class RerunAnalystRequest
{
    public string Feedback { get; set; } = "";
}

/// <summary>Re-run the Tester as a feedback-driven refine that MERGES into the existing drafts
/// (update flagged, add new, remove only those explicitly flagged; pushed drafts are untouched).</summary>
public sealed class RefineTesterRequest
{
    public string? OverallFeedback { get; set; }
    public List<TestFeedback> PerTest { get; set; } = [];
}

/// <summary>Per-draft feedback for a Tester refine. <c>Remove</c> explicitly drops that draft.</summary>
public sealed class TestFeedback
{
    public int DraftId { get; set; }
    public string? Feedback { get; set; }
    public bool Remove { get; set; }
}

public sealed class StartResponse
{
    public int SessionId { get; set; }
}

public sealed class AcceptedResponse
{
    public bool Started { get; set; }
}

public sealed class CancelResponse
{
    public bool Cancelled { get; set; }
}

public sealed class ApproveAllResponse
{
    public int ApprovedCount { get; set; }
}

public sealed class PushRequest
{
    public int TestPlanId { get; set; }
    public int TestSuiteId { get; set; }
}

/// <summary>Per-field skip-null PATCH for a draft (null = leave unchanged).</summary>
public sealed class UpdateDraftRequest
{
    public string? Title { get; set; }
    public string? Preconditions { get; set; }
    public string? StepsJson { get; set; }
    public DraftStatus? Status { get; set; }
}

/// <summary>Full-replace analysis PATCH (omitted fields take defaults and overwrite — NOT skip-null).
/// Stored as the camelCase <see cref="AnalystOutput"/> JSON.</summary>
public sealed class TestAnalysisUpdate
{
    public string Module { get; set; } = "";
    public string Feature { get; set; } = "";
    public string Summary { get; set; } = "";
    public string HappyPath { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public List<string> BusinessRules { get; set; } = [];
    public List<string> EdgeCases { get; set; } = [];
    public List<string> NegativeCases { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
    public List<string> CrossFlow { get; set; } = [];
    public List<string> OpenQuestions { get; set; } = [];
    public List<string> QuestionAnswers { get; set; } = [];

    public AnalystOutput ToAnalystOutput() => new()
    {
        Module = Module, Feature = Feature, Summary = Summary, HappyPath = HappyPath,
        AcceptanceCriteria = AcceptanceCriteria, BusinessRules = BusinessRules, EdgeCases = EdgeCases,
        NegativeCases = NegativeCases, Permissions = Permissions, CrossFlow = CrossFlow,
        OpenQuestions = OpenQuestions, QuestionAnswers = QuestionAnswers,
    };
}

public sealed class TestCaseDraftDto
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Title { get; set; } = "";
    public string Preconditions { get; set; } = "";
    public string StepsJson { get; set; } = "[]";
    public DraftStatus Status { get; set; }
    public int? AdoTestCaseId { get; set; }
    public int SortOrder { get; set; }

    public static TestCaseDraftDto From(TestCaseDraft d) => new()
    {
        Id = d.Id, SessionId = d.SessionId, Title = d.Title, Preconditions = d.Preconditions,
        StepsJson = d.StepsJson, Status = d.Status, AdoTestCaseId = d.AdoTestCaseId, SortOrder = d.SortOrder,
    };
}

public sealed class TestSessionSummary
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public SessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public static TestSessionSummary From(TestSession s) => new()
    {
        Id = s.Id, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        Status = s.Status, CreatedAt = s.CreatedAt, ErrorMessage = s.ErrorMessage,
    };
}

public sealed class TestSessionList
{
    public int Total { get; set; }
    public IReadOnlyList<TestSessionSummary> Items { get; set; } = [];
}

public sealed class TestSessionDetail
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public SessionStatus Status { get; set; }
    public string? CurrentActivity { get; set; }
    public string? AnalysisJson { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EventLogJson { get; set; }
    public string? AdditionalContext { get; set; }
    public string? CodeContext { get; set; }
    public string? TesterContext { get; set; }
    public int? ReferenceTestPlanId { get; set; }
    public int? ReferenceTestSuiteId { get; set; }
    public string? PlanNavigationHint { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<TestCaseDraftDto> Drafts { get; set; } = [];

    public static TestSessionDetail From(TestSession s, IEnumerable<TestCaseDraft> drafts) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        Status = s.Status, CurrentActivity = s.CurrentActivity, AnalysisJson = s.AnalysisJson,
        ErrorMessage = s.ErrorMessage, EventLogJson = s.EventLogJson,
        AdditionalContext = s.AdditionalContext, CodeContext = s.CodeContext, TesterContext = s.TesterContext,
        ReferenceTestPlanId = s.ReferenceTestPlanId, ReferenceTestSuiteId = s.ReferenceTestSuiteId,
        PlanNavigationHint = s.PlanNavigationHint,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        Drafts = drafts.OrderBy(d => d.SortOrder).Select(TestCaseDraftDto.From).ToList(),
    };
}
