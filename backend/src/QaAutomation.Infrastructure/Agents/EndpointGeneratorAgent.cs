using System.Text;
using System.Text.Json.Nodes;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Generates a Playwright endpoint spec (200/401/400) + one ADO Test Case per case.</summary>
public sealed class EndpointGeneratorAgent
{
    private readonly ILlmClient _llm;

    public EndpointGeneratorAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<GeneratedSpec>> GenerateAsync(
        string module, RouteInfo route,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, route);

        // Pass 1: spec + cases metadata
        var specData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.EndpointGeneratorSystem, userPrompt, '{', progress, emit,
            label: "EndpointGenerator", cancellationToken: ct);

        var rawSpecCode = "";
        var cases = new List<(string Label, int StatusCode)>();
        if (specData is JsonObject so)
        {
            rawSpecCode = JsonUtils.StrOr(so, "spec");
            if (so["cases"] is JsonArray ca)
                foreach (var c in ca)
                    if (c is JsonObject co)
                        cases.Add((JsonUtils.StrOr(co, "label"), (int)(co["statusCode"]?.GetValue<int>() ?? 200)));
        }
        if (cases.Count == 0)
            cases = [("200 happy path", 200), ("401 without bearer", 401), ("400 on invalid input", 400)];

        // QAL-067: tag each case's test( block with its own [<tcId-pending:N>] placeholder (in source
        // order) BEFORE splitting into per-case GeneratedSpecs — the file is shared, so this must happen
        // once against the whole spec, not per-case against an already-scattered copy.
        // SpecNormalizer guarantees the auth/base-URL wiring is correct even if the LLM drifts.
        var specCode = SpecNormalizer.Normalize(TitleTag.EnsureIndexedInSpec(rawSpecCode, cases.Count));

        // Pass 2: ADO Test Cases for each case
        var tcData = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.EndpointTestcaseSystem,
            userPrompt + $"\n\nGenerate one test case per case: {string.Join(", ", cases.Select(c => c.Label))}",
            '[', progress, emit, label: "EndpointTestcase", cancellationToken: ct);

        var tcs = ParseTestCases(tcData, cases);
        var mod = TitleTag.TitleCaseModule(module);
        var specs = new List<GeneratedSpec>();
        for (var i = 0; i < cases.Count; i++)
        {
            var tc = i < tcs.Count ? tcs[i] : FallbackTestCase(module, route, cases[i].Label, cases[i].StatusCode);
            tc.Title = TitleTag.EnsureTitle(tc.Title, $"{mod} - Endpoint - {route.Method} - Verify that {route.Route} {cases[i].Label}", caseIndex: i);
            specs.Add(new GeneratedSpec
            {
                Level = "endpoint",
                Title = tc.Title,
                // QAL-067: ALL cases carry the same shared file's code (not just case 0) so PersistDraft
                // can give every case a SpecFilePath — case index is what disambiguates them on push.
                SpecCode = specCode,
                SourceRoutes = [$"{route.Method} {route.Route}"],
                TestCase = tc,
                CaseIndex = i,
            });
        }
        return specs;
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

    private static List<GeneratedTestCase> ParseTestCases(JsonNode data, List<(string Label, int StatusCode)> cases)
    {
        var arr = data as JsonArray;
        if (arr is null) return [];
        var result = new List<GeneratedTestCase>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            result.Add(new GeneratedTestCase
            {
                Title = JsonUtils.StrOr(obj, "title"),
                Preconditions = "",
                Steps = ParseSteps(obj["steps"] as JsonArray),
            });
        }
        return result;
    }

    private static GeneratedTestCase FallbackTestCase(string module, RouteInfo route, string label, int statusCode) => new()
    {
        // No tag embedded here — the caller's EnsureTitle(..., caseIndex: i) adds the correct
        // per-case [<tcId-pending:N>] tag; pre-embedding the bare Pending tag would double-tag it.
        Title = $"{TitleTag.TitleCaseModule(module)} - Endpoint - {route.Method} - Verify that {route.Route} {label}",
        Preconditions = "",
        Steps =
        [
            new TestCaseStep { Action = "Obtain a valid bearer token for the INFINITY QA environment", Expected = "Token received" },
            new TestCaseStep { Action = $"Send a {route.Method} request to {route.Route}", Expected = $"Response returns {statusCode} and the body carries the payload expected for the {label} case" },
            new TestCaseStep { Action = $"Verify that the response returns status {statusCode}", Expected = $"HTTP {statusCode} is returned, and — for state-changing calls — the returned resource shows the change made by this request" },
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
}
