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

    /// <summary>House API title:
    /// <c>[module] - [feature] - [level] - [detail]</c>, optionally prefixed with a pending tag.</summary>
    public static string BuildApiTitle(
        string module, string feature, string level, string detail, int? caseIndex = null, bool includePendingTag = true)
    {
        var feat = string.IsNullOrWhiteSpace(feature) ? "General" : feature.Trim();
        var body = $"{TitleCaseModule(module)} - {feat} - {level.Trim()} - {detail.Trim()}";
        return includePendingTag ? $"{PendingFor(caseIndex)} {body}" : body;
    }

    /// <summary>Resolve a human feature name for a route: match FRS FeaturesJson apis when present,
    /// else derive from METHOD + path (e.g. POST …/pipelineDeals → "Create Pipeline Deals").</summary>
    public static string ResolveFeatureName(string method, string route, string? extraContext = null)
    {
        var fromFrs = TryMatchFrsFeature(method, route, extraContext);
        if (!string.IsNullOrWhiteSpace(fromFrs)) return fromFrs!;
        return InferFeatureFromRoute(method, route);
    }

    /// <summary>No-op: test selection now uses spec file paths rather than a [tcId] title tag,
    /// so there is nothing to inject. Kept for call-site compatibility.</summary>
    public static string EnsureInSpec(string specCode, string title, int? caseIndex = null) => specCode;

    /// <summary>No-op: see <see cref="EnsureInSpec"/>.</summary>
    public static string EnsureIndexedInSpec(string specCode, int caseCount) => specCode;

    private static string? TryMatchFrsFeature(string method, string route, string? extraContext)
    {
        if (string.IsNullOrWhiteSpace(extraContext)) return null;
        // Locate a Features JSON array (objects with "apis") via balanced-bracket scan.
        var searchFrom = 0;
        while (searchFrom < extraContext.Length)
        {
            var start = extraContext.IndexOf('[', searchFrom);
            if (start < 0) break;
            var end = FindBalancedArrayEnd(extraContext, start);
            if (end < 0) { searchFrom = start + 1; continue; }
            var slice = extraContext[start..(end + 1)];
            searchFrom = end + 1;
            if (!slice.Contains("\"apis\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(slice);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                var routeNorm = NormalizeApiKey(route);
                var methodNorm = (method ?? "").Trim().ToUpperInvariant();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("apis", out var apis) || apis.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    foreach (var api in apis.EnumerateArray())
                    {
                        var apiStr = api.GetString() ?? "";
                        var m = System.Text.RegularExpressions.Regex.Match(apiStr.Trim(),
                            @"^(GET|POST|PUT|DELETE|PATCH)\s+(\S+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        string apiMethod, apiPath;
                        if (m.Success) { apiMethod = m.Groups[1].Value.ToUpperInvariant(); apiPath = m.Groups[2].Value; }
                        else { apiMethod = ""; apiPath = apiStr; }
                        if (apiMethod.Length > 0 && apiMethod != methodNorm) continue;
                        var apiNorm = NormalizeApiKey(apiPath);
                        if (apiNorm.Length == 0) continue;
                        if (routeNorm.Contains(apiNorm, StringComparison.OrdinalIgnoreCase)
                            || apiNorm.Contains(routeNorm, StringComparison.OrdinalIgnoreCase))
                            return FeatureDisplayName(el);
                    }
                }
            }
            catch { /* not valid JSON — keep scanning */ }
        }
        return null;
    }

    private static int FindBalancedArrayEnd(string s, int openIdx)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = openIdx; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
    private static string FeatureDisplayName(System.Text.Json.JsonElement el)
    {
        if (el.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var a in aliases.EnumerateArray())
            {
                var s = a.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
            }
        }
        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id)) return "General";
        return string.Join(' ', id.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string InferFeatureFromRoute(string method, string route)
    {
        var parts = (route ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !(p.StartsWith('{') && p.EndsWith('}')))
            .Select(HumanizeToken)
            .Where(p => p.Length > 0)
            .ToList();
        // Prefer the last two meaningful segments (e.g. deal / pipelineDeals → Deal Pipeline Deals),
        // but collapse to the last one when only one exists.
        var noun = parts.Count == 0 ? "API" : parts[^1];
        // Singular-ish cleanup handled by HumanizeToken; verb from HTTP method.
        var verb = (method ?? "").Trim().ToUpperInvariant() switch
        {
            "POST" => "Create",
            "PUT" or "PATCH" => "Update",
            "DELETE" => "Delete",
            "GET" => parts.Count > 0 && parts[^1].EndsWith("s", StringComparison.Ordinal) ? "List" : "Get",
            _ => "Exercise",
        };
        return $"{verb} {noun}".Trim();
    }

    private static string HumanizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        // camelCase / PascalCase → spaced words
        var spaced = System.Text.RegularExpressions.Regex.Replace(token, "([a-z])([A-Z])", "$1 $2");
        spaced = spaced.Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string NormalizeApiKey(string path) =>
        (path ?? "").Trim().TrimStart('/').ToLowerInvariant();
}

/// <summary>
/// Generates a Zod schema + Playwright contract spec + house-style ADO Test Case for one API route.
/// </summary>
public sealed class ContractGeneratorAgent
{
    private readonly ILlmClient _llm;

    public ContractGeneratorAgent(ILlmClient llm) => _llm = llm;

    public async Task<GeneratedSpec> GenerateAsync(
        string module, RouteInfo route, string? extraContext = null,
        bool isProductMode = false,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, route, extraContext);

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
        specCode = SpecNormalizer.Normalize(TitleTag.EnsureInSpec(specCode, $"Contract: {route.Method} {route.Route}"), isProductMode);

        // Pass 2: ADO Test Case
        var tcData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ContractTestcaseSystem, userPrompt, '[', progress, emit,
            label: "ContractTestcase", cancellationToken: ct);

        var feature = TitleTag.ResolveFeatureName(route.Method, route.Route, extraContext);
        var tc = ParseTestCase(tcData) ?? FallbackTestCase(module, route, feature, extraContext);
        // Always force house title — LLM may emit legacy "Contract: METHOD route" shapes.
        tc.Title = TitleTag.BuildApiTitle(module, feature, "Contract",
            $"{route.Method} {route.Route} - response shape matches schema");

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

    private static string BuildPrompt(string module, RouteInfo route, string? extraContext = null)
    {
        var feature = TitleTag.ResolveFeatureName(route.Method, route.Route, extraContext);
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n");
        sb.Append($"Feature: {feature}\n");
        sb.Append($"Level: Contract\n");
        sb.Append($"Route: {route.Method} {route.Route}\n");
        sb.Append($"Title must be: {TitleTag.TitleCaseModule(module)} - {feature} - Contract - {route.Method} {route.Route} - response shape matches schema\n");
        if (route.RequestDto is not null) sb.Append($"RequestDto: {route.RequestDto}\n");
        if (route.ResponseDto is not null) sb.Append($"ResponseDto: {route.ResponseDto}\n");
        if (route.ValidationRules.Count > 0) sb.Append($"ValidationRules: {string.Join(", ", route.ValidationRules)}\n");
        if (route.AuthRequirements.Count > 0) sb.Append($"AuthRequirements: {string.Join(", ", route.AuthRequirements)}\n");
        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            sb.Append("\n## Extra Context (from tester)\n");
            sb.Append(extraContext.Trim());
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Refine an existing contract spec+steps with reviewer feedback.
    /// Re-emits with the same output shape; system prompt unchanged.</summary>
    public async Task<GeneratedSpec> RefineAsync(
        string module, RouteInfo route, string priorSpec, IReadOnlyList<TestCaseStep> priorSteps,
        string feedback, string? extraContext = null,
        bool isProductMode = false,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append(BuildPrompt(module, route, extraContext));
        sb.Append("\n## Prior spec\n");
        sb.Append(priorSpec);
        sb.Append("\n\n## Prior ADO test case steps\n");
        foreach (var s in priorSteps)
            sb.Append($"- Action: {s.Action} | Expected: {s.Expected}\n");
        sb.Append("\n## Reviewer Feedback (refine, do NOT restart)\n");
        sb.Append(feedback.Trim());
        sb.Append('\n');

        var specData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ContractGeneratorSystem, sb.ToString(), '{', progress, emit,
            label: "ContractGenerator(refine)", cancellationToken: ct);
        var specCode = "";
        var schemaJson = "";
        if (specData is JsonObject so) { specCode = JsonUtils.StrOr(so, "spec"); schemaJson = JsonUtils.StrOr(so, "schema"); }
        specCode = SpecNormalizer.Normalize(TitleTag.EnsureInSpec(specCode, $"Contract: {route.Method} {route.Route}"), isProductMode);

        var tcData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.ContractTestcaseSystem, sb.ToString(), '[', progress, emit,
            label: "ContractTestcase(refine)", cancellationToken: ct);
        var feature = TitleTag.ResolveFeatureName(route.Method, route.Route, extraContext);
        var tc = ParseTestCase(tcData) ?? FallbackTestCase(module, route, feature, extraContext);
        tc.Title = TitleTag.BuildApiTitle(module, feature, "Contract",
            $"{route.Method} {route.Route} - response shape matches schema");
        return new GeneratedSpec { Level = "contract", Title = tc.Title, SpecCode = specCode, SchemaJson = schemaJson.Length > 0 ? schemaJson : null, SourceRoutes = [$"{route.Method} {route.Route}"], TestCase = tc };
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

    private static GeneratedTestCase FallbackTestCase(string module, RouteInfo route, string? feature = null, string? extraContext = null)
    {
        feature ??= TitleTag.ResolveFeatureName(route.Method, route.Route, extraContext);
        return new()
        {
            Title = TitleTag.BuildApiTitle(module, feature, "Contract",
                $"{route.Method} {route.Route} - response shape matches schema"),
            Preconditions = "",
            Steps =
            [
                new TestCaseStep { Action = "Obtain a valid bearer token for the QA environment", Expected = "Token received" },
                new TestCaseStep { Action = $"Send a {route.Method} request to {route.Route} with the bearer token", Expected = "Response returns 200" },
                new TestCaseStep { Action = "Parse the JSON response body", Expected = "Valid JSON body returned" },
                new TestCaseStep { Action = "Verify that the response body matches the expected schema", Expected = $"Response contains the fields declared by the {route.Method} {route.Route} response contract, each non-null and of the declared type" },
            ],
        };
    }

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
