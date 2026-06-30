namespace QaAutomation.Core.Abstractions;

/// <summary>Spec for a single Playwright run (which tests to run, in what environment).</summary>
public sealed class PlaywrightRunSpec
{
    public int RunId { get; init; }
    /// <summary>DMS profile key (e.g. "infinity") — set as AUTOMATION_PROFILE on child process env.</summary>
    public string Profile { get; init; } = "";
    /// <summary>Numeric ADO Test Case ids to run; grep pattern is built from these.</summary>
    public IReadOnlyList<int> GrepTags { get; init; } = [];
    public string Module { get; init; } = "";
    /// <summary>Optional level filter ("contracts" | "endpoints" | "workflows"); null = all.</summary>
    public string? LevelFilter { get; init; }
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
}
