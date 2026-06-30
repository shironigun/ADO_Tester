using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Tests;

/// <summary>Unit tests for ImpactAnalyzerAgent JSON contract (QAL-052).</summary>
public sealed class ImpactAnalyzerAgentTests
{
    private static readonly IReadOnlyList<string> NoFiles = [];
    private static readonly IReadOnlyList<string> NoCommits = [];

    [Fact]
    public async Task Valid_json_parses_impactedDraftIds_reasoning_confidence()
    {
        const string json = """
            {"impactedDraftIds":[10,20],"reasoning":"both routes changed","confidence":"high","skippedReason":null}
            """;
        var inventory = new[]
        {
            new DraftInventoryItem(10, "Deals", "contract", "TC 10", [], []),
            new DraftInventoryItem(20, "Deals", "endpoint", "TC 20", [], []),
        };

        var agent = new ImpactAnalyzerAgent(new StubLlmClient(json));
        var result = await agent.AnalyzeAsync(NoFiles, NoCommits, inventory);

        Assert.Equal([10, 20], result.ImpactedDraftIds);
        Assert.Equal("both routes changed", result.Reasoning);
        Assert.Equal("high", result.Confidence);
        Assert.False(result.Fallback);
    }

    [Fact]
    public async Task Ids_not_in_inventory_are_filtered_out()
    {
        const string json = """
            {"impactedDraftIds":[10,99],"reasoning":"r","confidence":"medium"}
            """;
        // Only id 10 is in the inventory — 99 should be dropped
        var inventory = new[] { new DraftInventoryItem(10, "Deals", "contract", "TC 10", [], []) };

        var agent = new ImpactAnalyzerAgent(new StubLlmClient(json));
        var result = await agent.AnalyzeAsync(NoFiles, NoCommits, inventory);

        Assert.Equal([10], result.ImpactedDraftIds);
    }

    [Fact]
    public async Task Empty_ids_array_still_parses_skippedReason()
    {
        const string json = """
            {"impactedDraftIds":[],"reasoning":"no relevant changes","confidence":"low","skippedReason":"No routes match changed files"}
            """;
        var agent = new ImpactAnalyzerAgent(new StubLlmClient(json));
        var result = await agent.AnalyzeAsync(NoFiles, NoCommits, []);

        Assert.Empty(result.ImpactedDraftIds);
        Assert.Equal("No routes match changed files", result.SkippedReason);
    }

    [Fact]
    public async Task Malformed_json_throws_AgentException()
    {
        var agent = new ImpactAnalyzerAgent(new StubLlmClient("not valid json at all"));
        await Assert.ThrowsAsync<AgentException>(() =>
            agent.AnalyzeAsync(NoFiles, NoCommits, []));
    }

    [Fact]
    public async Task Json_array_root_throws_AgentException()
    {
        // Agent expects an object, not an array
        var agent = new ImpactAnalyzerAgent(new StubLlmClient("[1,2,3]"));
        await Assert.ThrowsAsync<AgentException>(() =>
            agent.AnalyzeAsync(NoFiles, NoCommits, []));
    }

    [Fact]
    public void Inventory_projection_excludes_SpecCode()
    {
        // DraftInventoryItem is the projection sent to the agent.
        // Verifies SpecCode is structurally absent (no such property on the record).
        var item = new DraftInventoryItem(
            Id: 1, Module: "Deals", Level: "contract",
            Title: "TC 1", SourceRoutes: ["GET /deals"], Tags: ["@smoke"]);

        var prompt = BuildTestPrompt([item]);

        // SpecCode is not a field of DraftInventoryItem → it cannot appear in the prompt
        Assert.DoesNotContain("spec", prompt, StringComparison.OrdinalIgnoreCase);
        // But the id, module, level, title, routes, tags ARE present
        Assert.Contains("id=1", prompt);
        Assert.Contains("module=Deals", prompt);
        Assert.Contains("GET /deals", prompt);
        Assert.Contains("@smoke", prompt);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Calls BuildPrompt via the real agent with a capturing LLM so we can inspect the prompt.</summary>
    private static string BuildTestPrompt(IReadOnlyList<DraftInventoryItem> inventory)
    {
        string? captured = null;
        var client = new CapturingClient(resp: "{}", onPrompt: p => captured = p);
        var agent = new ImpactAnalyzerAgent(client);
        agent.AnalyzeAsync(NoFiles, NoCommits, inventory).GetAwaiter().GetResult();
        return captured ?? "";
    }

    private sealed class CapturingClient : ILlmClient
    {
        private readonly string _resp;
        private readonly Action<string> _onPrompt;
        public string ProviderName => "Capturing";

        public CapturingClient(string resp, Action<string> onPrompt) { _resp = resp; _onPrompt = onPrompt; }

        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string? model = null,
            StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
        {
            _onPrompt(userPrompt);
            return Task.FromResult(_resp);
        }
    }
}
