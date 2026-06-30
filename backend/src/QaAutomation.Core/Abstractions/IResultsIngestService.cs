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

    // ── QA Loop split: triage-persist (never files) + user-gated file-bugs ──

    /// <summary>
    /// Triage all un-triaged failures for a QA Loop run: calls FailureTriageAgent, updates
    /// TestFailure rows (Classification/Confidence/Reason/BugDraftJson). NEVER files to ADO.
    /// </summary>
    Task<TriageResult> TriageAsync(int runId, CancellationToken ct = default);

    /// <summary>
    /// Files ADO Bugs from pre-built BugDraftJson on the selected TestFailure rows.
    /// No second LLM call. Links each Bug to its Test Case + optional story.
    /// </summary>
    Task<FileBugsResult> FileBugsAsync(int runId, IReadOnlyList<int> failureIds, int? storyId, CancellationToken ct = default);
}
