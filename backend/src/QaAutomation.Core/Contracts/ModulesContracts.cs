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
