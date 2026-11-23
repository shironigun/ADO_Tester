using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Tests;

/// <summary>INT-204 — imported drafts feed analyzer/generator context.</summary>
public sealed class PlanIntakeWiringTests
{
    [Fact]
    public void DraftsToLinkedCases_keeps_ado_ids_only()
    {
        var drafts = new List<ApiTestDraft>
        {
            new() { AdoTestCaseId = 10, Title = "A", Preconditions = "", StepsJson = "[]" },
            new() { AdoTestCaseId = null, Title = "Gap", Preconditions = "", StepsJson = "[]" },
            new()
            {
                AdoTestCaseId = 20, Title = "B", Preconditions = "x",
                StepsJson = """[{"action":"Click","expected":"Ok"}]""",
            },
        };
        var linked = QaLoopOrchestrator.DraftsToLinkedCases(drafts);
        Assert.Equal(2, linked.Count);
        Assert.Equal(10, linked[0].Id);
        Assert.Equal(20, linked[1].Id);
        Assert.Single(linked[1].Steps);
    }

    [Fact]
    public void FormatImportedDraftsContext_lists_import_tags_and_ado_ids()
    {
        var drafts = new List<ApiTestDraft>
        {
            new()
            {
                AdoTestCaseId = 99, Title = "Thin case", SortOrder = 0,
                StepsJson = "[]", Tags = """["import-thin"]""",
            },
            new()
            {
                AdoTestCaseId = null, Title = "Gap case", SortOrder = 1,
                StepsJson = "[]", Tags = """["import-gap"]""",
            },
        };
        var ctx = QaLoopOrchestrator.FormatImportedDraftsContext(drafts);
        Assert.NotNull(ctx);
        Assert.Contains("ado#99", ctx);
        Assert.Contains("Thin case", ctx);
        Assert.Contains("Gap case", ctx);
    }

    [Fact]
    public void ApiSessionStatus_includes_ImportingPlan()
    {
        Assert.Equal("ImportingPlan", ApiSessionStatus.ImportingPlan.ToString());
        Assert.Equal("plan-intake", EventPhases.PlanIntake);
    }
}
