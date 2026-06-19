using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Bug Writer agent — turns Analyst expected-behavior + observed actual into house-style bug
/// reports (spec §5.7). Opener is '['; reproSteps are plain strings; preconditions always "".</summary>
public sealed partial class BugWriterAgent
{
    private readonly ILlmClient _llm;

    public BugWriterAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<GeneratedBug>> GenerateAsync(
        AnalystOutput analysis, AdoWorkItem workItem, string actualBehavior, string? codeContext,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildUserPrompt(analysis, workItem, actualBehavior, codeContext);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.BugWriterSystem, prompt, '[', progress, emit, label: "Bug Writer", cancellationToken: cancellationToken);
        if (data is not JsonArray arr)
            throw new AgentException("Bug Writer returned non-array JSON.");

        var outList = new List<GeneratedBug>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;

            var sev = JsonUtils.StrOr(o, "severity");
            if (string.IsNullOrEmpty(sev))
                sev = "Medium";
            sev = sev.Trim();
            if (sev is not ("Critical" or "High" or "Medium" or "Low"))
            {
                var low = sev.ToLowerInvariant();
                sev = low.Contains("crit") ? "Critical"
                    : low.Contains("high") ? "High"
                    : low.Contains("low") ? "Low"
                    : "Medium";
            }

            outList.Add(new GeneratedBug
            {
                Title = JsonUtils.StrOr(o, "title"),
                Severity = sev,
                Preconditions = JsonUtils.StrOr(o, "preconditions"),
                ReproSteps = JsonUtils.CoerceStringList(o["reproSteps"]),
                Expected = JsonUtils.StrOr(o, "expected"),
                Actual = JsonUtils.StrOr(o, "actual"),
                SuggestedFix = JsonUtils.StrOr(o, "suggestedFix"),
            });
        }
        return outList;
    }

    internal static string BuildUserPrompt(AnalystOutput a, AdoWorkItem wi, string actual, string? code)
    {
        var lines = new List<string> { $"# Ticket #{wi.Id} — {wi.Title}" };
        if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
        {
            lines.Add("## Acceptance Criteria (from ticket)");
            lines.Add(StripHtml(wi.AcceptanceCriteria!));
        }

        lines.Add("## Analyst — Expected Behavior");
        if (a.Module.Length > 0) lines.Add($"Module:  {a.Module}");
        if (a.Feature.Length > 0) lines.Add($"Feature: {a.Feature}");
        if (a.Summary.Length > 0) lines.Add($"Summary: {a.Summary}");
        if (a.HappyPath.Length > 0) lines.Add($"Happy path: {a.HappyPath}");

        void AppendList(string label, List<string> items)
        {
            if (items.Count == 0)
                return;
            lines.Add($"{label}:");
            foreach (var i in items)
                lines.Add($"- {i}");
        }

        AppendList("Acceptance criteria", a.AcceptanceCriteria);
        AppendList("Business rules", a.BusinessRules);
        AppendList("Edge cases", a.EdgeCases);
        AppendList("Negative cases", a.NegativeCases);
        AppendList("Permissions", a.Permissions);
        AppendList("Cross-flow impacts", a.CrossFlow);

        var answered = new List<(string Q, string A)>();
        for (var i = 0; i < a.OpenQuestions.Count; i++)
        {
            var ans = i < a.QuestionAnswers.Count ? a.QuestionAnswers[i].Trim() : "";
            if (ans.Length > 0)
                answered.Add((a.OpenQuestions[i], ans));
        }
        if (answered.Count > 0)
        {
            lines.Add("");
            lines.Add("## Clarifications (QA answers to the Analyst's open questions — treat as authoritative)");
            foreach (var (q, ans) in answered)
            {
                lines.Add($"- Q: {q}");
                lines.Add($"  A: {ans}");
            }
        }

        lines.Add("");
        lines.Add("## Observed Actual Behavior");
        lines.Add((actual ?? "").Trim());

        if (!string.IsNullOrWhiteSpace(code))
        {
            lines.Add("");
            lines.Add("## Code Context (snippets from the repo)");
            lines.Add(code!.Trim());
        }

        lines.Add("");
        lines.Add("## Task");
        lines.Add("Identify each discrepancy between Expected and Observed Actual. For each,");
        lines.Add("draft ONE bug in the required JSON shape. Do not invent bugs that aren't");
        lines.Add("supported by the observed actual. Return ONLY the JSON array.");
        return string.Join("\n", lines);
    }

    private static string StripHtml(string html) => HtmlTagRegex().Replace(html, " ").Trim();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
