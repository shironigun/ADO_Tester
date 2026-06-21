using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

public sealed class GenerateReportRequest
{
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public List<string> Boards { get; set; } = [];
    public ReportTheme Theme { get; set; } = ReportTheme.Executive;
}

public sealed class GenerateReportResponse
{
    public int ReportId { get; set; }
}

/// <summary>Report row for the UI (camelCase; status/theme as enum names).</summary>
public sealed class ReportDto
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string Boards { get; set; } = "";
    public int TicketCount { get; set; }
    public ReportStatus Status { get; set; }
    public string? HtmlPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public string Trigger { get; set; } = "Manual";
    public ReportTheme Theme { get; set; }
    public string? SummaryJson { get; set; }
    public string? EventLogJson { get; set; }

    public static ReportDto From(Report r) => new()
    {
        Id = r.Id, ProjectId = r.ProjectId, FromUtc = r.FromUtc, ToUtc = r.ToUtc, Boards = r.Boards,
        TicketCount = r.TicketCount, Status = r.Status, HtmlPath = r.HtmlPath, ErrorMessage = r.ErrorMessage,
        CreatedAt = r.CreatedAt, Title = r.Title, Trigger = r.Trigger, Theme = r.Theme,
        SummaryJson = r.SummaryJson, EventLogJson = r.EventLogJson,
    };
}
