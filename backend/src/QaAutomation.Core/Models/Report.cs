namespace QaAutomation.Core.Models;

/// <summary>A generated weekly/ad-hoc QA report over a date range + board filters. Table: Reports.</summary>
public sealed class Report
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    /// <summary>Boards joined with "; " (spec §5.3); empty = whole project.</summary>
    public string Boards { get; set; } = string.Empty;
    public int TicketCount { get; set; }
    public ReportStatus Status { get; set; } = ReportStatus.Generating;
    public string? HtmlPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>"Manual" or "Scheduled".</summary>
    public string Trigger { get; set; } = "Manual";
    public ReportTheme Theme { get; set; } = ReportTheme.Executive;
    /// <summary>Structured AI insights (TL;DR, theme clusters, speaker script) as JSON text.</summary>
    public string? SummaryJson { get; set; }
    public string? EventLogJson { get; set; }
}
