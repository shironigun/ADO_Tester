using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// ── Runs ──────────────────────────────────────────────────────────────────────

public sealed class RunStartRequest
{
    public string? ModuleFilter { get; set; }
    public string? LevelFilter { get; set; }
    public string? TagFilter { get; set; }
    public string Profile { get; set; } = "infinity";
    public List<int>? DraftIds { get; set; }
}

public sealed class RunStartResponse
{
    public int RunId { get; set; }
}

/// <summary>Trigger AI triage of the run's failed tests (draft-first; never files to ADO).</summary>
public sealed class TriageRequest { }

public sealed class FileBugsRequest
{
    public List<int> FailureIds { get; set; } = [];
    public int? StoryId { get; set; }
}

public sealed class FileBugsResponse
{
    public int FiledCount { get; set; }
    public List<int> BugIds { get; set; } = [];
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class TestFailureDto
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string TestTitle { get; set; } = "";
    public int? AdoTestCaseId { get; set; }
    public string Classification { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? BugDraftJson { get; set; }
    public int? BugId { get; set; }
    public string? ArtifactPaths { get; set; }

    public static TestFailureDto From(TestFailure f) => new()
    {
        Id = f.Id, RunId = f.RunId, TestTitle = f.TestTitle, AdoTestCaseId = f.AdoTestCaseId,
        Classification = f.Classification.ToWire(), Confidence = f.Confidence,
        Reason = f.Reason, BugDraftJson = f.BugDraftJson, BugId = f.BugId,
        ArtifactPaths = f.ArtifactPaths,
    };
}

public sealed class TestRunSummary
{
    public int Id { get; set; }
    public string? Module { get; set; }
    public string? LevelFilter { get; set; }
    public string Profile { get; set; } = "";
    public string TriggerSource { get; set; } = "";
    public TestRunStatus Status { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public int? AdoTestRunId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool ActivityEnabled { get; set; }

    public static TestRunSummary From(TestRun r) => new()
    {
        Id = r.Id, Module = r.Module, LevelFilter = r.LevelFilter,
        Profile = r.Profile.ToWire(), TriggerSource = r.TriggerSource.ToWire(),
        Status = r.Status, Passed = r.Passed, Failed = r.Failed, Skipped = r.Skipped,
        DurationMs = r.DurationMs, AdoTestRunId = r.AdoTestRunId, ErrorMessage = r.ErrorMessage,
        StartedAt = r.StartedAt, FinishedAt = r.FinishedAt, CreatedAt = r.CreatedAt,
        ActivityEnabled = r.ActivityEnabled,
    };
}

public sealed class TestRunList
{
    public int Total { get; set; }
    public IReadOnlyList<TestRunSummary> Items { get; set; } = [];
}

public sealed class TestRunDetail
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string? Module { get; set; }
    public string? LevelFilter { get; set; }
    public string? TagFilter { get; set; }
    public string Profile { get; set; } = "";
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public string TriggerSource { get; set; } = "";
    public int? TriggerPipelineId { get; set; }
    public int? TriggerBuildId { get; set; }
    public string? TriggerCommitSha { get; set; }
    public string? ImpactAnalysisJson { get; set; }
    public TestRunStatus Status { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? JunitPath { get; set; }
    public string? ReportPath { get; set; }
    public string? EventLogJson { get; set; }
    public int? AdoTestRunId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool ActivityEnabled { get; set; }
    public IReadOnlyList<TestFailureDto> Failures { get; set; } = [];

    public static TestRunDetail From(TestRun r, IEnumerable<TestFailure> failures) => new()
    {
        Id = r.Id, ProjectId = r.ProjectId, Module = r.Module, LevelFilter = r.LevelFilter,
        TagFilter = r.TagFilter, Profile = r.Profile.ToWire(), TestPlanId = r.TestPlanId,
        TestSuiteId = r.TestSuiteId, TriggerSource = r.TriggerSource.ToWire(),
        TriggerPipelineId = r.TriggerPipelineId, TriggerBuildId = r.TriggerBuildId,
        TriggerCommitSha = r.TriggerCommitSha, ImpactAnalysisJson = r.ImpactAnalysisJson,
        Status = r.Status, Passed = r.Passed, Failed = r.Failed, Skipped = r.Skipped,
        DurationMs = r.DurationMs, JunitPath = r.JunitPath, ReportPath = r.ReportPath,
        EventLogJson = r.EventLogJson,
        AdoTestRunId = r.AdoTestRunId, ErrorMessage = r.ErrorMessage,
        StartedAt = r.StartedAt, FinishedAt = r.FinishedAt, CreatedAt = r.CreatedAt,
        ActivityEnabled = r.ActivityEnabled,
        Failures = failures.Select(TestFailureDto.From).ToList(),
    };
}
