using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Runs the Analyst → Tester pipeline for a given work item.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Starts a new session: fetches the work item, runs the Analyst, and (if autoHandoff)
    /// immediately runs the Tester. Returns the session ID; the caller polls for completion.
    /// <paramref name="context"/> carries optional free-text guidance + a reference Test Plan.
    /// </summary>
    Task<int> StartAsync(
        int projectId,
        int workItemId,
        bool autoHandoff = true,
        AuthorRunContext? context = null,
        int? batchId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs just the Tester on an existing session whose Analyst has already completed.
    /// Used when the user edited the analysis before triggering Tester. <paramref name="context"/>
    /// may supply extra Tester context and/or a reference Test Plan (persisted on the session).
    /// </summary>
    Task RunTesterAsync(int sessionId, AuthorRunContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs the Analyst with reviewer <paramref name="feedback"/>, improving/filling gaps in the
    /// existing analysis rather than starting from scratch. Session returns to AwaitingTester.
    /// </summary>
    Task RerunAnalystAsync(int sessionId, string feedback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Feedback-driven Tester refine that MERGES into existing drafts: updates flagged drafts, adds new
    /// ones, and removes only drafts explicitly flagged; pushed drafts are never touched.
    /// </summary>
    Task RefineTesterAsync(
        int sessionId,
        string? overallFeedback,
        IReadOnlyList<TestFeedback> perTest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves and pushes all Approved drafts in a session to ADO
    /// (creates Test Case work items, adds to plan/suite, links to story).
    /// </summary>
    Task PushToAdoAsync(
        int sessionId,
        int testPlanId,
        int testSuiteId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes a SINGLE draft to ADO synchronously (create Test Case, add to plan/suite, link to the
    /// story), marks it Pushed with its new Test Case id, and returns it. Backs the per-row push button.
    /// </summary>
    Task<TestCaseDraft> PushDraftAsync(
        int draftId,
        int testPlanId,
        int testSuiteId,
        CancellationToken cancellationToken = default);

    /// <summary>Signals the background pipeline for the given session to stop.</summary>
    bool Cancel(int sessionId);
}
