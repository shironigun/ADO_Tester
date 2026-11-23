namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Seeds product <c>automation/resources/impact-map.json</c> from read-only service
/// <c>*/build/build-qa.yml</c> path includes, and simulates tag selection for a change set (INT-401).
/// </summary>
public interface IImpactMapService
{
    /// <summary>
    /// Rebuild/merge Deals (+ future unit) entries from CRM <c>build-qa.yml</c> files.
    /// Writes only under <c>AUTOMATION_ROOT/resources/</c>.
    /// </summary>
    ImpactMapSeedResult SeedFromBuildQa();

    /// <summary>
    /// Select Playwright grep tags for the given changed paths (repo-relative, forward slashes).
    /// Mirrors product <c>pipelines/run-impacted.ts</c> path rules.
    /// </summary>
    ImpactPathSimResult Simulate(IReadOnlyList<string> changedFiles);
}

public sealed record ImpactMapSeedResult(
    string WrittenPath,
    int PathPrefixCount,
    IReadOnlyList<string> UnitsSeeded,
    IReadOnlyList<string> SourcesRead);

public sealed record ImpactPathSimResult(
    string Grep,
    IReadOnlyList<string> SelectedTags,
    IReadOnlyList<string> MatchedFiles,
    bool UsedSmokeFallback);
