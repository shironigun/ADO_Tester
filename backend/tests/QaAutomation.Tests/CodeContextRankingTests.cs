using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.CodeContext;

namespace QaAutomation.Tests;

/// <summary>
/// Changed-file ranking for code discovery. A large PR used to keep the first 20 files in API order, which
/// can put config and wiring ahead of the service the story is actually about; the verifier then had no code
/// to judge and returned "inconclusive" for every case.
/// </summary>
public sealed class CodeContextRankingTests
{
    private static ChangedFile F(string path, string type = "edit") => new(path, type);

    private static List<string> Order(IReadOnlyList<ChangedFile> files, params string?[] storyText) =>
        CodeContextService.RankChanged(files, CodeContextService.StoryTerms(storyText)).Select(f => f.Path).ToList();

    [Fact]
    public void Implementation_outranks_wiring_config_and_generated_files()
    {
        var files = new[]
        {
            F("/src/Api/Configurations/ConfigurationExtensions.cs"),
            F("/src/Api/Program.cs"),
            F("/src/Api/appsettings.Development.json"),
            F("/src/Data/Migrations/20260101_AddInvoiceMerge.Designer.cs", "add"),
            F("/src/Domain/InvoiceMergeService.cs"),
            F("/db/Invoices.GetMergeCandidates.sql", "add"),
        };

        var ordered = Order(files, "Merge duplicate invoices");

        Assert.Equal("/src/Domain/InvoiceMergeService.cs", ordered[0]);
        Assert.Equal("/db/Invoices.GetMergeCandidates.sql", ordered[1]);
        Assert.Equal("/src/Data/Migrations/20260101_AddInvoiceMerge.Designer.cs", ordered[^1]);   // generated: last
        Assert.True(ordered.IndexOf("/src/Domain/InvoiceMergeService.cs") < ordered.IndexOf("/src/Api/Program.cs"));
    }

    [Fact]
    public void Paths_that_mention_the_storys_own_words_rank_above_unrelated_logic()
    {
        var files = new[]
        {
            F("/src/Domain/ShippingLabelService.cs"),
            F("/src/Domain/InvoiceMergeService.cs"),
        };

        // Both are "services"; only one is about the story. Plurals fold ("invoices" → "invoice").
        Assert.Equal("/src/Domain/InvoiceMergeService.cs", Order(files, "Merge duplicate invoices")[0]);
    }

    [Fact]
    public void Tests_rank_below_the_code_they_cover_and_ties_keep_api_order()
    {
        var files = new[]
        {
            F("/tests/InvoiceMergeServiceTests.cs"),
            F("/src/Domain/InvoiceMergeService.cs"),
            F("/src/Domain/Alpha.cs"),
            F("/src/Domain/Beta.cs"),
        };

        var ordered = Order(files);   // no story text: pure structure + stable ties

        Assert.Equal("/src/Domain/InvoiceMergeService.cs", ordered[0]);
        Assert.True(ordered.IndexOf("/src/Domain/Alpha.cs") < ordered.IndexOf("/src/Domain/Beta.cs"));   // API order kept
        Assert.Equal("/tests/InvoiceMergeServiceTests.cs", ordered[^1]);
    }

    [Fact]
    public void The_twenty_file_cut_now_keeps_the_service_in_a_large_pr()
    {
        // 24 config/boilerplate files sort before the service in API (alphabetical) order.
        var files = Enumerable.Range(0, 24).Select(i => F($"/src/Api/Configurations/Setting{i:00}.cs"))
            .Append(F("/src/Domain/InvoiceMergeService.cs")).ToList();

        var kept = CodeContextService.RankChanged(files, CodeContextService.StoryTerms("Merge duplicate invoices"))
            .Take(20).Select(f => f.Path).ToList();

        Assert.Contains("/src/Domain/InvoiceMergeService.cs", kept);                        // the old Take(20) dropped it
        Assert.DoesNotContain("/src/Domain/InvoiceMergeService.cs", files.Take(20).Select(f => f.Path));
    }

    [Fact]
    public void StoryTerms_folds_plurals_and_drops_short_and_filler_words()
    {
        var terms = CodeContextService.StoryTerms("Transfer customers as contacts", null, "User feature");

        Assert.Contains("transfer", terms);
        Assert.Contains("customer", terms);   // customers → customer
        Assert.Contains("contact", terms);    // contacts → contact
        Assert.DoesNotContain("as", terms);
        Assert.DoesNotContain("user", terms);
        Assert.DoesNotContain("feature", terms);
    }
}
