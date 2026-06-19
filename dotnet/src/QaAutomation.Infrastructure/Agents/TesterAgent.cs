using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Tester agent — generates the house-style test-case array (spec §5.7). Opener is '['; the
/// step shape is {action, expected}; preconditions are always "".</summary>
public sealed class TesterAgent
{
    private readonly ILlmClient _llm;

    public TesterAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<GeneratedTestCase>> GenerateAsync(
        AnalystOutput analysis, AdoWorkItem workItem,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildUserPrompt(analysis, workItem);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.TesterSystem, prompt, '[', progress, emit, label: "Tester", cancellationToken: cancellationToken);
        if (data is not JsonArray arr)
            throw new AgentException("Tester returned non-array JSON.");

        var outList = new List<GeneratedTestCase>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;
            var steps = new List<TestCaseStep>();
            if ((o["steps"] ?? o["Steps"]) is JsonArray stepsArr)
                foreach (var s in stepsArr)
                    if (s is JsonObject so)
                        steps.Add(new TestCaseStep
                        {
                            Action = JsonUtils.StrOr(so, "action", "Action"),
                            Expected = JsonUtils.StrOr(so, "expected", "Expected"),
                        });
            outList.Add(new GeneratedTestCase
            {
                Title = JsonUtils.StrOr(o, "title", "Title"),
                Preconditions = JsonUtils.StrOr(o, "preconditions", "Preconditions"),
                Steps = steps,
            });
        }
        return outList;
    }

    internal static string BuildUserPrompt(AnalystOutput a, AdoWorkItem wi)
    {
        var lines = new List<string>
        {
            $"Work item #{wi.Id}: {wi.Title}",
            "",
            $"Module: {a.Module}",
            $"Feature: {a.Feature}",
            "",
            $"Summary: {a.Summary}",
            "",
            $"Happy path: {a.HappyPath}",
        };

        void AppendList(string label, List<string> items)
        {
            if (items.Count == 0)
                return;
            lines.Add("");
            lines.Add($"{label}:");
            foreach (var i in items)
                lines.Add($"- {i}");
        }

        AppendList("Acceptance criteria", a.AcceptanceCriteria);
        AppendList("Business rules", a.BusinessRules);
        AppendList("Edge cases", a.EdgeCases);
        AppendList("Negative cases", a.NegativeCases);
        AppendList("Permissions", a.Permissions);
        AppendList("Cross-flow", a.CrossFlow);

        if (a.OpenQuestions.Count > 0)
        {
            lines.Add("");
            lines.Add("Open questions — when the QA has supplied an answer, treat it as authoritative; " +
                      "otherwise use your best judgment:");
            for (var i = 0; i < a.OpenQuestions.Count; i++)
            {
                var ans = i < a.QuestionAnswers.Count ? a.QuestionAnswers[i].Trim() : "";
                if (ans.Length > 0)
                {
                    lines.Add($"- Q: {a.OpenQuestions[i]}");
                    lines.Add($"  A: {ans}");
                }
                else
                {
                    lines.Add($"- Q (unanswered): {a.OpenQuestions[i]}");
                }
            }
        }

        lines.Add("");
        lines.Add("Generate the full test case array now.");
        return string.Join("\n", lines);
    }
}
