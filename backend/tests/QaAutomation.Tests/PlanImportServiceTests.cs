using Microsoft.EntityFrameworkCore;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>INT-203 — thin/rich/gap classification + dedup (no full ADO fake).</summary>
public sealed class PlanImportServiceTests
{
    [Fact]
    public void IsThin_empty_or_blank_steps()
    {
        Assert.True(PlanImportService.IsThin(new LinkedTestCase(1, "T", "", [])));
        Assert.True(PlanImportService.IsThin(new LinkedTestCase(2, "T", "",
            [new TestCaseStep { Action = "  ", Expected = "" }])));
        Assert.False(PlanImportService.IsThin(new LinkedTestCase(3, "T", "",
            [new TestCaseStep { Action = "Click Save", Expected = "Saved" }])));
    }

    [Fact]
    public void ResolveUnit_prefers_override_then_frs_then_module()
    {
        var s = new ApiTestSession { Module = "Deals", FrsSlug = "messaging" };
        Assert.Equal("deals", PlanImportService.ResolveUnit(s, "Deals"));
        Assert.Equal("messaging", PlanImportService.ResolveUnit(s, null));
        s.FrsSlug = null;
        Assert.Equal("deals", PlanImportService.ResolveUnit(s, null));
    }

    [Fact]
    public void ExtractGapTitles_selected_gap_rows_only()
    {
        var json = """
            {"Module":"Deals","Rows":[
              {"Id":"1","Label":"GET /deals","Status":"gap","Selected":true},
              {"Id":"2","Label":"POST /deals","Status":"gap","Selected":false},
              {"Id":"3","Label":"Covered","Status":"covered","Selected":true}
            ]}
            """;
        var gaps = PlanImportService.ExtractGapTitles(json);
        Assert.Single(gaps);
        Assert.Equal("GET /deals", gaps[0]);
    }

    [Fact]
    public void NormalizeTitle_stable_for_dedup()
    {
        Assert.Equal(
            PlanImportService.NormalizeTitle("Deals - Create Deal"),
            PlanImportService.NormalizeTitle("deals — create deal!"));
    }

    [Fact]
    public void ApplyImportedCases_dedups_ado_ids_on_rerun()
    {
        using var pg = PostgresTestDatabase.Create("plan_import");
        pg.InitializeSchema();
        using var db = pg.CreateContext();

        if (!db.Projects.Any(p => p.Id == 1))
        {
            db.Projects.Add(new Project { Id = 1, Name = "P" });
            db.SaveChanges();
        }

        var session = new ApiTestSession
        {
            ProjectId = 1,
            Module = "Deals",
            FrsSlug = "deals",
            TestPlanId = 42,
            Status = ApiSessionStatus.Analyzing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ApiTestSessions.Add(session);
        db.SaveChanges();

        var thin = new LinkedTestCase(1001, "Thin Deal Case", "", []);
        var rich = new LinkedTestCase(1002, "Rich Deal Case", "",
            [new TestCaseStep { Action = "Open deals", Expected = "List shows" }]);
        var gaps = new[] { "Missing workflow X" };
        var adoIds = new HashSet<int>();
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        var meta1 = PlanImportService.ApplyImportedCases(
            db, session.Id, [thin, rich], gaps, adoIds, titles, 0, now);
        db.SaveChanges();
        Assert.Equal(1, meta1.ThinImproved);
        Assert.Equal(1, meta1.RichLinked);
        Assert.Equal(1, meta1.GapsCreated);
        Assert.Equal(3, db.ApiTestDrafts.Count(d => d.SessionId == session.Id));

        var meta2 = PlanImportService.ApplyImportedCases(
            db, session.Id, [thin, rich], gaps, adoIds, titles, 10, now);
        db.SaveChanges();
        Assert.Equal(0, meta2.ThinImproved);
        Assert.Equal(0, meta2.RichLinked);
        Assert.Equal(0, meta2.GapsCreated);
        Assert.Equal(3, db.ApiTestDrafts.Count(d => d.SessionId == session.Id));

        var ids = db.ApiTestDrafts.Where(d => d.SessionId == session.Id && d.AdoTestCaseId != null)
            .Select(d => d.AdoTestCaseId!.Value).OrderBy(x => x).ToList();
        Assert.Equal([1001, 1002], ids);
    }
}
