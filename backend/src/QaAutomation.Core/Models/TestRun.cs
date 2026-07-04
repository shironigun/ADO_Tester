namespace QaAutomation.Core.Models;

/// <summary>One Playwright execution run. Table: TestRuns.</summary>
public sealed class TestRun
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string? Module { get; set; }
    public string? LevelFilter { get; set; }
    public string? TagFilter { get; set; }
    public DmsProfile Profile { get; set; } = DmsProfile.Infinity;
    public int? TestPlanId { get; set; }
    public int? TestSuiteId { get; set; }
    public TriggerSource TriggerSource { get; set; } = TriggerSource.Manual;
    public int? TriggerPipelineId { get; set; }
    public int? TriggerBuildId { get; set; }
    public string? TriggerCommitSha { get; set; }
    /// <summary>Serialized ImpactAnalysis JSON.</summary>
    public string? ImpactAnalysisJson { get; set; }
    public TestRunStatus Status { get; set; } = TestRunStatus.Queued;
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? JunitPath { get; set; }
    public string? ReportPath { get; set; }
    /// <summary>Serialized console narrative (EventBus.Narrative) captured at run completion so the
    /// Console tab can replay the full run history after the live SSE stream has closed.</summary>
    public string? EventLogJson { get; set; }
    public int? AdoTestRunId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Captures qaloop.activity_enabled at row creation; false suppresses Playwright stdout streaming.</summary>
    public bool ActivityEnabled { get; set; } = true;

    /// <summary>Serialized List&lt;LevelStat&gt; — per-level executed/passed/failed, set at run completion.</summary>
    public string? LevelBreakdownJson { get; set; }

    public List<TestFailure> Failures { get; set; } = [];
}

/// <summary>Per-level run stats serialized into <see cref="TestRun.LevelBreakdownJson"/>.</summary>
public sealed record LevelStat(string Level, int Executed, int Passed, int Failed);

/// <summary>One failed test from a TestRun, with AI triage state. Table: TestFailures.</summary>
public sealed class TestFailure
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string TestTitle { get; set; } = string.Empty;
    public int? AdoTestCaseId { get; set; }
    public FailureClassification Classification { get; set; } = FailureClassification.TestIssue;
    public string Confidence { get; set; } = "low";
    public string Reason { get; set; } = string.Empty;
    /// <summary>Serialized composed Bug (title/severity/preconditions/repro/expected/observations); never auto-filed.</summary>
    public string? BugDraftJson { get; set; }
    public int? BugId { get; set; }
    /// <summary>JSON array of artifact paths (screenshots, traces) attached to this failure.</summary>
    public string? ArtifactPaths { get; set; }
}
