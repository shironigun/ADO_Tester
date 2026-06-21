namespace QaAutomation.Core.Models;

/// <summary>
/// A flattened view of an Azure DevOps work item with the fields the agents care about.
/// </summary>
public sealed class AdoWorkItem
{
    public int Id { get; init; }
    public string WorkItemType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public string? AssignedTo { get; init; }
    public string? State { get; init; }
    public string Url { get; init; } = string.Empty;
}
