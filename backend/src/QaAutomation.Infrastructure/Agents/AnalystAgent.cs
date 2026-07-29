using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Analyst agent — produces the structured expected-behavior analysis (spec §5.7). Prompt is
/// the frozen embedded resource; output is the dual-case-tolerant <see cref="AnalystOutput"/>.
/// <para>Two optional free-text inputs steer it without touching the frozen system prompt: the tester's
/// <c>additionalContext</c> (routes/screens/notes not in the story) and discovered repo <c>codeContext</c>
/// — both appended as labeled user-prompt sections. <see cref="RefineAsync"/> re-runs it against its own
/// prior output plus reviewer feedback to fill gaps instead of starting from scratch.</para></summary>
public sealed class AnalystAgent
{
    private readonly ILlmClient _llm;

    public AnalystAgent(ILlmClient llm) => _llm = llm;

    public async Task<AnalystOutput> AnalyzeAsync(
        AdoWorkItem workItem, IReadOnlyList<KnowledgebaseHit> context,
        string? additionalContext = null, string? codeContext = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildUserPrompt(workItem, context, additionalContext, codeContext);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.AnalystSystem, prompt, '{', progress, emit, label: "Analyst", cancellationToken: cancellationToken);
        if (data is not JsonObject)
            throw new AgentException("Analyst returned non-object JSON.");
        return AnalystOutput.FromJson(data);
    }

    /// <summary>Refine an existing analysis with reviewer feedback — improve and fill gaps (e.g. missing
    /// edge cases / permissions) while keeping the same output shape. Same frozen system prompt.</summary>
    public async Task<AnalystOutput> RefineAsync(
        AnalystOutput prior, string feedback, AdoWorkItem workItem, IReadOnlyList<KnowledgebaseHit> context,
        string? additionalContext = null, string? codeContext = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildRefinePrompt(prior, feedback, workItem, context, additionalContext, codeContext);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.AnalystSystem, prompt, '{', progress, emit, label: "Analyst (refine)", cancellationToken: cancellationToken);
        if (data is not JsonObject)
            throw new AgentException("Analyst returned non-object JSON.");
        return AnalystOutput.FromJson(data);
    }

    internal static string BuildUserPrompt(
        AdoWorkItem wi, IReadOnlyList<KnowledgebaseHit> context,
        string? additionalContext = null, string? codeContext = null)
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
        AppendContextSections(lines, context, additionalContext, codeContext);
        return string.Join("\n", lines);
    }

    private static string BuildRefinePrompt(
        AnalystOutput prior, string feedback, AdoWorkItem wi, IReadOnlyList<KnowledgebaseHit> context,
        string? additionalContext, string? codeContext)
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

        lines.Add("");
        lines.Add("## Prior Analysis (yours — improve it, do NOT start over)");
        lines.Add(prior.ToJson());

        lines.Add("");
        lines.Add("## Reviewer Feedback (authoritative — apply it)");
        lines.Add((feedback ?? "").Trim());
        lines.Add("");
        lines.Add("Return the FULL improved analysis as a single JSON object in the same shape as before. " +
                  "Keep everything already correct, and specifically fill any gaps the feedback calls out " +
                  "(e.g. add missing edge cases, permissions, negative cases, or business rules).");

        AppendContextSections(lines, context, additionalContext, codeContext);
        return string.Join("\n", lines);
    }

    private static void AppendContextSections(
        List<string> lines, IReadOnlyList<KnowledgebaseHit> context, string? additionalContext, string? codeContext)
    {
        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            lines.Add("");
            lines.Add("## Additional Context (from tester)");
            lines.Add(additionalContext!.Trim());
        }
        if (!string.IsNullOrWhiteSpace(codeContext))
        {
            lines.Add("");
            lines.Add("## Code Context (snippets from the repo)");
            lines.Add(codeContext!.Trim());
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
    }
}
