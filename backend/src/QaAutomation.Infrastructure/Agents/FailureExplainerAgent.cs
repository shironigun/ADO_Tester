using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>One failed test sent for AI analysis (index echoes back in the response).</summary>
public sealed record FailureToExplain(int Index, string Level, string Title, string Error);

/// <summary>AI analysis for one failed test.</summary>
public sealed record FailureExplanation(int Index, string RootCause, string Explanation, string SuggestedFix, string Confidence);

/// <summary>
/// Single-shot batch analysis of a run's failed tests — ONE LLM call for the whole run (the
/// ClaudeCli provider rate-limits hard, so per-failure calls are off the table). Output feeds the
/// per-test "AI Failure Analysis" attachments in the Allure report.
/// </summary>
public sealed class FailureExplainerAgent
{
    private const string SystemPrompt = """
        You are a senior QA automation engineer. You analyze failed Playwright API test results for a
        CRM product tested at three levels: Contract (response schema validation), Endpoint
        (status-code behavior per route), and Workflow (multi-step business flows).

        You receive a JSON list of failed tests (index, level, title, error). For EACH failure return
        a concise analysis. Return ONLY a JSON object of this exact shape:
        {
          "analyses": [
            {
              "index": <int — echo the input index>,
              "rootCause": "<one line — the most likely root cause>",
              "explanation": "<2-3 sentences — what the error means; what the API actually did vs what was expected>",
              "suggestedFix": "<one or two concrete next steps — fix product code, fix the test, or investigate data/env>",
              "confidence": "high|medium|low"
            }
          ]
        }

        Analysis rules:
        - HTTP 5xx responses usually indicate real product bugs (the server crashed handling the request).
        - A 200 where 400/422 was expected usually means missing input validation — likely a real bug.
        - 401/403 mismatches suggest auth/token issues — often test environment, not product.
        - Timeouts / ECONNREFUSED / ENOTFOUND are usually environment or connectivity, not product bugs.
        - Identical errors on the same route should get consistent analyses.
        - Be specific to the route/payload named in the title; never invent details not present in the input.
        """;

    private readonly ILlmClient _llm;

    public FailureExplainerAgent(ILlmClient llm) => _llm = llm;

    public async Task<IReadOnlyList<FailureExplanation>> ExplainAsync(
        IReadOnlyList<FailureToExplain> failures, CancellationToken ct = default)
    {
        if (failures.Count == 0) return [];

        var userPrompt = "## Failed tests\n" + JsonSerializer.Serialize(failures)
            + "\n\nOutput the analyses JSON now.";
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, SystemPrompt, userPrompt, '{', label: "FailureExplainer", cancellationToken: ct);

        if (data is not JsonObject root || root["analyses"] is not JsonArray arr)
            throw new AgentException("Failure explainer did not return an analyses array.");

        var results = new List<FailureExplanation>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var idx = o["index"]?.GetValueKind() == JsonValueKind.Number ? o["index"]!.GetValue<int>() : -1;
            if (idx < 0) continue;
            results.Add(new FailureExplanation(
                idx,
                JsonUtils.StrOr(o, "rootCause"),
                JsonUtils.StrOr(o, "explanation"),
                JsonUtils.StrOr(o, "suggestedFix"),
                JsonUtils.StrOr(o, "confidence") is { Length: > 0 } c ? c : "low"));
        }
        return results;
    }
}
