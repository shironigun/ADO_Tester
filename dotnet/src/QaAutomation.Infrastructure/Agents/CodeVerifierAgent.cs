using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Code Verifier agent — STATIC review of the repo against test cases (spec §5.7). Pass-1 returns one
/// verdict per test case (single shot, raises on bad JSON). Pass-2 returns per-step verdicts for a
/// batch (shared code context), aligning by the model's 1-based case number then positionally, and
/// degrading to all-inconclusive rather than raising. Uses the LLM directly (no complete_json retry).
/// </summary>
public sealed class CodeVerifierAgent
{
    private readonly ILlmClient _llm;

    public CodeVerifierAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<CodeVerdict>> VerifyAsync(
        AdoWorkItem workItem, AnalystOutput analysis, IReadOnlyList<TestCaseInput> testCases,
        string codeContext, string? manualDescription = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(workItem, analysis, testCases, codeContext, manualDescription);
        var raw = await _llm.CompleteAsync(AgentPrompts.CodeVerifierSystem, prompt, null, progress, emit, cancellationToken);
        var cleaned = JsonUtils.StripFences(raw, '[');
        JsonNode? data;
        try { data = JsonNode.Parse(cleaned); }
        catch (JsonException e) { throw new AgentException($"Code Verifier returned invalid JSON: {e.Message}"); }
        if (data is not JsonArray arr)
            throw new AgentException("Code Verifier returned non-array JSON.");

        var outList = new List<CodeVerdict>();
        foreach (var x in arr)
            if (x is JsonObject o)
                outList.Add(ParseVerdict(o));
        return outList;
    }

    public async Task<List<List<StepVerdict>>> VerifyStepsAsync(
        AdoWorkItem workItem, AnalystOutput analysis, IReadOnlyList<TestCaseInput> cases,
        string codeContext, bool full,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        if (cases.Count == 0)
            return [];

        var system = AgentPrompts.CodeVerifierStepSystem.Replace(
            "{ACTUAL_RULE}", full ? AgentPrompts.ActualRuleFull : AgentPrompts.ActualRuleLite);
        var prompt = BuildStepsPrompt(workItem, analysis, cases, codeContext);
        var raw = await _llm.CompleteAsync(system, prompt, null, progress, emit, cancellationToken);

        // Key step-lists by the model's 1-based `case` number (when valid + in range); keep positional
        // order as a fallback if the numbering drifts.
        var byCase = new Dictionary<int, List<JsonObject>>();
        var positional = new List<List<JsonObject>>();
        try
        {
            if (JsonNode.Parse(JsonUtils.StripFences(raw, '[')) is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject o)
                        continue;
                    var steps = o["steps"] is JsonArray sa ? sa.OfType<JsonObject>().ToList() : [];
                    positional.Add(steps);
                    if (TryGetCaseNumber(o["case"], out var num) && num >= 1 && num <= cases.Count)
                        byCase[num] = steps;
                }
            }
        }
        catch (JsonException) { /* swallow — degrade to all-inconclusive */ }

        // No usable case numbers but the array lines up 1:1 → map by position.
        if (byCase.Count == 0 && positional.Count == cases.Count)
            for (var i = 0; i < cases.Count; i++)
                byCase[i + 1] = positional[i];

        var outList = new List<List<StepVerdict>>();
        for (var i = 0; i < cases.Count; i++)
        {
            byCase.TryGetValue(i + 1, out var results);
            results ??= [];
            var steps = cases[i].Steps;
            var verdicts = new List<StepVerdict>(steps.Count);
            for (var j = 0; j < steps.Count; j++)
                verdicts.Add(StepFromResult(steps[j], j < results.Count ? results[j] : null));
            outList.Add(verdicts);
        }
        return outList;
    }

    // ── parsing ──────────────────────────────────────────────────────────────

    private static CodeVerdict ParseVerdict(JsonObject d)
    {
        return new CodeVerdict
        {
            TestCase = JsonUtils.StrOr(d, "testCase", "test_case"),
            Status = NormalizeStatus(JsonUtils.StrOr(d, "status")),
            Evidence = JsonUtils.StrOr(d, "evidence"),
            Reasoning = JsonUtils.StrOr(d, "reasoning"),
        };
    }

    private static StepVerdict StepFromResult(TestCaseStep step, JsonObject? result) => new()
    {
        Action = step.Action,
        Expected = step.Expected,
        Status = NormalizeStatus(result is null ? "" : JsonUtils.StrOr(result, "status")),
        Actual = result is null ? "" : JsonUtils.StrOr(result, "actual"),
    };

    private static string NormalizeStatus(string raw)
    {
        var status = string.IsNullOrEmpty(raw) ? "inconclusive" : raw.Trim().ToLowerInvariant();
        return status is "pass" or "fail" or "inconclusive" ? status : "inconclusive";
    }

    private static bool TryGetCaseNumber(JsonNode? node, out int value)
    {
        value = 0;
        if (node is null)
            return false;
        switch (node.GetValueKind())
        {
            case JsonValueKind.Number:
                try { value = (int)node.GetValue<double>(); return true; }   // int(1.0) == 1 (truncates)
                catch { return false; }
            case JsonValueKind.String:
                return int.TryParse(node.GetValue<string>(), out value);     // "1.0" is rejected, like Python int()
            default:
                return false;
        }
    }

    // ── prompt builders ───────────────────────────────────────────────────────

    internal static string BuildPrompt(
        AdoWorkItem wi, AnalystOutput a, IReadOnlyList<TestCaseInput> testCases, string codeContext, string? manualDescription)
    {
        var lines = new List<string> { $"# Story #{wi.Id} — {wi.Title}" };
        if (a.Module.Length > 0 || a.Feature.Length > 0)
            lines.Add($"Module: {a.Module} · Feature: {a.Feature}");
        if (a.Summary.Length > 0)
            lines.Add($"Expected (summary): {a.Summary}");
        if (a.HappyPath.Length > 0)
            lines.Add($"Happy path: {a.HappyPath}");

        lines.Add("");
        lines.Add("## Test cases to verify");
        if (testCases.Count == 0)
            lines.Add("(No generated test cases found — verify the story's expected behavior instead.)");
        for (var i = 0; i < testCases.Count; i++)
        {
            var tc = testCases[i];
            lines.Add($"{i + 1}. {tc.Title}");
            if (tc.Preconditions.Length > 0)
                lines.Add($"   preconditions: {tc.Preconditions}");
            foreach (var st in tc.Steps.Take(12))
                lines.Add($"   - {st.Action} → {st.Expected}");
        }

        if (!string.IsNullOrWhiteSpace(manualDescription))
        {
            lines.Add("");
            lines.Add("## Manual description (observed behavior to also assess)");
            lines.Add(manualDescription!.Trim());
        }

        lines.Add("");
        lines.Add("## Actual repository code (excerpts)");
        lines.Add(string.IsNullOrEmpty(codeContext) ? "(no code retrieved)" : codeContext);

        lines.Add("");
        lines.Add("Return ONLY the JSON array of verdicts described in the system prompt.");
        return string.Join("\n", lines);
    }

    internal static string BuildStepsPrompt(
        AdoWorkItem wi, AnalystOutput a, IReadOnlyList<TestCaseInput> cases, string codeContext)
    {
        var lines = new List<string> { $"# Story #{wi.Id} — {wi.Title}" };
        if (a.Module.Length > 0 || a.Feature.Length > 0)
            lines.Add($"Module: {a.Module} · Feature: {a.Feature}");
        lines.Add("");
        lines.Add("## Test cases (judge every step)");
        for (var i = 0; i < cases.Count; i++)
        {
            var tc = cases[i];
            lines.Add($"### Case {i + 1}: {tc.Title}");
            if (tc.Preconditions.Length > 0)
                lines.Add($"preconditions: {tc.Preconditions}");
            for (var j = 0; j < tc.Steps.Count; j++)
                lines.Add($"  {j + 1}. {tc.Steps[j].Action} → {tc.Steps[j].Expected}");
        }
        lines.Add("");
        lines.Add("## Actual repository code (excerpts)");
        lines.Add(string.IsNullOrEmpty(codeContext) ? "(no code retrieved)" : codeContext);
        lines.Add("");
        lines.Add("Return ONLY the JSON array of per-case, per-step verdicts described in the system prompt.");
        return string.Join("\n", lines);
    }
}
