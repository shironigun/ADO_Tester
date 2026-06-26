namespace QaAutomation.Core.Contracts;

/// <summary>
/// Request to ingest a Playwright JUnit run and, on failures, triage + file house-style Bugs (Track A).
/// Pass the XML inline (<see cref="JunitXml"/>) or a path (<see cref="JunitPath"/>).
/// </summary>
public sealed record IngestResultsRequest
{
    /// <summary>The JUnit XML content (takes precedence over <see cref="JunitPath"/>).</summary>
    public string? JunitXml { get; init; }

    /// <summary>Path to a JUnit XML file (e.g. tests-e2e/test-results/junit.xml).</summary>
    public string? JunitPath { get; init; }

    /// <summary>Base dir for resolving relative attachment paths. Defaults to the JUnit file's folder.</summary>
    public string? ArtifactsRoot { get; init; }

    /// <summary>Optional KB doc name to ground triage (e.g. "deals-create-deal.md").</summary>
    public string? KbFileName { get; init; }

    /// <summary>Optionally also link each filed Bug to this story via the Related relation.</summary>
    public int? StoryId { get; init; }

    /// <summary>When false, triage only — no ADO writes (dry run / preview).</summary>
    public bool FileBugs { get; init; } = true;
}

/// <summary>Outcome of ingesting a run: per-failure triage + any Bug filed.</summary>
public sealed record IngestResultsResult
{
    public int TotalTests { get; init; }
    public int Failures { get; init; }
    public int BugsFiled { get; init; }
    public List<FailureOutcome> Outcomes { get; init; } = [];
}

/// <summary>The triage + filing outcome for one failed test.</summary>
public sealed record FailureOutcome
{
    public string TestTitle { get; init; } = "";
    public int? AdoTestCaseId { get; init; }
    public string Classification { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Filed { get; init; }
    public int? BugId { get; init; }
    public string BugTitle { get; init; } = "";
    public string Severity { get; init; } = "";

    // The composed bug content (always populated — a dry run is a true preview of what would be filed).
    public string Preconditions { get; init; } = "";
    public List<string> ReproSteps { get; init; } = [];
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";

    public List<string> Artifacts { get; init; } = [];
}
