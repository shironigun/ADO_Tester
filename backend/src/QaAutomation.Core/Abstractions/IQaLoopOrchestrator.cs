namespace QaAutomation.Core.Abstractions;

/// <summary>Runs the QA Loop scan → analyze → generate → push pipeline.</summary>
public interface IQaLoopOrchestrator
{
    /// <summary>
    /// Seeds a new ApiTestSession synchronously and launches the background analyze pipeline.
    /// Returns the session id immediately.
    /// </summary>
    Task<int> StartAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the three generators in parallel (Contract/Endpoint/Workflow) for all selected surfaces
    /// on the session. Transitions the session from AwaitingReview to Drafting → Drafted.
    /// </summary>
    Task GenerateDraftsAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes all Approved drafts in the session to ADO (creates Test Case, adds to suite,
    /// links Tested-By to the story when WorkItemId set). Tag-rewrites [<tcId-pending>] on success.
    /// Returns asynchronously; transitions Drafted → Pushing → Done.
    /// </summary>
    Task PushAllAsync(int sessionId, int testPlanId, int testSuiteId, int? storyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously pushes a single Approved draft to ADO and returns it.
    /// </summary>
    Task<Core.Models.ApiTestDraft> PushDraftAsync(int draftId, int testPlanId, int testSuiteId,
        int? storyId = null, CancellationToken cancellationToken = default);

    /// <summary>Signals the background pipeline for the given session to stop.</summary>
    bool Cancel(int sessionId);
}
