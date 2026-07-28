using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Tester agent — generates the house-style test-case array (spec §5.7). Opener is '['; the
/// step shape is {action, expected}; preconditions are always "".
/// <para>Optional labeled user-prompt sections steer it without touching the frozen system prompt:
/// <c>testerContext</c> (free-text from the tester) and <c>existingTestsBlock</c> (existing ADO cases to
/// avoid duplicating). <see cref="RefineAsync"/> runs a feedback-driven pass that returns only the
/// changed/new cases (each tagged with a <c>ref</c>) for the orchestrator to merge.</para></summary>
public sealed class TesterAgent
{
    private readonly ILlmClient _llm;

    public TesterAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<GeneratedTestCase>> GenerateAsync(
        AnalystOutput analysis, AdoWorkItem workItem,
        string? testerContext = null, string? existingTestsBlock = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildUserPrompt(analysis, workItem, testerContext, existingTestsBlock);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.TesterSystem, prompt, '[', progress, emit, label: "Tester", cancellationToken: cancellationToken);
        if (data is not JsonArray arr)
            throw new AgentException("Tester returned non-array JSON.");

        var outList = new List<GeneratedTestCase>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;
            outList.Add(new GeneratedTestCase
            {
                Title = JsonUtils.StrOr(o, "title", "Title"),
                Preconditions = JsonUtils.StrOr(o, "preconditions", "Preconditions"),
                Steps = ParseSteps(o),
            });
        }
        return outList;
    }

    /// <summary>Feedback-driven refine. Returns ONLY the cases to change (each carrying its existing
    /// <c>ref</c>) and brand-new cases (<c>ref = "new"</c>); unchanged and locked cases are omitted and
    /// preserved by the orchestrator.</summary>
    public async Task<List<RefinedTestCase>> RefineAsync(
        AnalystOutput analysis, AdoWorkItem workItem,
        IReadOnlyList<ExistingDraftForRefine> existing, IReadOnlyList<string> removedTitles,
        string? overallFeedback, string? testerContext, string? existingTestsBlock,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildRefinePrompt(analysis, workItem, existing, removedTitles, overallFeedback, testerContext, existingTestsBlock);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.TesterSystem, prompt, '[', progress, emit, label: "Tester (refine)", cancellationToken: cancellationToken);
        if (data is not JsonArray arr)
            throw new AgentException("Tester returned non-array JSON.");

        var outList = new List<RefinedTestCase>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;
            outList.Add(new RefinedTestCase
            {
                Ref = ReadRef(o),
                Title = JsonUtils.StrOr(o, "title", "Title"),
                Preconditions = JsonUtils.StrOr(o, "preconditions", "Preconditions"),
                Steps = ParseSteps(o),
            });
        }
        return outList;
    }

    private static List<TestCaseStep> ParseSteps(JsonObject o)
    {
        var steps = new List<TestCaseStep>();
        if ((o["steps"] ?? o["Steps"]) is JsonArray stepsArr)
            foreach (var s in stepsArr)
                if (s is JsonObject so)
                    steps.Add(new TestCaseStep
                    {
                        Action = JsonUtils.StrOr(so, "action", "Action"),
                        Expected = JsonUtils.StrOr(so, "expected", "Expected"),
                    });
        return steps;
    }

    private static string? ReadRef(JsonObject o)
    {
        var node = o["ref"] ?? o["Ref"] ?? o["id"] ?? o["Id"];
        if (node is null) return null;
        var kind = node.GetValueKind();
        return kind == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString().Trim('"');
    }

    internal static string BuildUserPrompt(
        AnalystOutput a, AdoWorkItem wi, string? testerContext = null, string? existingTestsBlock = null)
    {
        var lines = BaseAnalysisLines(a, wi);
        AppendExtraSections(lines, testerContext, existingTestsBlock);
        lines.Add("");
        lines.Add("Generate the full test case array now.");
        return string.Join("\n", lines);
    }

    private static string BuildRefinePrompt(
        AnalystOutput a, AdoWorkItem wi,
        IReadOnlyList<ExistingDraftForRefine> existing, IReadOnlyList<string> removedTitles,
        string? overallFeedback, string? testerContext, string? existingTestsBlock)
    {
        var lines = BaseAnalysisLines(a, wi);

        var locked = existing.Where(e => e.Locked).ToList();
        var editable = existing.Where(e => !e.Locked).ToList();

        if (editable.Count > 0)
        {
            lines.Add("");
            lines.Add("## Existing generated test cases (revise these — each shows its `ref`)");
            foreach (var e in editable)
            {
                lines.Add($"- ref: {e.DraftId} · status: {e.Status}");
                lines.Add($"  Title: {e.Title}");
                foreach (var st in e.Steps)
                    lines.Add($"    • {st.Action} → {st.Expected}");
                if (!string.IsNullOrWhiteSpace(e.Feedback))
                    lines.Add($"  Feedback (apply): {e.Feedback!.Trim()}");
            }
        }

        if (locked.Count > 0)
        {
            lines.Add("");
            lines.Add("## Locked test cases (already pushed to ADO — do NOT reproduce or modify)");
            foreach (var e in locked)
                lines.Add($"- {e.Title}");
        }

        if (removedTitles.Count > 0)
        {
            lines.Add("");
            lines.Add("## Removed by the tester (do NOT re-add these)");
            foreach (var t in removedTitles)
                lines.Add($"- {t}");
        }

        if (!string.IsNullOrWhiteSpace(overallFeedback))
        {
            lines.Add("");
            lines.Add("## Overall feedback (authoritative)");
            lines.Add(overallFeedback!.Trim());
        }

        AppendExtraSections(lines, testerContext, existingTestsBlock);

        lines.Add("");
        lines.Add("Return a JSON array. Include ONLY: (a) each existing test you are CHANGING, carrying " +
                  "its `ref` (the numeric id shown) plus the full revised title/steps; and (b) any NEW " +
                  "test, with `ref` set to \"new\". Do NOT include tests you are leaving unchanged, and " +
                  "never include the locked or removed tests. Keep the house style and step shape.");
        return string.Join("\n", lines);
    }

    private static List<string> BaseAnalysisLines(AnalystOutput a, AdoWorkItem wi)
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

        return lines;
    }

    private static void AppendExtraSections(List<string> lines, string? testerContext, string? existingTestsBlock)
    {
        if (!string.IsNullOrWhiteSpace(testerContext))
        {
            lines.Add("");
            lines.Add("## Extra Context (from tester)");
            lines.Add(testerContext!.Trim());
        }
        if (!string.IsNullOrWhiteSpace(existingTestsBlock))
        {
            lines.Add("");
            lines.Add("## Existing Test Cases in ADO (do NOT duplicate — produce only net-new/complementary coverage; " +
                      "you may cite an existing TC id where coverage exists)");
            lines.Add(existingTestsBlock!.Trim());
        }
    }
}
