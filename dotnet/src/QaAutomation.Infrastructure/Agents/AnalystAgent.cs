using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Analyst agent — produces the structured expected-behavior analysis (spec §5.7). Prompt is
/// the frozen embedded resource; output is the dual-case-tolerant <see cref="AnalystOutput"/>.</summary>
public sealed class AnalystAgent
{
    private readonly ILlmClient _llm;

    public AnalystAgent(ILlmClient llm) => _llm = llm;

    public async Task<AnalystOutput> AnalyzeAsync(
        AdoWorkItem workItem, IReadOnlyList<KnowledgebaseHit> context,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildUserPrompt(workItem, context);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.AnalystSystem, prompt, '{', progress, emit, label: "Analyst", cancellationToken: cancellationToken);
        if (data is not JsonObject)
            throw new AgentException("Analyst returned non-object JSON.");
        return AnalystOutput.FromJson(data);
    }

    internal static string BuildUserPrompt(AdoWorkItem wi, IReadOnlyList<KnowledgebaseHit> context)
    {
        var lines = new List<string>
        {
            "## Work Item",
            $"**ID:** {wi.Id}",
            $"**Type:** {wi.WorkItemType}",
            $"**Title:** {wi.Title}",
        };
        if (!string.IsNullOrWhiteSpace(wi.Description))
        {
            lines.Add("**Description:**");
            lines.Add(wi.Description!);
        }
        if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
        {
            lines.Add("**Acceptance Criteria:**");
            lines.Add(wi.AcceptanceCriteria!);
        }
        if (context.Count > 0)
        {
            lines.Add("");
            lines.Add("## Domain Knowledge (relevant chunks)");
            foreach (var hit in context)
            {
                lines.Add($"### From: {hit.FileName}");
                lines.Add(hit.Content);
            }
        }
        return string.Join("\n", lines);
    }
}
