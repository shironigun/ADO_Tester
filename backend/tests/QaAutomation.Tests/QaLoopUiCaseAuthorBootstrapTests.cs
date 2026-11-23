using System.Text.Json;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Tests;

public sealed class QaLoopUiCaseAuthorBootstrapTests
{
    [Fact]
    public void EnsureBootstrapSteps_prepends_login_popup_and_module_nav()
    {
        var raw = """[{"action":"Click Equipment Locator button","expected":"Drawer opens"}]""";
        var json = QaLoopUiCaseAuthorAgent.EnsureBootstrapSteps("Inventory", raw);
        using var doc = JsonDocument.Parse(json);
        var steps = doc.RootElement.EnumerateArray().ToList();
        Assert.True(steps.Count >= 4);
        Assert.Contains("Login to", steps[0].GetProperty("action").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email registration", steps[1].GetProperty("action").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inventory", steps[2].GetProperty("action").GetString()!);
        Assert.Contains("side menu", steps[2].GetProperty("action").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Click Equipment Locator button", steps[3].GetProperty("action").GetString());
    }

    [Fact]
    public void EnsureBootstrapSteps_dedupes_existing_bootstrap_prefix()
    {
        var raw = """
        [
          {"action":"Login to the application","expected":"ok"},
          {"action":"Close the email registration popup if it appears","expected":"ok"},
          {"action":"Click on Inventory module in the side menu","expected":"ok"},
          {"action":"Click Equipment Locator button","expected":"Drawer opens"}
        ]
        """;
        var json = QaLoopUiCaseAuthorAgent.EnsureBootstrapSteps("Inventory", raw);
        using var doc = JsonDocument.Parse(json);
        var steps = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(4, steps.Count);
        Assert.Equal("Click Equipment Locator button", steps[3].GetProperty("action").GetString());
    }

    [Fact]
    public void Prompt_requires_end_to_end_bootstrap()
    {
        Assert.Contains("EVERY CASE IS END-TO-END", AgentPrompts.QaLoopUiCaseAuthorSystem);
        Assert.Contains("CHILDLIKE GRANULARITY", AgentPrompts.QaLoopUiCaseAuthorSystem);
        Assert.Contains("Close the email registration popup if it appears", AgentPrompts.QaLoopUiCaseAuthorSystem);
        Assert.Contains("Click on <Module> module in the side menu", AgentPrompts.QaLoopUiCaseAuthorSystem);
    }
}
