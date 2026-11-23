using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>Maps ADO Test Plan suite folders → automation unit slugs (INT-202).</summary>
public interface IPlanSuiteUnitMapper
{
    /// <summary>Pure map from an already-fetched suite list (unit-testable; no I/O).</summary>
    PlanSuiteUnitMap MapSuites(int planId, IReadOnlyList<AdoTestSuite> suites);

    /// <summary>Fetch suite tree from ADO, map, optionally LLM-resolve ambiguous rows.</summary>
    Task<PlanSuiteUnitMap> FetchAndMapAsync(int planId, bool useAiForAmbiguous = true, CancellationToken ct = default);

    /// <summary>Fetch+map for a session's TestPlanId and persist <c>SuiteUnitMapJson</c>.</summary>
    Task<PlanSuiteUnitMap> MapAndPersistSessionAsync(int sessionId, bool useAiForAmbiguous = true, CancellationToken ct = default);
}
