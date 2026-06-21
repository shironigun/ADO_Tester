namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Runs the Analyst → Tester pipeline for a given work item.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Starts a new session: fetches the work item, runs the Analyst, and (if autoHandoff)
    /// immediately runs the Tester. Returns the session ID; the caller polls for completion.
    /// </summary>
    Task<int> StartAsync(
        int projectId,
        int workItemId,
        bool autoHandoff = true,
        int? batchId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs just the Tester on an existing session whose Analyst has already completed.
    /// Used when the user edited the analysis before triggering Tester.
    /// </summary>
    Task RunTesterAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves and pushes all Approved drafts in a session to ADO
    /// (creates Test Case work items, adds to plan/suite, links to story).
    /// </summary>
    Task PushToAdoAsync(
        int sessionId,
        int testPlanId,
        int testSuiteId,
        CancellationToken cancellationToken = default);

    /// <summary>Signals the background pipeline for the given session to stop.</summary>
    bool Cancel(int sessionId);
}
