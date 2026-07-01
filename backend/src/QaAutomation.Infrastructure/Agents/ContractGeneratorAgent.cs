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

    /// <summary>Returns spec text guaranteed to contain a pending tag in the test title line. If the
    /// model omitted it, injects it at the start of the first test/describe title found.</summary>
    public static string EnsureInSpec(string specCode, string title, int? caseIndex = null)
    {
        var tag = PendingFor(caseIndex);
        if (specCode.Contains(tag) || System.Text.RegularExpressions.Regex.IsMatch(specCode, @"\[\d+\]"))
            return specCode;
        // Inject into the first test( or describe( title string.
        var pattern = new System.Text.RegularExpressions.Regex(@"(test|describe)\s*\(\s*(['""])");
        return pattern.Replace(specCode, m => $"{m.Groups[1].Value}({m.Groups[2].Value}{tag} ", 1);
    }

    /// <summary>QAL-067: same as <see cref="EnsureInSpec"/> but for a shared multi-case spec file — tags
    /// the Nth untagged test(/describe( occurrence with [<tcId-pending:N>], in source order, for N in
    /// [0, caseCount). If the model already tagged some/all titles (any [<tcId-pending...] or [\d+] tag),
    /// those occurrences are left alone and only the remaining untagged ones get indexed tags, so this is
    /// safe to call even when the model partially followed instructions.</summary>
    public static string EnsureIndexedInSpec(string specCode, int caseCount)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"(test|describe)\s*\(\s*(['""])");
        var nextIndex = 0;
        return pattern.Replace(specCode, m =>
        {
            // Peek at the text right after the opening quote — if it's already tagged, skip it untouched.
            var tailStart = m.Index + m.Length;
            var tail = specCode.Substring(tailStart, Math.Min(64, specCode.Length - tailStart));
            if (tail.Contains("[<tcId-pending") || System.Text.RegularExpressions.Regex.IsMatch(tail, @"^\[\d+\]"))
                return m.Value;
            if (nextIndex >= caseCount)
                return m.Value;
            var tag = PendingFor(nextIndex++);
            return $"{m.Groups[1].Value}({m.Groups[2].Value}{tag} ";
        });
    }
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

        var tc = ParseTestCase(tcData) ?? FallbackTestCase(route);
        tc.Title = TitleTag.EnsureTitle(tc.Title, $"Contract: {route.Method} {route.Route} - response shape matches schema");

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

    private static GeneratedTestCase FallbackTestCase(RouteInfo route) => new()
    {
        Title = $"{TitleTag.Pending} Contract: {route.Method} {route.Route} - response shape matches schema",
        Preconditions = "",
        Steps =
        [
            new TestCaseStep { Action = "Obtain a valid bearer token for the INFINITY QA environment", Expected = "Token received" },
            new TestCaseStep { Action = $"Send a {route.Method} request to {route.Route} with the bearer token", Expected = "Response received with status 200" },
            new TestCaseStep { Action = "Parse the JSON response body", Expected = "Valid JSON body returned" },
            new TestCaseStep { Action = "Verify that the response body matches the expected schema", Expected = "All required fields are present with correct types" },
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
