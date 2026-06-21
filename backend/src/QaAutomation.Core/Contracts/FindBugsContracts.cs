using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// Request/response DTOs for the Find-Bugs API (spec B9). camelCase on the wire; enum Status/Severity
// serialize as their exact name. Reuses AcceptedResponse/CancelResponse/ApproveAllResponse from the
// Author-Tests contracts.

public sealed class BugStartRequest
{
    public int WorkItemId { get; set; }
}

public sealed class BugStartResponse
{
    public int SessionId { get; set; }
}

public sealed class BugRunRequest
{
    public string ActualBehavior { get; set; } = "";
    public string? CodeContext { get; set; }
    public bool VerifyCode { get; set; }
    public List<string> CommitIds { get; set; } = [];
    public List<string> PrIds { get; set; } = [];
    public List<string> SelectedTestCases { get; set; } = [];
}

public sealed class BugLinkedChange
{
    public string Kind { get; set; } = "";   // 'pr' | 'commit'
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class BugLinkedRefs
{
    public IReadOnlyList<string> PullRequestIds { get; set; } = [];
    public IReadOnlyList<string> CommitShas { get; set; } = [];
    public IReadOnlyList<BugLinkedChange> Changes { get; set; } = [];
}

public sealed class BugFromVerdictStep
{
    public string Action { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Status { get; set; } = "inconclusive";
    public string Actual { get; set; } = "";
}

public sealed class BugFromVerdictRequest
{
    public string TestCase { get; set; } = "";
    public string? Source { get; set; }
    public int? AdoTestCaseId { get; set; }
    public string Severity { get; set; } = "Medium";
    public string Evidence { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public int? FailedStepIndex { get; set; }
    public List<BugFromVerdictStep> Steps { get; set; } = [];
    public string? Mode { get; set; }   // 'instant' | 'agent' | null
}

public sealed class UpdateBugDraftRequest
{
    public string? Title { get; set; }
    public BugSeverity? Severity { get; set; }
    public string? Preconditions { get; set; }
    public string? ReproStepsJson { get; set; }
    public string? ExpectedBehavior { get; set; }
    public string? ActualBehavior { get; set; }
    public string? SuggestedFix { get; set; }
    public BugDraftStatus? Status { get; set; }
}

public sealed class BugAnalysisUpdate
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

public sealed class BugDraftDto
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Title { get; set; } = "";
    public BugSeverity Severity { get; set; }
    public string Preconditions { get; set; } = "";
    public string ReproStepsJson { get; set; } = "[]";
    public string ExpectedBehavior { get; set; } = "";
    public string ActualBehavior { get; set; } = "";
    public string SuggestedFix { get; set; } = "";
    public BugDraftStatus Status { get; set; }
    public int? AdoBugId { get; set; }
    public int SortOrder { get; set; }

    public static BugDraftDto From(BugDraft d) => new()
    {
        Id = d.Id, SessionId = d.SessionId, Title = d.Title, Severity = d.Severity,
        Preconditions = d.Preconditions, ReproStepsJson = d.ReproStepsJson, ExpectedBehavior = d.ExpectedBehavior,
        ActualBehavior = d.ActualBehavior, SuggestedFix = d.SuggestedFix, Status = d.Status,
        AdoBugId = d.AdoBugId, SortOrder = d.SortOrder,
    };
}

public sealed class BugSessionSummary
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public BugSessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public static BugSessionSummary From(BugSession s) => new()
    {
        Id = s.Id, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        Status = s.Status, CreatedAt = s.CreatedAt, ErrorMessage = s.ErrorMessage,
    };
}

public sealed class BugSessionList
{
    public int Total { get; set; }
    public IReadOnlyList<BugSessionSummary> Items { get; set; } = [];
}

public sealed class BugSessionDetail
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public BugSessionStatus Status { get; set; }
    public string? CurrentActivity { get; set; }
    public string? AnalysisJson { get; set; }
    public string? ActualBehavior { get; set; }
    public string? CodeContext { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EventLogJson { get; set; }
    public string? CodeVerificationJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public IReadOnlyList<BugDraftDto> Drafts { get; set; } = [];

    public static BugSessionDetail From(BugSession s, IEnumerable<BugDraft> drafts) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
        Status = s.Status, CurrentActivity = s.CurrentActivity, AnalysisJson = s.AnalysisJson,
        ActualBehavior = s.ActualBehavior, CodeContext = s.CodeContext, ErrorMessage = s.ErrorMessage,
        EventLogJson = s.EventLogJson, CodeVerificationJson = s.CodeVerificationJson,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        Drafts = drafts.OrderBy(d => d.SortOrder).Select(BugDraftDto.From).ToList(),
    };
}
