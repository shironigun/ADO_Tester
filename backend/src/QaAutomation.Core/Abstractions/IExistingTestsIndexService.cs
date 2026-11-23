using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Cached scan/index of existing ADO test cases so the Tester can avoid duplicating coverage that
/// already exists in a reference Test Plan. Two enforcement layers:
/// (1) prompt-level — <see cref="BuildTesterContextAsync"/> injects existing titles + pattern summary;
/// (2) post-gen — <see cref="FindDuplicatesAsync"/> similarity-checks generated cases against the index.
/// The index is persisted to PostgreSQL and reused across runs; a staleness TTL + forceRescan keep it current.
/// </summary>
public interface IExistingTestsIndexService
{
    /// <summary>Ensure the index for <paramref name="planId"/>/<paramref name="suiteId"/> is up to date.
    /// First-time (cache miss) or <paramref name="forceRescan"/>: scans ADO and upserts. Otherwise returns the cached scan.</summary>
    Task<TestPlanScan> EnsureIndexAsync(int planId, int? suiteId, bool forceRescan, CancellationToken ct);

    /// <summary>Return the most recent scan metadata without rescanning. Returns null if never scanned.</summary>
    Task<TestPlanScan?> GetScanStatusAsync(int planId, int? suiteId, CancellationToken ct);

    /// <summary>Build the dedup block for the Tester prompt: all existing titles grouped + title-pattern
    /// summary, within <paramref name="byteBudget"/>. Returns null when no index exists.</summary>
    Task<string?> BuildTesterContextAsync(int planId, int? suiteId, AnalystOutput? analysis, int byteBudget, CancellationToken ct);

    /// <summary>Post-gen similarity check on <paramref name="cases"/>.
    /// Returns a result for each case that is a clear duplicate or borderline match in the index.</summary>
    Task<IReadOnlyList<DedupResult>> FindDuplicatesAsync(
        int planId, int? suiteId, IReadOnlyList<DedupTestCase> cases, CancellationToken ct);
}

/// <summary>Outcome of a single dedup check.</summary>
/// <param name="Title">The generated test case's title.</param>
/// <param name="IsClearDuplicate">True → auto-drop; false → borderline, flag for review.</param>
/// <param name="MatchedTitle">The existing ADO TC title that matched.</param>
/// <param name="MatchedAdoId">The existing ADO TC work-item id.</param>
public sealed record DedupResult(string Title, bool IsClearDuplicate, string? MatchedTitle, int? MatchedAdoId);

/// <summary>A test case passed to the dedup index for similarity checking. Distinct from the full
/// <see cref="QaAutomation.Core.Models.GeneratedTestCase"/> (which carries Steps/Preconditions).</summary>
/// <param name="Title">Test case title.</param>
/// <param name="StepsSummary">Concatenated step actions for overlap check.</param>
public sealed record DedupTestCase(string Title, string StepsSummary);
