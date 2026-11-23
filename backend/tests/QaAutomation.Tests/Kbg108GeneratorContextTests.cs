using System.Text.Json;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Orchestrators;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>KBG-108 — FRS-enriched generator extraContext + analyzer KB Context block.</summary>
public sealed class Kbg108GeneratorContextTests
{
    private static FrsModuleContext SampleFrs() => new(
        Slug: "messaging",
        ModuleMarkdown: "# Messaging module brief",
        AtlasMarkdown: "## Overview\n\n## Disambiguation traps\nTrap A\n\n## Other\nignore",
        FrontendPathIncludes: ["src/messaging/**"],
        BackendPathIncludes: ["api/Messaging/**"],
        KeywordsExclusive: ["thread"],
        RulesMarkdown: "Never send empty body.",
        FeBackLinksMarkdown: "POST /api/messages ↔ compose form",
        FeaturesJson: """[{"id":"msg-send","suggestedPack":"smoke"}]""");

    [Fact]
    public void BuildGeneratorExtraContext_null_when_no_generate_context_and_no_kb()
    {
        var session = new ApiTestSession { Module = "x" };
        Assert.Null(QaLoopOrchestrator.BuildGeneratorExtraContext(session));
    }

    [Fact]
    public void BuildGeneratorExtraContext_includes_rules_febe_features_and_tag_instructions()
    {
        var frs = SampleFrs();
        var session = new ApiTestSession
        {
            Module = "messaging",
            GenerateContext = "Prefer happy path first.",
            KbContextJson = JsonSerializer.Serialize(frs),
        };

        var extra = QaLoopOrchestrator.BuildGeneratorExtraContext(session);

        Assert.NotNull(extra);
        Assert.Contains("Prefer happy path first.", extra);
        Assert.Contains("## Domain Rules (FRS: messaging)", extra);
        Assert.Contains("Never send empty body.", extra);
        Assert.Contains("## FE-BE Contracts", extra);
        Assert.Contains("POST /api/messages ↔ compose form", extra);
        Assert.Contains("## Features and suggested packs", extra);
        Assert.Contains("msg-send", extra);
        Assert.Contains("@smoke/@regression/@e2e", extra);
        Assert.Contains("[Module] - [FeatureName]", extra);
    }

    [Fact]
    public void BuildInventoryPrompt_injects_kb_context_block_when_frs_present()
    {
        var prompt = ModuleAnalyzerAgent.BuildInventoryPrompt(
            "messaging", "code-here", [], frsContext: SampleFrs());

        Assert.Contains("## Knowledge Base Context (FRS: messaging)", prompt);
        Assert.Contains("# Messaging module brief", prompt);
        Assert.Contains("Never send empty body.", prompt);
        Assert.Contains("POST /api/messages ↔ compose form", prompt);
        Assert.Contains("Trap A", prompt);
        var kbIdx = prompt.IndexOf("## Knowledge Base Context", StringComparison.Ordinal);
        var srcIdx = prompt.IndexOf("## Module source code", StringComparison.Ordinal);
        Assert.True(kbIdx >= 0 && srcIdx > kbIdx);
        Assert.Contains("code-here", prompt);
    }
}
