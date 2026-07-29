using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>One discovered module flow (lightweight) + the existing test cases that cover it
/// (empty = a coverage gap). Used by the legacy ModuleAnalysisService path.</summary>
public sealed record AnalyzedFlow(string Name, string Summary, List<int> CoveredByTestCaseIds);

/// <summary>Result of the new QA Loop inventory analysis pass.</summary>
public sealed record ModuleAnalysis(InventoryDoc Inventory, SurfaceMatrix Surface, List<string> AdditionalFilesNeeded);

/// <summary>
/// Module-analyzer agent — two operating modes:
/// <para>Legacy (Track A): <see cref="AnalyzeAsync"/> returns flows + coverage for ModuleAnalysisService.</para>
/// <para>QA Loop: <see cref="AnalyzeInventoryAsync"/> returns InventoryDoc + SurfaceMatrix + additionalFilesNeeded.
/// If additionalFilesNeeded is non-empty, C# fetches them via IAzureDevOpsClient and re-invokes exactly once.</para>
/// </summary>
public sealed class ModuleAnalyzerAgent
{
    private readonly ILlmClient _llm;

    public ModuleAnalyzerAgent(ILlmClient llm) => _llm = llm;

    // ── Legacy Track A path ───────────────────────────────────────────────────

    public async Task<List<AnalyzedFlow>> AnalyzeAsync(
        string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildLegacyPrompt(module, sourceBundle, existingCases);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ModuleAnalyzerSystem, prompt, '{', progress, emit,
            label: "ModuleAnalyzer", cancellationToken: cancellationToken);
        if (data is not JsonObject o || o["flows"] is not JsonArray arr)
            throw new AgentException("Module analyzer did not return a 'flows' array.");

        var flows = new List<AnalyzedFlow>();
        foreach (var item in arr)
        {
            if (item is not JsonObject f) continue;
            var ids = new List<int>();
            if ((f["coveredByTestCaseIds"] ?? f["CoveredByTestCaseIds"]) is JsonArray idArr)
                foreach (var x in idArr)
                    if (x is not null && x.GetValueKind() == JsonValueKind.Number && x.AsValue().TryGetValue(out int id))
                        ids.Add(id);
            var name = JsonUtils.StripEmDashes(JsonUtils.StrOr(f, "name"));
            if (name.Length == 0) continue;
            flows.Add(new AnalyzedFlow(name, JsonUtils.StripEmDashes(JsonUtils.StrOr(f, "summary")), ids.Distinct().ToList()));
        }
        return flows;
    }

    // ── QA Loop inventory path ────────────────────────────────────────────────

    /// <summary>One-pass inventory + surface matrix analysis. If the model requests additional files,
    /// fetches them via <paramref name="fetchFile"/> and re-invokes exactly once.</summary>
    public async Task<ModuleAnalysis> AnalyzeInventoryAsync(
        string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases,
        string? additionalContext = null,
        Func<string, CancellationToken, Task<string?>>? fetchFile = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var prompt = BuildInventoryPrompt(module, sourceBundle, existingCases, additionalContext);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ModuleAnalyzerSystem, prompt, '{', progress, emit,
            label: "ModuleAnalyzer", cancellationToken: ct);
        if (data is not JsonObject root)
            throw new AgentException("Module analyzer did not return a JSON object.");

        // Optional one-time refinement fetch. Bounded so the refined prompt stays within the LLM's
        // context window: for frontend-driven scans the model can request many (large) backend files,
        // which otherwise blows past provider limits (ClaudeCli "Prompt is too long"). Cap the count and
        // truncate each file; a bigger-context provider can raise these for fuller coverage.
        const int MaxRefinementFiles = 6;
        const int MaxRefinementFileChars = 8000;
        var additionalNeeded = ParseStringArray(root, "additionalFilesNeeded");
        if (additionalNeeded.Count > 0 && fetchFile is not null)
        {
            var extras = new List<string>();
            foreach (var path in additionalNeeded.Take(MaxRefinementFiles))
            {
                var content = await fetchFile(path, ct);
                if (string.IsNullOrEmpty(content)) continue;
                if (content.Length > MaxRefinementFileChars)
                    content = content[..MaxRefinementFileChars] + "\n/* ...truncated... */";
                extras.Add($"\n### {path}\n```\n{content}\n```");
            }
            if (extras.Count > 0)
            {
                var refined = prompt + "\n\n## Additional files requested\n" + string.Join("\n", extras);
                data = await JsonUtils.CompleteJsonAsync(
                    _llm, AgentPrompts.ModuleAnalyzerSystem, refined, '{', progress, emit,
                    label: "ModuleAnalyzer(refined)", cancellationToken: ct);
                if (data is not JsonObject r2)
                    throw new AgentException("Module analyzer refinement did not return a JSON object.");
                root = r2;
            }
        }

        return ParseAnalysis(module, root, existingCases);
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private static ModuleAnalysis ParseAnalysis(
        string module, JsonObject root, IReadOnlyList<LinkedTestCase> existingCases)
    {
        var existingIds = existingCases.Select(c => c.Id).ToHashSet();

        // inventory
        var inv = root["inventory"] as JsonObject;
        var routes = new List<RouteInfo>();
        var dtos   = new List<DtoInfo>();
        var wflows = new List<WorkflowInfo>();
        if (inv is not null)
        {
            if (inv["routes"] is JsonArray ra)
                foreach (var r in ra)
                    if (r is JsonObject ro)
                        routes.Add(new RouteInfo(
                            JsonUtils.StrOr(ro, "method"), JsonUtils.StrOr(ro, "route"),
                            ro["requestDto"]?.GetValue<string>(), ro["responseDto"]?.GetValue<string>(),
                            ParseStringArray(ro, "validationRules"), ParseStringArray(ro, "authRequirements")));

            if (inv["dtos"] is JsonArray da)
                foreach (var d in da)
                    if (d is JsonObject dto)
                    {
                        var fields = new List<DtoField>();
                        if (dto["fields"] is JsonArray fa)
                            foreach (var fi in fa)
                                if (fi is JsonObject fio)
                                    fields.Add(new DtoField(JsonUtils.StrOr(fio, "name"), JsonUtils.StrOr(fio, "type")));
                        dtos.Add(new DtoInfo(JsonUtils.StrOr(dto, "name"), fields));
                    }

            if (inv["workflows"] is JsonArray wa)
                foreach (var w in wa)
                    if (w is JsonObject wo)
                    {
                        var steps = new List<WorkflowStep>();
                        if (wo["steps"] is JsonArray sa)
                            foreach (var s in sa)
                                if (s is JsonObject so)
                                    steps.Add(new WorkflowStep(JsonUtils.StrOr(so, "endpoint"), JsonUtils.StrOr(so, "role")));
                        wflows.Add(new WorkflowInfo(JsonUtils.StrOr(wo, "name"), steps));
                    }
        }
        var inventory = new InventoryDoc(module, routes, dtos, wflows);

        // surfaceMatrix
        var rows = new List<SurfaceRow>();
        if ((root["surfaceMatrix"] as JsonObject)?["rows"] is JsonArray sra)
            foreach (var r in sra)
                if (r is JsonObject ro)
                {
                    var linkedIdRaw = ro["linkedTestCaseId"];
                    int? linkedId = (linkedIdRaw is not null && linkedIdRaw.GetValueKind() == JsonValueKind.Number)
                        ? linkedIdRaw.AsValue().GetValue<int>()
                        : null;
                    if (linkedId.HasValue && !existingIds.Contains(linkedId.Value))
                        linkedId = null;  // model cited a non-existent id — reject

                    var status = JsonUtils.StrOr(ro, "status");
                    // gap=selected, covered=not selected
                    var selectedDefault = !string.Equals(status, "covered", StringComparison.OrdinalIgnoreCase);
                    var selectedRaw = ro["selected"];
                    var selected = selectedRaw is not null && selectedRaw.GetValueKind() == JsonValueKind.False
                        ? false
                        : selectedRaw is not null && selectedRaw.GetValueKind() == JsonValueKind.True
                        ? true
                        : selectedDefault;

                    var levelStr = JsonUtils.StrOr(ro, "level");
                    var level = QaLoopEnumExtensions.ToTestLevel(levelStr);

                    rows.Add(new SurfaceRow
                    {
                        Id = JsonUtils.StrOr(ro, "id"),
                        Level = level,
                        Label = JsonUtils.StrOr(ro, "label"),
                        Status = status,
                        LinkedTestCaseId = linkedId,
                        Selected = selected,
                    });
                }
        var surface = new SurfaceMatrix(module, rows);

        var additionalNeeded = ParseStringArray(root, "additionalFilesNeeded");
        return new ModuleAnalysis(inventory, surface, additionalNeeded);
    }

    private static List<string> ParseStringArray(JsonObject obj, string key)
    {
        var result = new List<string>();
        if (obj[key] is JsonArray arr)
            foreach (var item in arr)
                if (item?.GetValue<string>() is string s && s.Length > 0)
                    result.Add(s);
        return result;
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    internal static string BuildInventoryPrompt(string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases, string? additionalContext = null)
    {
        var lines = new List<string>
        {
            $"## Analyze the **{module}** module: produce the inventory and 3-level coverage matrix.",
            "",
            $"## Existing ADO test cases ({existingCases.Count}) — id, step count, title:",
        };
        if (existingCases.Count == 0)
            lines.Add("(none — every surface is a coverage gap)");
        else
            foreach (var c in existingCases)
                lines.Add($"- #{c.Id} [{c.Steps.Count} steps] {c.Title}");
        lines.Add("");
        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            lines.Add("## Additional Context (from tester)");
            lines.Add(additionalContext.Trim());
            lines.Add("");
        }
        lines.Add("## Module source code");
        lines.Add(sourceBundle);
        lines.Add("");
        lines.Add("Output the inventory + surfaceMatrix JSON now.");
        return string.Join("\n", lines);
    }

    // ponytail: keeping the legacy prompt builder separate — only AnalyzeAsync (Track A) uses it
    internal static string BuildLegacyPrompt(string module, string sourceBundle, IReadOnlyList<LinkedTestCase> existingCases)
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
