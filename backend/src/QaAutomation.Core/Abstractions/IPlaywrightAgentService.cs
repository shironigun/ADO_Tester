namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Spawns the local Claude CLI in agentic mode (Playwright MCP) to generate and heal
/// Playwright UI test specs. Scoped — resolved in background-task DI scopes.
/// </summary>
public interface IPlaywrightAgentService
{
    /// <summary>
    /// Asks the Claude CLI agent to generate a Playwright `.ui.spec.ts` for the given test case.
    /// Returns the absolute path of the written spec file.
    /// </summary>
    Task<string> GenerateScriptAsync(
        string caseTitle, string stepsJson, string bucket, int tcIdPending,
        IProgress<string>? progress, CancellationToken ct, string? moduleRoute = null);

    /// <summary>
    /// Asks the Claude CLI healer agent to fix the spec at <paramref name="specFilePath"/>
    /// given the <paramref name="failureMessage"/> from the last Playwright run.
    /// The spec is overwritten in place.
    /// </summary>
    Task HealAsync(
        string specFilePath, string failureMessage,
        IProgress<string>? progress, CancellationToken ct);
}
