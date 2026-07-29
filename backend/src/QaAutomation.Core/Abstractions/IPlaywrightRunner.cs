namespace QaAutomation.Core.Abstractions;

/// <summary>Spec for a single Playwright run (which tests to run, in what environment).</summary>
public sealed class PlaywrightRunSpec
{
    public int RunId { get; init; }
    /// <summary>DMS profile key (e.g. "infinity") — set as AUTOMATION_PROFILE on child process env.</summary>
    public string Profile { get; init; } = "";
    /// <summary>Numeric ADO Test Case ids — still used for ADO Test Run point mapping even when
    /// test selection uses spec file paths instead of grep.</summary>
    public IReadOnlyList<int> GrepTags { get; init; } = [];
    /// <summary>Absolute spec file paths to pass to Playwright as positional args (replaces
    /// --grep-by-title). When non-empty, used for test selection; GrepTags still drives ADO mapping.</summary>
    public IReadOnlyList<string> SpecFilePaths { get; init; } = [];
    public string Module { get; init; } = "";
    /// <summary>Optional level filter ("contracts" | "endpoints" | "workflows"); null = all.</summary>
    public string? LevelFilter { get; init; }

    // Allure context fields (QAL-056/057) — optional, sourced from TestRun at execution time
    public string TriggerSourceWire { get; init; } = "";
    public int? TriggerPipelineId { get; init; }
    public int? TriggerBuildId { get; init; }
    public string? TriggerCommitSha { get; init; }
    public int? AdoTestRunId { get; init; }
    /// <summary>Serialized ImpactAnalysis for pipeline-triggered runs — surfaces trigger reasoning
    /// (what changed, confidence, impacted-test count) in the Allure report metadata.</summary>
    public string? ImpactAnalysisJson { get; init; }
    /// <summary>When false, Playwright stdout/stderr is not streamed to the event bus (QAL-060).</summary>
    public bool ActivityEnabled { get; init; } = true;
    /// <summary>When true, injects screenshot:'only-on-failure' and trace:'on-first-retry' into the
    /// playwright.config.ts. Set for UI-phase runs (DraftKind=UiScript); always false for API runs.</summary>
    public bool EnableCapture { get; init; } = false;
}

/// <summary>Outcome of a Playwright run.</summary>
public sealed class PlaywrightRunResult
{
    public int ExitCode { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Absolute path to the JUnit XML report.</summary>
    public string? JunitPath { get; init; }
    /// <summary>Absolute path to the JSON results file.</summary>
    public string? ResultsJsonPath { get; init; }
    /// <summary>Absolute path to the HTML report directory (null until QAL-057 wires Allure).</summary>
    public string? ReportPath { get; init; }
    /// <summary>Parsed per-test failures (title + error excerpt).</summary>
    public IReadOnlyList<FailedTestResult> FailedTests { get; init; } = [];
}

/// <summary>One failed test extracted from JUnit XML.</summary>
public sealed class FailedTestResult
{
    public string TestTitle { get; init; } = "";
    public string? ErrorMessage { get; init; }
    /// <summary>Absolute path of the spec file that owns this test, extracted from the JUnit
    /// testsuite 'file' attribute. Used to map failures back to ADO Test Case IDs without
    /// requiring a numeric ID tag in the test title.</summary>
    public string? SpecFilePath { get; init; }
}

/// <summary>Runs Playwright specs in a background process; singleton-safe.</summary>
public interface IPlaywrightRunner
{
    /// <summary>
    /// Provisions the shared Playwright runtime (once), executes the run, streams stdout/stderr
    /// via the event bus, and returns parsed results.
    /// </summary>
    Task<PlaywrightRunResult> RunAsync(PlaywrightRunSpec spec, string eventKey,
        CancellationToken ct = default);

    /// <summary>Re-enrich + regenerate the Allure report from a past run's preserved
    /// allure-results (no Playwright execution, no ADO writes). Null when no results exist.</summary>
    Task<string?> RebuildReportAsync(PlaywrightRunSpec spec, string eventKey,
        CancellationToken ct = default);

    /// <summary>Best-effort delete of a run's on-disk artifact directory (junit, results, allure-results,
    /// the generated report, test-output). Called when a run row is deleted so nothing is orphaned on disk.
    /// Never throws — a missing or locked directory is logged and ignored.</summary>
    void DeleteRunArtifacts(int runId);
}
