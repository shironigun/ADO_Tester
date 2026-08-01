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

    /// <summary>
    /// Re-runs the Analyzer on the existing inventory + surface with reviewer feedback,
    /// improving the analysis in place. Session returns to AwaitingReview.
    /// </summary>
    Task RerunAnalyzeAsync(int sessionId, string feedback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges per-draft feedback into existing drafts. Targeted Draft/Rejected rows are refined in place;
    /// flagged rows are removed; Approved + Pushed rows are never touched.
    /// </summary>
    Task RefineDraftsAsync(int sessionId, string? overallFeedback, IReadOnlyList<Core.Contracts.ApiDraftFeedback> perDraft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs the QA Loop UI Analyst on the existing <c>UiAnalysisJson</c> with reviewer feedback.
    /// Guard: <c>UiStatus == AwaitingUiAnalysisReview</c>. Replaces output in place.
    /// </summary>
    Task RerunUiAnalysisAsync(int sessionId, string feedback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the UI test-case authoring agent for the session. Transitions UiStatus:
    /// Authoring → AwaitingReview; persists ApiTestDraft rows with DraftKind=UiCase.
    /// </summary>
    Task AuthorUiCasesAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns the Playwright MCP agent for each Approved UiCase draft; writes a .ui.spec.ts per case;
    /// inserts a sibling UiScript draft with SpecCode/SpecFilePath set; transitions UiStatus=Drafted.
    /// </summary>
    Task GenerateUiScriptsAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs Approved UiScript drafts via Playwright (step 10b). Transitions UiStatus:
    /// RunningUiScripts → Drafted; persists per-draft <c>ErrorMessage</c> from JUnit results.
    /// </summary>
    Task RunUiScriptsAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Heals the selected UiScript drafts by running the Playwright healer agent on each spec.
    /// The spec file is overwritten in place; SpecCode is updated; Status is reset to Draft.
    /// Individual heal failures are logged and skipped (batch continues).
    /// Optional <paramref name="userContext"/> is appended to each draft's failure message.
    /// </summary>
    Task HealSpecsAsync(int sessionId, int[] draftIds, string? userContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>Signals the background pipeline for the given session to stop.</summary>
    bool Cancel(int sessionId);
}
