using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Reports;

namespace QaAutomation.Tests;

/// <summary>Frozen-contract tests for the report builder (spec B7/B10): board-name derivation
/// (the by-board grouping depends on it) and the <c>window.__REPORT__</c> <c>&lt;/</c> escape (security).</summary>
public sealed class ReportHtmlBuilderTests
{
    [Theory]
    [InlineData("Ideal Agile\\CRM Team", "CRM Team")]   // 2nd segment
    [InlineData("Ideal Agile/CRM Team", "CRM Team")]    // forward slash normalized to backslash
    [InlineData("Ideal Agile\\CRM Team\\Sub", "CRM Team")] // still the 2nd segment
    [InlineData("Ideal Agile", "Ideal Agile")]          // single segment → fallback to first
    [InlineData("", "Other")]                            // empty → Other
    [InlineData(null, "Other")]                          // null → Other
    public void BoardLabel_uses_second_area_path_segment(string? areaPath, string expected)
        => Assert.Equal(expected, ReportHtmlBuilder.BoardLabel(areaPath));

    [Fact]
    public void Build_escapes_closing_tags_in_window_REPORT_data()
    {
        // A ticket whose text contains "</script>" must be escaped to "<\/script>" inside the
        // window.__REPORT__ blob — otherwise it would break out of the <script> element (XSS).
        // title is serialized raw into window.__REPORT__ (description is sanitized), so it's the
        // field that exercises the </ -> <\/ escape.
        var ticket = new ReportTicket
        {
            Id = 1,
            Title = "Import bug </script> x",
            WorkItemType = "Bug",
            State = "Active",
            AssignedTo = "QA",
            AreaPath = "Ideal Agile\\CRM Team",
        };

        var html = new ReportHtmlBuilder().Build(
            "Weekly Report",
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc),
            boardsLabel: "",
            tickets: [ticket],
            theme: ReportTheme.Executive,
            summary: null,
            teamName: "QA Team",
            boards: []);

        // System.Text.Json escapes '<' to < by default, so the title's "</script>" can't break
        // out of the <script> element — it appears as </script in the window.__REPORT__ data.
        Assert.Contains("\\u003C/script", html);
        Assert.DoesNotContain("Import bug </script>", html);   // never emitted raw
    }
}
