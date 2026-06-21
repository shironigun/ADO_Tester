using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>Renders the self-contained themed HTML report (spec B7).</summary>
public interface IReportHtmlBuilder
{
    string Build(
        string title, DateTime fromUtc, DateTime toUtc, string boardsLabel, IReadOnlyList<ReportTicket> tickets,
        ReportTheme theme, ReportSummary? summary, string teamName, IReadOnlyList<string> boards);

    /// <summary>Render the deck from fixed mock data (Settings → "Preview format").</summary>
    string BuildMock(ReportTheme theme, string teamName);
}
