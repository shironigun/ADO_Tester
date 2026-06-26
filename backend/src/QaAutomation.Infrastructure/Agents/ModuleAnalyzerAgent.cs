using System.Text;
using System.Text.Json;        // JsonValueKind
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>One discovered module flow (lightweight) + the existing test cases that cover it
/// (empty = a coverage gap). Detailed per-flow KB is produced later, on demand.</summary>
public sealed record AnalyzedFlow(string Name, string Summary, List<int> CoveredByTestCaseIds);

/// <summary>
/// Module-analyzer agent (Track A) — in ONE call, reads a module's source + its existing ADO test cases,
/// discovers every user-facing flow, and maps which existing cases cover each (none = gap). Output is kept
/// COMPACT (name + summary + covered ids) so the call stays fast; detailed per-flow KB is generated on
/// demand at spec time. Authored prompt; JSON output.
/// </summary>
public sealed class ModuleAnalyzerAgent
{
    private readonly ILlmClient _llm;

    public ModuleAnalyzerAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<AnalyzedFlow>> AnalyzeAsync(
        string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(module, sourceBundle, existingCases);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ModuleAnalyzerSystem, prompt, '{', progress, emit,
            label: "ModuleAnalyzer", cancellationToken: cancellationToken);
        if (data is not JsonObject o || o["flows"] is not JsonArray arr)
            throw new AgentException("Module analyzer did not return a 'flows' array.");

        var flows = new List<AnalyzedFlow>();
        foreach (var item in arr)
        {
            if (item is not JsonObject f)
                continue;
            var ids = new List<int>();
            if ((f["coveredByTestCaseIds"] ?? f["CoveredByTestCaseIds"]) is JsonArray idArr)
                foreach (var x in idArr)
                    if (x is not null && x.GetValueKind() == JsonValueKind.Number && x.AsValue().TryGetValue(out int id))
                        ids.Add(id);

            var name = JsonUtils.StripEmDashes(JsonUtils.StrOr(f, "name"));
            if (name.Length == 0)
                continue;
            flows.Add(new AnalyzedFlow(name, JsonUtils.StripEmDashes(JsonUtils.StrOr(f, "summary")), ids.Distinct().ToList()));
        }
        return flows;
    }

    internal static string BuildPrompt(string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases)
    {
        var lines = new List<string>
        {
            $"## Analyze the **{module}** module: discover all flows and map coverage against the existing test cases.",
            "",
            $"## Existing ADO test cases ({existingCases.Count}) — id, step count, title:",
        };
        if (existingCases.Count == 0)
            lines.Add("(none — every flow you find is a coverage gap)");
        else
            foreach (var c in existingCases)
                lines.Add($"- #{c.Id} [{c.Steps.Count} steps] {c.Title}");
        lines.Add("");
        lines.Add("## Module source code");
        lines.Add(sourceBundle);
        lines.Add("");
        lines.Add("Output the compact flows JSON now.");
        return string.Join("\n", lines);
    }

    /// <summary>Render the whole-module KB doc (flows + coverage) for storage/search.</summary>
    public static string RenderModuleMap(string module, IReadOnlyList<AnalyzedFlow> flows, Func<int, string?> titleOf)
    {
        var sb = new StringBuilder();
        sb.Append("# Module KB + coverage: ").Append(module).Append("\n\n");
        sb.Append("Flows: ").Append(flows.Count)
          .Append(" | covered: ").Append(flows.Count(f => f.CoveredByTestCaseIds.Count > 0))
          .Append(" | gaps: ").Append(flows.Count(f => f.CoveredByTestCaseIds.Count == 0)).Append("\n\n");
        foreach (var f in flows)
        {
            sb.Append("## ").Append(f.Name).Append('\n');
            if (f.Summary.Length > 0) sb.Append(f.Summary).Append('\n');
            sb.Append(f.CoveredByTestCaseIds.Count > 0
                ? "Coverage: COVERED by " + string.Join(", ", f.CoveredByTestCaseIds.Select(id => $"#{id} ({titleOf(id) ?? "?"})")) + "\n\n"
                : "Coverage: GAP (no existing test case)\n\n");
        }
        return sb.ToString();
    }
}
