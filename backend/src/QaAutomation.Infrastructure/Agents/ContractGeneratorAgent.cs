using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>One generated Playwright spec + ADO Test Case for a single contract surface.</summary>
public sealed class GeneratedSpec
{
    public string Level { get; init; } = "";
    public string Title { get; init; } = "";
    public string SpecCode { get; init; } = "";
    public string? SchemaJson { get; init; }
    public List<string> SourceRoutes { get; init; } = [];
    public GeneratedTestCase? TestCase { get; init; }
    /// <summary>QAL-067: index of this case within a shared multi-case spec file (Endpoint's 200/401/400
    /// cases all live in ONE physical file). Null for Contract/Workflow, which are always single-case.
    /// Drives which of the file's several [<tcId-pending:N>] placeholders this draft's push may rewrite.</summary>
    public int? CaseIndex { get; init; }
}

/// <summary>
/// [<tcId-pending>] title-tag enforcement helper. Ensures every generated spec title contains the
/// placeholder so the JUnit \[(\d+)\] parser can later map failures back to ADO Test Cases.
/// </summary>
public static partial class TitleTag
{
    public const string Pending = "[<tcId-pending>]";

    /// <summary>QAL-067: the per-case placeholder for case <paramref name="caseIndex"/> of a shared
    /// multi-case spec file, e.g. "[<tcId-pending:1>]". Null index returns the plain <see cref="Pending"/>
    /// tag used by single-case levels (Contract/Workflow).</summary>
    public static string PendingFor(int? caseIndex) =>
        caseIndex is { } i ? $"[<tcId-pending:{i}>]" : Pending;

    /// <summary>Title-case a module name for display in test-case titles (e.g. "leads" → "Leads").
    /// Leaves already-cased names alone; falls back to "Module" if empty.</summary>
    public static string TitleCaseModule(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return "Module";
        var trimmed = module.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>No-op: test selection now uses spec file paths rather than a [tcId] title tag,
    /// so there is nothing to inject. Kept for call-site compatibility.</summary>
    public static string EnsureInSpec(string specCode, string title, int? caseIndex = null) => specCode;

    /// <summary>No-op: see <see cref="EnsureInSpec"/>.</summary>
    public static string EnsureIndexedInSpec(string specCode, int caseCount) => specCode;
}

/// <summary>
/// Generates a Zod schema + Playwright contract spec + house-style ADO Test Case for one API route.
/// </summary>
public sealed class ContractGeneratorAgent
{
    private readonly ILlmClient _llm;

    public ContractGeneratorAgent(ILlmClient llm) => _llm = llm;

    public async Task<GeneratedSpec> GenerateAsync(
        string module, RouteInfo route,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, route);

        // Pass 1: spec + schema
        var specData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ContractGeneratorSystem, userPrompt, '{', progress, emit,
            label: "ContractGenerator", cancellationToken: ct);

        var specCode = "";
        var schemaJson = "";
        if (specData is JsonObject so)
        {
            specCode = JsonUtils.StrOr(so, "spec");
            schemaJson = JsonUtils.StrOr(so, "schema");
        }
        specCode = TitleTag.EnsureInSpec(specCode, $"Contract: {route.Method} {route.Route}");

        // Pass 2: ADO Test Case
        var tcData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ContractTestcaseSystem, userPrompt, '[', progress, emit,
            label: "ContractTestcase", cancellationToken: ct);

        var mod = TitleTag.TitleCaseModule(module);
        var tc = ParseTestCase(tcData) ?? FallbackTestCase(module, route);
        tc.Title = TitleTag.EnsureTitle(tc.Title, $"{mod} - Contract - {route.Method} - Verify that {route.Route} response shape matches the expected schema");

        var slug = Slug($"{route.Method}-{route.Route}");
        return new GeneratedSpec
        {
            Level = "contract",
            Title = tc.Title,
            SpecCode = specCode,
            SchemaJson = schemaJson.Length > 0 ? schemaJson : null,
            SourceRoutes = [$"{route.Method} {route.Route}"],
            TestCase = tc,
        };
    }

    private static string BuildPrompt(string module, RouteInfo route)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n");
        sb.Append($"Route: {route.Method} {route.Route}\n");
        if (route.RequestDto is not null) sb.Append($"RequestDto: {route.RequestDto}\n");
        if (route.ResponseDto is not null) sb.Append($"ResponseDto: {route.ResponseDto}\n");
        if (route.ValidationRules.Count > 0) sb.Append($"ValidationRules: {string.Join(", ", route.ValidationRules)}\n");
        if (route.AuthRequirements.Count > 0) sb.Append($"AuthRequirements: {string.Join(", ", route.AuthRequirements)}\n");
        return sb.ToString();
    }

    private static GeneratedTestCase? ParseTestCase(JsonNode data)
    {
        var arr = data as JsonArray ?? (data as JsonObject)?["cases"] as JsonArray;
        if (arr is null || arr.Count == 0) return null;
        var item = arr[0] as JsonObject;
        if (item is null) return null;
        return new GeneratedTestCase
        {
            Title = JsonUtils.StrOr(item, "title"),
            Preconditions = "",
            Steps = ParseSteps(item["steps"] as JsonArray),
        };
    }

    private static GeneratedTestCase FallbackTestCase(string module, RouteInfo route) => new()
    {
        Title = $"{TitleTag.Pending} {TitleTag.TitleCaseModule(module)} - Contract - {route.Method} - Verify that {route.Route} response shape matches the expected schema",
        Preconditions = "",
        Steps =
        [
            new TestCaseStep { Action = "Obtain a valid bearer token for the INFINITY QA environment", Expected = "Token received" },
            new TestCaseStep { Action = $"Send a {route.Method} request to {route.Route} with the bearer token", Expected = "Response returns 200" },
            new TestCaseStep { Action = "Parse the JSON response body", Expected = "Valid JSON body returned" },
            new TestCaseStep { Action = "Verify that the response body matches the expected schema", Expected = $"Response contains the fields declared by the {route.Method} {route.Route} response contract, each non-null and of the declared type" },
        ],
    };

    private static List<TestCaseStep> ParseSteps(JsonArray? arr)
    {
        var steps = new List<TestCaseStep>();
        if (arr is null) return steps;
        foreach (var item in arr)
            if (item is JsonObject s)
                steps.Add(new TestCaseStep { Action = JsonUtils.StrOr(s, "action"), Expected = JsonUtils.StrOr(s, "expected") });
        return steps;
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}

// Extension for TitleTag used by all three generators
public static partial class TitleTag
{
    /// <summary>Ensures the title starts with its pending tag; if not, prepends it. If the title already
    /// carries the WRONG pending tag (e.g. the model emitted the bare tag but this case needs an indexed
    /// one), swaps it for the correct one instead of double-tagging. A real numeric tag (already pushed)
    /// is left untouched.</summary>
    public static string EnsureTitle(string title, string fallbackSuffix, int? caseIndex = null)
    {
        var tag = PendingFor(caseIndex);
        if (title.Contains(tag))
            return title;
        if (System.Text.RegularExpressions.Regex.IsMatch(title, @"^\[.*\d+.*\]"))
            return title;   // already has a real numeric tag — leave it alone
        if (title.Contains(Pending))
            return title.Replace(Pending, tag);   // wrong (bare) pending tag present — fix it up
        return title.Length > 0 ? $"{tag} {title}" : $"{tag} {fallbackSuffix}";
    }
}
