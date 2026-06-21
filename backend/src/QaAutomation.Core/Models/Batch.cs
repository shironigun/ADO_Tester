namespace QaAutomation.Core.Models;

/// <summary>An Epic/Feature run — a grouping over N child sessions (one per story). Each child is a
/// normal TestSession/BugSession carrying this batch's id. The batch's status is DERIVED from its
/// children at read time (spec §5.3), never stored. Table: Batches.</summary>
public sealed class Batch
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public BatchModule Module { get; set; }
    /// <summary>The Epic/Feature the run was launched from (provenance/display only); NULL for an
    /// ad-hoc multi-select of individual stories.</summary>
    public int? ParentWorkItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Find-Bugs only (Auto/Manual); NULL for Author-Tests.</summary>
    public BatchMode? Mode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
