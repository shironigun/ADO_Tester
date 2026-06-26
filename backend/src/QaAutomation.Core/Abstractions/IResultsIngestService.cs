using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Ingests a Playwright JUnit run, triages each failure (real_bug / flaky / test_issue / stale_test),
/// and files a house-style Bug in ADO for real bugs — linked to the failing Test Case (Track A triage
/// loop). The Pass/Fail publish back to the test plan is Track B's azure-reporter; this is the bug seam.
/// </summary>
public interface IResultsIngestService
{
    Task<IngestResultsResult> IngestAsync(int projectId, IngestResultsRequest request, CancellationToken ct = default);
}
