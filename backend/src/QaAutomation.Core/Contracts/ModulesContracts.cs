namespace QaAutomation.Core.Contracts;

/// <summary>Analyze a whole module: read its source (ADO repo+branch, or a local SourceRoot) and the
/// existing test cases in a plan/suite, discover all flows, and map coverage. (Track A module run.)</summary>
public sealed record ModuleAnalyzeRequest
{
    public string Module { get; init; } = "";
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string SourceRoot { get; init; } = "";   // optional local clone (else read from ADO)
    public int PlanId { get; init; }
    public int SuiteId { get; init; }
    public bool Recurse { get; init; } = true;       // include sub-suites
    public int MaxFileChars { get; init; } = 5_000;
    public int MaxBundleChars { get; init; } = 90_000;
}

/// <summary>One module flow + its coverage by existing test cases (empty = a gap). Detailed per-flow KB
/// is generated on demand at spec time.</summary>
public sealed record ModuleFlow
{
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool Covered { get; init; }
    public List<int> CoveringTestCaseIds { get; init; } = [];
    public List<string> CoveringTestCaseTitles { get; init; } = [];
}

public sealed record ModuleAnalyzeResult
{
    public string Module { get; init; } = "";
    public int ExistingTestCaseCount { get; init; }
    public int FilesRead { get; init; }
    public int FlowCount { get; init; }
    public int CoveredCount { get; init; }
    public int GapCount { get; init; }
    public string KbDocFileName { get; init; } = "";
    public List<ModuleFlow> Flows { get; init; } = [];
}

/// <summary>Generate a Playwright spec for ONE module flow on demand (after analysis). Reads the module
/// source, produces the flow's DETAILED KB doc, then emits the spec: a COVERED flow tags its existing test
/// case id; a GAP (no covering ids) authors + creates a new ADO Test Case in the plan/suite and tags it.</summary>
public sealed record ModuleFlowSpecRequest
{
    public string Module { get; init; } = "";
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string SourceRoot { get; init; } = "";   // optional local clone (else read from ADO)
    public int PlanId { get; init; }
    public int SuiteId { get; init; }

    public string FlowName { get; init; } = "";
    public string FlowSummary { get; init; } = "";
    public List<int> CoveringTestCaseIds { get; init; } = [];        // empty => a gap (create a new TC)
    public List<string> CoveringTestCaseTitles { get; init; } = [];  // context for KB enrichment

    public int? StoryId { get; init; }      // link a newly-created gap TC to this story (optional)
    public bool Smoke { get; init; } = true;
    public string OutputRoot { get; init; } = "";   // local /automation write target; empty => don't write
    public int MaxFileChars { get; init; } = 5_000;
    public int MaxBundleChars { get; init; } = 90_000;
}

/// <summary>Result of an on-demand flow spec: which flow, whether it was a gap, the stored detailed KB doc,
/// and the underlying spec result (code, file, ADO Test Case tagged/created).</summary>
public sealed record ModuleFlowSpecResult
{
    public string FlowName { get; init; } = "";
    public bool WasGap { get; init; }
    public string KbDocFileName { get; init; } = "";
    public GenerateSpecResult Spec { get; init; } = new();
}
