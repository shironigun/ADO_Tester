using System.Text.Json;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;

namespace QaAutomation.Tests;

/// <summary>INT-201 — session Plan map columns + ApiSessionDetail round-trip.</summary>
public sealed class PlanSuiteUnitMapTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void PlanSuiteUnitMap_round_trips_through_json()
    {
        var map = new PlanSuiteUnitMap
        {
            TestPlanId = 42,
            MappedAtUtc = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc),
            Entries =
            [
                new PlanSuiteUnitMapEntry
                {
                    SuiteId = 100,
                    SuiteName = "Deals",
                    SuitePath = "Deals / API",
                    AutomationUnit = "deals",
                    Ambiguous = false,
                },
                new PlanSuiteUnitMapEntry
                {
                    SuiteId = 101,
                    SuiteName = "Misc",
                    SuitePath = "Misc",
                    AutomationUnit = null,
                    Ambiguous = true,
                    Notes = "needs human confirm",
                },
            ],
        };

        var json = JsonSerializer.Serialize(map, JsonOpts);
        var back = JsonSerializer.Deserialize<PlanSuiteUnitMap>(json, JsonOpts);

        Assert.NotNull(back);
        Assert.Equal(42, back.TestPlanId);
        Assert.Equal(2, back.Entries.Count);
        Assert.Equal("deals", back.Entries[0].AutomationUnit);
        Assert.True(back.Entries[1].Ambiguous);
    }

    [Fact]
    public void ApiSessionDetail_From_includes_suite_map_and_import_meta_json()
    {
        var mapJson = """{"testPlanId":99,"entries":[]}""";
        var metaJson = """{"thinImproved":1,"richLinked":2,"gapsCreated":3,"selectedUnit":"deals"}""";
        var session = new ApiTestSession
        {
            Id = 7,
            ProjectId = 1,
            Module = "Deals",
            TestPlanId = 99,
            SuiteUnitMapJson = mapJson,
            PlanImportMetaJson = metaJson,
            Status = ApiSessionStatus.Analyzing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var detail = ApiSessionDetail.From(session, []);

        Assert.Equal(99, detail.TestPlanId);
        Assert.Equal(mapJson, detail.SuiteUnitMapJson);
        Assert.Equal(metaJson, detail.PlanImportMetaJson);
    }

    [Fact]
    public void ScanStartRequest_documents_required_test_plan_id()
    {
        // Compile-time contract: TestPlanId stays nullable on the wire so missing body fields
        // deserialize to null; the controller rejects null/<=0 with 400 (INT-201).
        var req = new ScanStartRequest { Module = "Deals" };
        Assert.Null(req.TestPlanId);
        req.TestPlanId = 123;
        Assert.Equal(123, req.TestPlanId);
    }
}
