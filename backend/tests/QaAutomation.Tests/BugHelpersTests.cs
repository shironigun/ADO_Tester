using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Tests;

/// <summary>Unit tests for the pure Find-Bugs helpers (B5c): query building, import resolution,
/// severity parsing, and the verdict summary.</summary>
public sealed class BugHelpersTests
{
    [Fact]
    public void SearchQuery_takes_first_three_significant_words()
    {
        // "by" and "and" are stopwords; only the first 3 significant words are kept.
        Assert.Equal("Search VIN stock", BugHelpers.SearchQuery("Search by VIN and stock number guided"));
    }

    [Fact]
    public void SearchQuery_uses_first_non_empty_source()
    {
        Assert.Equal("Lead dedupe", BugHelpers.SearchQuery("", "  ", "Lead dedupe"));
    }

    [Fact]
    public void ExtractRelativeImports_keeps_only_relative_specifiers()
    {
        const string src = "import a from './foo';\nimport b from 'react';\nconst c = require('../bar/baz');";
        Assert.Equal(["./foo", "../bar/baz"], BugHelpers.ExtractRelativeImports(src));
    }

    [Fact]
    public void ResolveImport_extensionless_yields_ext_and_index_guesses()
    {
        var cands = BugHelpers.ResolveImport("./foo", "src/a.ts");
        Assert.Equal("src/foo.ts", cands[0]);
        Assert.Contains("src/foo.tsx", cands);
        Assert.Contains("src/foo/index.ts", cands);
        Assert.DoesNotContain("src/foo/index.cs", cands);   // index guesses only use the first 4 exts
    }

    [Fact]
    public void ResolveImport_collapses_parent_segments_and_keeps_explicit_extension()
    {
        Assert.Equal(["src/util.ts"], BugHelpers.ResolveImport("../util.ts", "src/a/b.ts"));
    }

    [Theory]
    [InlineData("critical", BugSeverity.Critical)]
    [InlineData("1", BugSeverity.Critical)]
    [InlineData("High priority", BugSeverity.High)]
    [InlineData("2", BugSeverity.High)]
    [InlineData("low", BugSeverity.Low)]
    [InlineData("4", BugSeverity.Low)]
    [InlineData("something else", BugSeverity.Medium)]
    [InlineData("", BugSeverity.Medium)]
    public void ParseSeverity_maps_text_and_numeric(string input, BugSeverity expected)
    {
        Assert.Equal(expected, BugHelpers.ParseSeverity(input));
    }

    [Fact]
    public void VerdictActualSummary_assembles_present_parts()
    {
        var summary = BugHelpers.VerdictActualSummary("Search returns fresh row", ["Search VIN", "Edit then search"], "Fresh row", "Stale row");
        Assert.Contains("A verified test case failed: Search returns fresh row.", summary);
        Assert.Contains("- Search VIN", summary);
        Assert.Contains("Expected: Fresh row", summary);
        Assert.Contains("Observed (from static code verification): Stale row", summary);
    }

    [Fact]
    public void VerdictActualSummary_omits_empty_parts()
    {
        var summary = BugHelpers.VerdictActualSummary("Case", [], "", "");
        Assert.Equal("A verified test case failed: Case.", summary);
    }
}
