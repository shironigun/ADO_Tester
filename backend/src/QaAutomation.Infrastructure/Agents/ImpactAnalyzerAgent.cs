using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Compact inventory projection sent to the impact analyzer (no SpecCode).</summary>
public sealed record DraftInventoryItem(int Id, string Module, string Level, string Title, List<string> SourceRoutes, List<string> Tags);

/// <summary>
/// Reads changed files + commit messages + a compact ApiTestDraft inventory, then returns
/// which draft ids are impacted. Throws AgentException on malformed JSON so the watcher
/// can apply its retry/fallback logic.
/// </summary>
public sealed class ImpactAnalyzerAgent
{
    private readonly ILlmClient _llm;

    public ImpactAnalyzerAgent(ILlmClient llm) => _llm = llm;

    public async Task<ImpactAnalysis> AnalyzeAsync(
        IReadOnlyList<string> changedFiles, IReadOnlyList<string> commitMessages,
        IReadOnlyList<DraftInventoryItem> testInventory,
        string? kbContext = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(changedFiles, commitMessages, testInventory, kbContext);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ImpactAnalyzerSystem, userPrompt, '{', progress, emit,
            label: "ImpactAnalyzer", cancellationToken: ct);

        if (data is not JsonObject root)
            throw new AgentException("Impact analyzer did not return a JSON object.");

        var impactedIds = new List<int>();
        if (root["impactedDraftIds"] is JsonArray idArr)
            foreach (var item in idArr)
                if (item?.GetValueKind() == JsonValueKind.Number && item.AsValue().TryGetValue(out int id))
                    impactedIds.Add(id);

        // Filter to only ids that exist in the inventory
        var validIds = testInventory.Select(d => d.Id).ToHashSet();
        impactedIds = impactedIds.Where(validIds.Contains).Distinct().ToList();

        return new ImpactAnalysis
        {
            ImpactedDraftIds = impactedIds,
            Reasoning = JsonUtils.StrOr(root, "reasoning"),
            Confidence = JsonUtils.StrOr(root, "confidence") is var c && c.Length > 0 ? c : "low",
            SkippedReason = root["skippedReason"]?.GetValue<string>(),
            Fallback = false,
        };
    }

    private static string BuildPrompt(
        IReadOnlyList<string> changedFiles, IReadOnlyList<string> commitMessages,
        IReadOnlyList<DraftInventoryItem> inventory, string? kbContext)
    {
        var sb = new StringBuilder();
        sb.Append("## Changed files\n");
        foreach (var f in changedFiles) sb.Append($"- {f}\n");
        sb.Append("\n## Commit messages\n");
        foreach (var m in commitMessages) sb.Append($"- {m}\n");
        sb.Append("\n## Test inventory (id, module, level, title, sourceRoutes, tags)\n");
        foreach (var d in inventory)
        {
            sb.Append($"- id={d.Id} module={d.Module} level={d.Level} title={d.Title}");
            if (d.SourceRoutes.Count > 0) sb.Append($" routes=[{string.Join(", ", d.SourceRoutes)}]");
            if (d.Tags.Count > 0) sb.Append($" tags=[{string.Join(", ", d.Tags)}]");
            sb.Append('\n');
        }
        if (!string.IsNullOrEmpty(kbContext))
        {
            sb.Append("\n## KB context\n");
            sb.Append(kbContext);
        }
        sb.Append("\nOutput the impact analysis JSON now.");
        return sb.ToString();
    }
}
