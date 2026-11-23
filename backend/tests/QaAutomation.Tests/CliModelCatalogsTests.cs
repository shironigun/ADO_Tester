using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Tests;

public sealed class CliModelCatalogsTests
{
    [Fact]
    public void ParseCursorListModels_features_haiku_composer_grok_and_dedupes()
    {
        const string sample = """
            Available models:

            auto - Auto (default)
            composer-2.5 - Composer 2.5
            composer-2.5-fast - Composer 2.5 Fast
            cursor-grok-4.5-low - Cursor Grok 4.5 Low
            cursor-grok-4.5-medium - Cursor Grok 4.5 Medium
            cursor-grok-4.5-high - Cursor Grok 4.5
            gpt-5.4 - GPT-5.4
            """;

        var models = CliModelCatalogs.ParseCursorListModels(sample);

        Assert.Equal("haiku", models[0].Id);
        Assert.Equal("composer-2.5", models[1].Id);
        Assert.Equal("cursor-grok-4.5", models[2].Id);
        Assert.Contains(models[0].Efforts, e => e.Value == "high");
        Assert.Contains(models[0].Contexts, c => c.Value == "200k");
        Assert.Contains(models[0].Contexts, c => c.Value == "1m");
        Assert.Contains(models[2].Efforts, e => e.Value == "low");
        Assert.Contains(models[2].Efforts, e => e.Value == "medium");
        Assert.Contains(models[2].Efforts, e => e.Value == "high");
        Assert.DoesNotContain(models, m => m.Id.StartsWith("cursor-grok-4.5-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(models, m => m.Id.Equals("composer-2.5-fast", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(models, m => m.Id == "auto");
        Assert.Contains(models, m => m.Id == "gpt-5.4");
    }

    [Fact]
    public void ParseCursorListModels_empty_stdout_still_returns_featured()
    {
        var models = CliModelCatalogs.ParseCursorListModels("");
        Assert.Contains(models, m => m.Id == "haiku");
        Assert.Contains(models, m => m.Id == "composer-2.5");
        Assert.Contains(models, m => m.Id == "cursor-grok-4.5");
        Assert.Contains(models, m => m.Id == "auto");
    }

    [Theory]
    [InlineData("haiku", null, null, "haiku")]
    [InlineData("haiku", "low", "200k", "haiku[context=200k,effort=low]")]
    [InlineData("composer-2.5", "high", null, "composer-2.5[effort=high]")]
    [InlineData("composer", "medium", "1m", "composer-2.5[context=1m,effort=medium]")]
    [InlineData("cursor-grok-4.5", "high", "1m", "cursor-grok-4.5[context=1m,effort=high]")]
    [InlineData("cursor-grok-4.5-medium", null, null, "cursor-grok-4.5[effort=medium]")]
    [InlineData("grok-4.5", "low", null, "cursor-grok-4.5[effort=low]")]
    [InlineData("auto", "medium", "1m", "auto[context=1m,effort=medium]")]
    [InlineData("auto[effort=low]", "high", "1m", "auto[effort=low]")]
    public void ComposeCursorModel_builds_bracket_overrides(
        string model, string? effort, string? context, string expected)
        => Assert.Equal(expected, CliModelCatalogs.ComposeCursorModel(model, effort, context));

    [Fact]
    public void ClaudeContextLabel_maps_aliases()
    {
        Assert.Equal("200k", CliModelCatalogs.ClaudeContextLabel("haiku"));
        Assert.Equal("1M", CliModelCatalogs.ClaudeContextLabel("opus"));
    }

    [Fact]
    public void CopilotContextLabel_maps_tiers()
    {
        Assert.Equal("200k", CliModelCatalogs.CopilotContextLabel("default"));
        Assert.Equal("1M", CliModelCatalogs.CopilotContextLabel("long_context"));
    }
}
