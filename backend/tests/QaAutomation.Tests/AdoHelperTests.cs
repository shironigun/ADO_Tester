using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.AzureDevOps;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Tests;

/// <summary>Pure-function frozen-contract tests (spec §5.5/§8): the HTML escaper, the Steps-XML
/// round-trip, and the secret sanitizer.</summary>
public sealed class AdoHelperTests
{
    [Fact]
    public void HtmlEscape_matches_python_quote_true()
        => Assert.Equal("a&amp;b&lt;c&gt;&quot;d&#x27;e", AzureDevOpsClient.HtmlEscape("a&b<c>\"d'e"));

    [Fact]
    public void StepsXml_round_trips_through_parse()
    {
        // No embedded angle-brackets: _strip_tags' <[^>]+> regex would (intentionally, like Python)
        // drop tag-like substrings. This case exercises &, ", ', and a non-ASCII em-dash losslessly.
        var steps = new List<TestCaseStep>
        {
            new() { Action = "Click \"SAVE\" & go", Expected = "Record's saved — done" },
            new() { Action = "Login to TestApp", Expected = "Dashboard loads" },
        };
        var parsed = AzureDevOpsClient.ParseStepsXml(AzureDevOpsClient.BuildStepsXml(steps));
        Assert.Equal(steps.Count, parsed.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            Assert.Equal(steps[i].Action, parsed[i].Action);
            Assert.Equal(steps[i].Expected, parsed[i].Expected);
        }
    }

    [Fact]
    public void ParseStepsXml_strips_embedded_tags_like_python()
    {
        // Mirrors Python _strip_tags: any <...> substring is removed (lossy by design).
        var xml = AzureDevOpsClient.BuildStepsXml([new TestCaseStep { Action = "see <b>bold</b>", Expected = "ok" }]);
        var parsed = AzureDevOpsClient.ParseStepsXml(xml);
        Assert.Single(parsed);
        Assert.Equal("see  bold", parsed[0].Action);   // <b> and </b> → spaces, trimmed
    }

    [Fact]
    public void Sanitize_empty_returns_empty() => Assert.Equal("", Sanitizer.Sanitize(""));

    [Fact]
    public void Sanitize_redacts_only_secrets_longer_than_six()
    {
        Assert.Equal("x ***REDACTED*** y", Sanitizer.Sanitize("x SECRET-LONG y", "SECRET-LONG")); // len 11 > 6 → redacted
        Assert.Equal("x short y", Sanitizer.Sanitize("x short y", "short"));                        // len 5 ≤ 6 → kept
    }

    [Fact]
    public void Sanitize_truncates_over_max_length()
    {
        var s = Sanitizer.Sanitize(new string('a', 3000));
        Assert.Equal(Sanitizer.MaxLength + "…[truncated]".Length, s.Length);
        Assert.EndsWith("…[truncated]", s);
    }
}
