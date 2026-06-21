using System.Text.Json.Nodes;
using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Find-Bugs pipeline (spec B5): Analyst → (park at AwaitingActual) → anchored code-verify →
/// Bug Writer → push. Mirrors <see cref="IAgentOrchestrator"/> but for bugs.
/// </summary>
public interface IBugOrchestrator
{
    /// <summary>Seed a BugSession and run the Analyst. Parks at AwaitingActual unless <paramref name="auto"/>
    /// (Epic AUTO run), which continues straight into code-verify + Bug Writer. Returns the session id.</summary>
    Task<int> StartAsync(int projectId, int workItemId, bool auto = false, CancellationToken cancellationToken = default);

    /// <summary>Run the code-analysis phase (always on) + Bug Writer against the supplied observed actual.</summary>
    Task RunBugWriterAsync(
        int sessionId, string actualBehavior, string? codeContext,
        bool verifyCode = false, IReadOnlyList<string>? commitIds = null, IReadOnlyList<string>? prIds = null,
        IReadOnlyList<string>? selectedTestCases = null, CancellationToken cancellationToken = default);

    /// <summary>Create Bug work items for all Approved drafts and Related-link them to the story.</summary>
    Task PushToAdoAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>The attached test cases for a session's story (ADO-linked + Author-Tests drafts). No LLM.</summary>
    Task<IReadOnlyList<AttachedTestCase>> ListTestCasesAsync(int sessionId, CancellationToken cancellationToken = default);

    /// <summary>Create ONE bug draft from a single verified test-case verdict (instant or agent mode).
    /// Returns the new draft id.</summary>
    Task<int> CreateBugFromVerdictAsync(int sessionId, JsonObject verdict, CancellationToken cancellationToken = default);

    /// <summary>Signal the background pipeline for the given session to stop.</summary>
    bool Cancel(int sessionId);
}
