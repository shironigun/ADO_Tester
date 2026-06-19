using System.Text.Json;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Reads a work item + KB context chunks and produces a structured AnalystOutput.
/// Uses a single LLM completion (no tool use needed — structured JSON output).
/// </summary>
public sealed class AnalystAgent
{
    private const string SystemPrompt =
        """
        You are the Analyst agent for a QA automation system.

        Given an Azure DevOps work item and relevant domain knowledge chunks, produce a structured
        expected-behavior analysis that the Tester agent will use to write test cases.

        Work item fields provided: Title, Description, Acceptance Criteria, Work Item Type, State.
        Domain knowledge: Markdown chunks from the project's knowledgebase (may be empty).

        Your output MUST be a valid JSON object — no markdown fences, no explanation, JSON only.
        Every list field MUST contain ONLY plain strings — never objects, never nested arrays.
        Schema:
        {
          "module":             "string — product module/area (e.g. 'Purchasing', 'Sales', 'Settings')",
          "feature":            "string — specific feature (e.g. 'Purchase Order Receiving Report')",
          "summary":            "string — one paragraph: what this story builds and why",
          "happyPath":          "string — the primary end-to-end success flow",
          "acceptanceCriteria": ["string — each AC as a clear, testable statement"],
          "businessRules":      ["string — stated and implied rules; each rule is one item"],
          "edgeCases":          ["string — boundary values, empty states, missing/deleted data"],
          "negativeCases":      ["string — invalid inputs, blocked actions, system enforcement"],
          "permissions":        ["string — role/permission considerations; empty array if none"],
          "crossFlow":          ["string — other modules/features this story touches; empty if none"],
          "openQuestions":      ["string — genuine ambiguities in the story; empty if none"]
        }
        """;

    private readonly ILlmClient _llm;

    public AnalystAgent(ILlmClient llm) => _llm = llm;

    public async Task<AnalystOutput> AnalyzeAsync(
        AdoWorkItem workItem,
        IReadOnlyList<KnowledgebaseHit> context,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(workItem, context);
        var json = await _llm.CompleteAsync(SystemPrompt, userPrompt, cancellationToken: ct);

        // Strip any accidental markdown fences if the model adds them
        var cleaned = json.Trim();
        if (cleaned.StartsWith("```"))
        {
            var s = cleaned.IndexOf('{');
            var e = cleaned.LastIndexOf('}') + 1;
            if (s >= 0 && e > s) cleaned = cleaned[s..e];
        }

        return JsonSerializer.Deserialize<AnalystOutput>(cleaned, JsonDefaults.Lenient)
            ?? throw new InvalidOperationException("Analyst returned invalid JSON.");
    }

    private static string BuildPrompt(AdoWorkItem wi, IReadOnlyList<KnowledgebaseHit> context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Work Item");
        sb.AppendLine($"**ID:** {wi.Id}");
        sb.AppendLine($"**Type:** {wi.WorkItemType}");
        sb.AppendLine($"**Title:** {wi.Title}");
        if (!string.IsNullOrWhiteSpace(wi.Description))
        {
            sb.AppendLine("**Description:**");
            sb.AppendLine(wi.Description);
        }
        if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
        {
            sb.AppendLine("**Acceptance Criteria:**");
            sb.AppendLine(wi.AcceptanceCriteria);
        }

        if (context.Count > 0)
        {
            sb.AppendLine("\n## Domain Knowledge (relevant chunks)");
            foreach (var hit in context)
            {
                sb.AppendLine($"### From: {hit.FileName}");
                sb.AppendLine(hit.Content);
            }
        }

        return sb.ToString();
    }
}
