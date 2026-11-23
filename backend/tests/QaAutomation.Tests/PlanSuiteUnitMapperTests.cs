using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>INT-202 — Deals-focused suite→unit map smoke (heuristic, no ADO/LLM).</summary>
public sealed class PlanSuiteUnitMapperTests
{
    [Fact]
    public void MapSuites_maps_deals_and_cpq_and_flags_unknown()
    {
        var suites = new List<AdoTestSuite>
        {
            new(1, "Root"),
            new(10, "Deals", ParentId: 1),
            new(11, "API", ParentId: 10),
            new(12, "Contracts", ParentId: 11),
            new(20, "CPQ+", ParentId: 10),
            new(30, "Data Review", ParentId: 1),
            new(40, "Misc Bucket", ParentId: 1),
        };

        // Use MapSuites via a thin throwaway — MatchUnit/BuildPath are internal static on the class.
        // Instantiate is heavy; call through a local helper that mirrors MapSuites by constructing
        // PlanSuiteUnitMapper is DI-heavy — test static MatchUnit + BuildPath + a local Map.
        var byId = suites.ToDictionary(s => s.Id);
        Assert.Equal("Root / Deals / API / Contracts", PlanSuiteUnitMapper.BuildPath(suites[3], byId));

        var (dealsUnit, dealsAmb, _) = PlanSuiteUnitMapper.MatchUnit("Deals", "Root / Deals");
        Assert.Equal("deals", dealsUnit);
        Assert.False(dealsAmb);

        var (cpqUnit, cpqAmb, _) = PlanSuiteUnitMapper.MatchUnit("CPQ+", "Root / Deals / CPQ+");
        Assert.Equal("cpq-plus", cpqUnit);
        Assert.False(cpqAmb);

        var (drUnit, drAmb, _) = PlanSuiteUnitMapper.MatchUnit("Data Review", "Root / Data Review");
        Assert.Equal("customers-data-review", drUnit);
        Assert.False(drAmb);

        var (unkUnit, unkAmb, notes) = PlanSuiteUnitMapper.MatchUnit("Misc Bucket", "Root / Misc Bucket");
        Assert.Null(unkUnit);
        Assert.True(unkAmb);
        Assert.Contains("no alias", notes ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapSuites_full_tree_persists_deals_path_entries()
    {
        // Exercise MapSuites without DI: construct with nulls is unsafe — use a stub factory path.
        // Instead build the map the same way MapSuites does (copy of logic via public MapSuites needs instance).
        // Create mapper with null-object stubs via Fake — simplest: new with NSubstitute-free manual fakes.
        var mapper = new PlanSuiteUnitMapper(
            ado: null!, llmFactory: null!, db: null!,
            log: Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanSuiteUnitMapper>.Instance);

        var suites = new List<AdoTestSuite>
        {
            new(1, "Plan Root"),
            new(2, "Deals", 1),
            new(3, "Contracts", 2),
            new(4, "Endpoints", 2),
            new(5, "Weird Folder", 1),
        };

        var map = mapper.MapSuites(99, suites);
        Assert.Equal(99, map.TestPlanId);
        Assert.Equal(5, map.Entries.Count);

        var deals = map.Entries.Single(e => e.SuiteId == 2);
        Assert.Equal("deals", deals.AutomationUnit);
        Assert.False(deals.Ambiguous);

        var contracts = map.Entries.Single(e => e.SuiteId == 3);
        // Name "Contracts" alone won't match; path "Plan Root / Deals / Contracts" → deals via path
        Assert.Equal("deals", contracts.AutomationUnit);
        Assert.False(contracts.Ambiguous);

        var weird = map.Entries.Single(e => e.SuiteId == 5);
        Assert.True(weird.Ambiguous);
        Assert.Null(weird.AutomationUnit);
    }
}
