namespace QaAutomation.Core.Models;

public enum ReportStatus { Generating, Done, Failed }

public sealed class Report
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    /// <summary>Comma-separated board / area path filters; empty = whole project.</summary>
    public string Boards { get; set; } = string.Empty;
    public int TicketCount { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Generating;
    public string? HtmlPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>"Manual" or "Scheduled".</summary>
    public string Trigger { get; set; } = "Manual";
}

/// <summary>A single ticket row for a report.</summary>
public sealed class ReportTicket
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string WorkItemType { get; init; } = string.Empty;
    public string? State { get; init; }
    public string? AssignedTo { get; init; }
    public string? IssueDescription { get; init; }
    public string? Fix { get; init; }
    public DateTime? ClosedAt { get; init; }
}
