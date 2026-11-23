namespace QaAutomation.Core.Models;

/// <summary>
/// Persisted AI/operator map of ADO Test Plan suite folders → automation units
/// (INT-201 column; filled by Plan Intake INT-202). Serialized as <c>SuiteUnitMapJson</c>.
/// </summary>
public sealed class PlanSuiteUnitMap
{
    public int TestPlanId { get; set; }
    /// <summary>When the map was last written (UTC).</summary>
    public DateTime MappedAtUtc { get; set; }
    public List<PlanSuiteUnitMapEntry> Entries { get; set; } = [];
}

/// <summary>One suite (folder) row in a <see cref="PlanSuiteUnitMap"/>.</summary>
public sealed class PlanSuiteUnitMapEntry
{
    public int SuiteId { get; set; }
    /// <summary>Human-readable path, e.g. "Deals / API / Contracts".</summary>
    public string SuitePath { get; set; } = "";
    public string SuiteName { get; set; } = "";
    /// <summary>Automation unit slug (e.g. "deals"). Null when ambiguous / unmapped.</summary>
    public string? AutomationUnit { get; set; }
    /// <summary>True when AI could not confidently assign a unit — needs human confirm.</summary>
    public bool Ambiguous { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Counters / meta from the last Plan import batch for a session (INT-201; filled by INT-203).
/// Serialized as <c>PlanImportMetaJson</c>.
/// </summary>
public sealed class PlanImportMeta
{
    public DateTime? LastImportAtUtc { get; set; }
    public int ThinImproved { get; set; }
    public int RichLinked { get; set; }
    public int GapsCreated { get; set; }
    /// <summary>Selected automation unit slug the import was filtered to.</summary>
    public string? SelectedUnit { get; set; }
}
