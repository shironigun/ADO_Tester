using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.AzureDevOps;

namespace QaAutomation.Tests;

/// <summary>
/// Golden-file diff (spec §8): the .NET ADO client must build byte-identical write bodies to the live
/// Python. <c>golden/ado_golden.json</c> is captured from the running Python client (see
/// <c>golden_capture.py</c>) with inputs that exercise ' " &amp; &lt; &gt; and a non-ASCII em-dash.
/// Bodies are compared structurally (parse → canonical re-serialize) so JSON whitespace differences
/// don't matter, but every op/path/value — including the Steps XML + bug HTML strings and the integer
/// Priority — must match exactly.
/// </summary>
public sealed class AdoGoldenTests : IDisposable
{
    private readonly JsonDocument _golden;
    private readonly JsonElement _root;
    private readonly CapturingHandler _handler = new();
    private readonly AzureDevOpsClient _client;

    public AdoGoldenTests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "ado_golden.json");
        _golden = JsonDocument.Parse(File.ReadAllText(path));
        _root = _golden.RootElement;

        var opts = new AzureDevOpsOptions
        {
            Organization = "YourOrg",
            Project = "Your Project",
            ApiVersion = "7.1",
            PersonalAccessToken = "dummy",
            BugAreaPath = _root.GetProperty("areaPath").GetString()!,
            QaOwner = _root.GetProperty("qaOwner").GetString()!,
        };
        _client = new AzureDevOpsClient(new HttpClient(_handler), new StaticOptionsMonitor<AzureDevOpsOptions>(opts), NullLogger<AzureDevOpsClient>.Instance);
    }

    [Fact]
    public async Task CreateTestCase_patch_matches_python()
    {
        var tc = Inputs.GetProperty("tc");
        await _client.CreateTestCaseAsync(tc.GetProperty("title").GetString()!, tc.GetProperty("preconditions").GetString()!, Steps());
        AssertJsonEqual(_root.GetProperty("tcPatch"), _handler.LastContent);
        Assert.Equal("application/json-patch+json", _handler.LastContentType);
    }

    [Fact]
    public async Task CreateBug_patch_matches_python()
    {
        var b = Inputs.GetProperty("bug");
        var repro = b.GetProperty("reproSteps").EnumerateArray().Select(x => x.GetString()!).ToList();
        await _client.CreateBugAsync(
            b.GetProperty("title").GetString()!, b.GetProperty("severity").GetString()!,
            b.GetProperty("preconditions").GetString()!, repro,
            b.GetProperty("expected").GetString()!, b.GetProperty("actual").GetString()!,
            b.GetProperty("suggestedFix").GetString()!);
        AssertJsonEqual(_root.GetProperty("bugPatch"), _handler.LastContent);
        Assert.Equal("application/json-patch+json", _handler.LastContentType);
    }

    [Fact]
    public async Task AddTestCaseToSuite_body_matches_python()
    {
        var s = Inputs.GetProperty("suite");
        await _client.AddTestCaseToSuiteAsync(s.GetProperty("planId").GetInt32(), s.GetProperty("suiteId").GetInt32(), s.GetProperty("testCaseId").GetInt32());
        AssertJsonEqual(_root.GetProperty("suiteBody"), _handler.LastContent);
        Assert.Equal("application/json", _handler.LastContentType);
    }

    [Fact]
    public async Task LinkTestedBy_body_matches_python()
    {
        var t = Inputs.GetProperty("testedBy");
        await _client.LinkTestedByAsync(t.GetProperty("storyId").GetInt32(), t.GetProperty("testCaseId").GetInt32());
        AssertJsonEqual(_root.GetProperty("testedByBody"), _handler.LastContent);
        Assert.Equal("PATCH", _handler.LastMethod);
    }

    [Fact]
    public async Task LinkRelated_body_matches_python()
    {
        var r = Inputs.GetProperty("related");
        await _client.LinkRelatedAsync(r.GetProperty("fromId").GetInt32(), r.GetProperty("toId").GetInt32());
        AssertJsonEqual(_root.GetProperty("relatedBody"), _handler.LastContent);
    }

    [Fact]
    public void StepsXml_matches_python()
        => Assert.Equal(_root.GetProperty("stepsXml").GetString(), AzureDevOpsClient.BuildStepsXml(Steps()));

    [Fact]
    public async Task SearchWorkItems_overlong_numeric_returns_empty_without_crashing()
    {
        // Audit finding: int.Parse would throw OverflowException; the fix yields [] like Python's 404 path.
        var result = await _client.SearchWorkItemsAsync("123456789012345678901234567890");
        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateBug_unknown_severity_defaults_to_medium()
    {
        await _client.CreateBugAsync("t", "Bogus", "", ["step"], "", "", "");
        using var doc = JsonDocument.Parse(_handler.LastContent!);
        var sev = doc.RootElement.EnumerateArray()
            .First(o => o.GetProperty("path").GetString() == "/fields/Microsoft.VSTS.Common.Severity");
        Assert.Equal("3 - Medium", sev.GetProperty("value").GetString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private JsonElement Inputs => _root.GetProperty("inputs");

    private List<TestCaseStep> Steps() => Inputs.GetProperty("steps").EnumerateArray()
        .Select(s => new TestCaseStep { Action = s.GetProperty("action").GetString()!, Expected = s.GetProperty("expected").GetString()! })
        .ToList();

    private static void AssertJsonEqual(JsonElement expected, string? actual)
    {
        Assert.NotNull(actual);
        using var doc = JsonDocument.Parse(actual!);
        Assert.Equal(Canon(expected), Canon(doc.RootElement));
    }

    private static string Canon(JsonElement e) => JsonSerializer.Serialize(e);

    public void Dispose() => _golden.Dispose();
}
