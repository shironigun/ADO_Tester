namespace QaAutomation.Core.Models;

/// <summary>Persisted record of a scan of an ADO Test Plan's existing test cases.
/// Reused across runs until stale; refreshed on forceRescan. Table: TestPlanScans.</summary>
public sealed class TestPlanScan
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    /// <summary>Null = whole plan (root suite scanned recursively); non-null = specific suite scope.</summary>
    public int? SuiteId { get; set; }
    public DateTime ScannedAt { get; set; }
    public int CaseCount { get; set; }
    /// <summary>Heuristic summary of the title patterns found (e.g. "[Module] - [Feature] - Verify …"),
    /// injected into the Tester prompt so the agent understands the naming convention in use.</summary>
    public string? TitlePatternSummary { get; set; }
}
