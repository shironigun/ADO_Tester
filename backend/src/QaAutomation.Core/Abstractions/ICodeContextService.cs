using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Shared code-discovery service. Anchors on a work item's linked PRs/commits (and optionally a
/// free-text hint) to find, read, and triage the relevant repo files — reusable across the Author
/// Tests and Find Bugs pipelines.
/// </summary>
public interface ICodeContextService
{
    /// <param name="workItemId">ADO work item id for linked-PR/commit lookup.</param>
    /// <param name="wi">Fetched work item (already retrieved by the caller).</param>
    /// <param name="analysis">Analyst output used for search-term building. Pass null when discovery
    /// runs before the Analyst (Author Tests) — the service falls back to <paramref name="wi"/>.Title.</param>
    /// <param name="commitIds">Manually supplied commit SHAs (in addition to auto-discovered ones).</param>
    /// <param name="prIds">Manually supplied PR ids (in addition to auto-discovered ones).</param>
    /// <param name="extraHintText">Free-text hints appended to the code-search query (e.g. route/screen names).</param>
    /// <param name="alwaysSearch">When true, run a code search even when linked PRs/commits exist (Author Tests
    /// thin-ticket path). When false (Find Bugs default), only search when no linked refs are found.</param>
    /// <param name="llm">LLM client for the Triage pass.</param>
    /// <param name="key">SSE session key for event publishing.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(string DiffSummary, string Code)> DiscoverAsync(
        int workItemId, AdoWorkItem wi, AnalystOutput? analysis,
        IReadOnlyList<string> commitIds, IReadOnlyList<string> prIds,
        string? extraHintText, bool alwaysSearch,
        ILlmClient llm, string key, CancellationToken ct);
}
