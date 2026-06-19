using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

public interface IReportService
{
    /// <summary>Kicks off report generation in the background. Returns the new report ID.</summary>
    Task<int> GenerateAsync(
        int projectId, DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
        string trigger = "Manual", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Report>> ListAsync(int projectId, CancellationToken cancellationToken = default);

    Task<Report?> GetAsync(int reportId, CancellationToken cancellationToken = default);

    Task<string?> ReadHtmlAsync(int reportId, CancellationToken cancellationToken = default);
}
