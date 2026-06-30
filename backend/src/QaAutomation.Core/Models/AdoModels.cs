namespace QaAutomation.Core.Models;

// Lightweight Azure DevOps DTOs — flattened views the agents/reports/code-verify consume.
// Frozen lowercase domains noted inline (spec §5.5).

/// <summary>A Test Case work item linked to a story in ADO (source of truth), with parsed steps.</summary>
public sealed record LinkedTestCase(int Id, string Title, string Preconditions, IReadOnlyList<TestCaseStep> Steps);

/// <summary>One file touched by a PR or commit (the diff anchor).</summary>
/// <param name="ChangeType">add | edit | delete | rename | "edit, rename" | seed (lowercase).</param>
public sealed record ChangedFile(string Path, string ChangeType);

/// <summary>A PR or commit linked to a work item, enriched with a human title + browser URL.</summary>
/// <param name="Kind">"pr" | "commit" (lowercase).</param>
public sealed record LinkedChange(string Kind, string Id, string Title, string Url);

/// <summary>One discussion comment on a work item (report spotlight modal).</summary>
public sealed record TicketComment(string Author, string? Date, string Html);

/// <summary>One derived history event on a work item (report spotlight modal).</summary>
/// <param name="Kind">created | state | assignment | comment | other (lowercase).</param>
public sealed record TimelineEvent(string? Date, string Actor, string Kind, string Detail);

public sealed record AdoTestPlan(int Id, string Name);
public sealed record AdoTestSuite(int Id, string Name, int? ParentId = null);

// ── Build / pipeline reads ────────────────────────────────────────────────────
public sealed record AdoBuild(int Id, string SourceVersion, string RepositoryId, DateTime FinishedAt);
public sealed record AdoBuildChange(string Id, string Message);

/// <summary>An id/name pair (discover projects, list repositories).</summary>
public sealed record AdoNamedRef(string Id, string Name);

/// <summary>A work item's linked Git refs, parsed from its ArtifactLink relations.</summary>
public sealed record GitRefs(IReadOnlyList<string> PullRequestIds, IReadOnlyList<string> CommitShas);

/// <summary>
/// Flat ticket shape used by the weekly HTML report. Base fields come from the WIQL batch fetch;
/// <see cref="Comments"/>/<see cref="Timeline"/> are populated lazily by enrichment (hence mutable).
/// </summary>
public sealed class ReportTicket
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string WorkItemType { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? AssignedTo { get; set; }
    public string? IssueDescription { get; set; }
    public string? Fix { get; set; }
    public string? ClosedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedDate { get; set; }
    public string? Description { get; set; }
    public string? RootCause { get; set; }
    public string? AreaPath { get; set; }
    public List<TicketComment> Comments { get; set; } = [];
    public List<TimelineEvent> Timeline { get; set; } = [];
}
