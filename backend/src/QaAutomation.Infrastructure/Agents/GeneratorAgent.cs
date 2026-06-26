using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Generator agent (Track A) — emits a runnable Playwright E2E spec (TypeScript) from a KB flow document
/// plus a scenario. This is the "Tester → Generator" evolution: distinct from <see cref="TesterAgent"/>
/// (which emits ADO test-case drafts), the Generator emits CODE. Its system prompt is authored (not a
/// frozen Python port) and safe to iterate on. Output is code, so it calls
/// <see cref="ILlmClient.CompleteAsync"/> directly. One spec per call keeps generations small (CLI exit-1).
/// </summary>
public sealed class GeneratorAgent
{
    private readonly ILlmClient _llm;

    public GeneratorAgent(ILlmClient llm) => _llm = llm;

    /// <summary>Generate one self-contained Playwright E2E spec. <paramref name="testTitle"/> is the EXACT,
    /// already-tagged title (carries `[adoId]` + `@smoke`); the model must not change it.</summary>
    public async Task<string> GenerateE2eSpecAsync(
        string module, string testTitle, string scenarioIntent, string kbFlowMarkdown, string? styleReference,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(module, testTitle, scenarioIntent, kbFlowMarkdown, styleReference);
        var raw = await _llm.CompleteAsync(AgentPrompts.GeneratorE2eSystem, prompt, null, progress, emit, cancellationToken);
        return CleanCode(raw);
    }

    /// <summary>Author ONE ADO Test Case (title + preconditions + steps) from a scenario and its KB flow,
    /// so the deterministic ADO client can create it and we can tag the spec with the real id. Returns the
    /// existing <see cref="GeneratedTestCase"/> shape (same as the Tester) for a uniform ADO write path.</summary>
    public async Task<GeneratedTestCase> AuthorAdoTestCaseAsync(
        string module, string scenarioTitle, string scenarioIntent, string kbFlowMarkdown,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildAuthorPrompt(module, scenarioTitle, scenarioIntent, kbFlowMarkdown);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.TestCaseAuthorSystem, prompt, '{', progress, emit,
            label: "TestCaseAuthor", cancellationToken: cancellationToken);
        if (data is not JsonObject o)
            throw new AgentException("Test-case author returned non-object JSON.");

        var steps = new List<TestCaseStep>();
        if ((o["steps"] ?? o["Steps"]) is JsonArray arr)
            foreach (var s in arr)
                if (s is JsonObject so)
                    steps.Add(new TestCaseStep
                    {
                        Action = JsonUtils.StrOr(so, "action", "Action"),
                        Expected = JsonUtils.StrOr(so, "expected", "Expected"),
                    });

        var title = JsonUtils.StrOr(o, "title", "Title");
        return new GeneratedTestCase
        {
            Title = string.IsNullOrWhiteSpace(title) ? scenarioTitle : title,
            Preconditions = JsonUtils.StrOr(o, "preconditions", "Preconditions"),
            Steps = steps,
        };
    }

    internal static string BuildAuthorPrompt(string module, string scenarioTitle, string scenarioIntent, string kbFlowMarkdown)
    {
        var lines = new List<string>
        {
            $"## Author ONE ADO Test Case for the **{module}** module.",
            "",
            $"**Scenario:** {scenarioTitle}",
        };
        if (!string.IsNullOrWhiteSpace(scenarioIntent))
            lines.Add($"**Intent:** {scenarioIntent}");
        lines.Add("");
        lines.Add("## KB flow — ground the steps, preconditions, and expected results in this:");
        lines.Add(kbFlowMarkdown);
        lines.Add("");
        lines.Add("Output the Test Case JSON now.");
        return string.Join("\n", lines);
    }

    internal static string BuildPrompt(
        string module, string testTitle, string scenarioIntent, string kbFlowMarkdown, string? styleReference)
    {
        var lines = new List<string>
        {
            $"## Task: write ONE Playwright E2E spec for the **{module}** module.",
            "",
            $"**Exact test title to use (do not change a single character):** `{testTitle}`",
        };
        if (!string.IsNullOrWhiteSpace(scenarioIntent))
        {
            lines.Add("");
            lines.Add($"**Scenario intent:** {scenarioIntent}");
        }
        lines.Add("");
        lines.Add("## KB flow — ground every step, locator, route, field, and validation in this:");
        lines.Add(kbFlowMarkdown);
        if (!string.IsNullOrWhiteSpace(styleReference))
        {
            lines.Add("");
            lines.Add("## House-style reference — an existing spec from this repo. Match its imports, structure,");
            lines.Add("comment tone, and `test.step` usage. Do NOT import its page objects (be self-contained):");
            lines.Add("```ts");
            lines.Add(styleReference!);
            lines.Add("```");
        }
        lines.Add("");
        lines.Add("Output the spec file now — TypeScript only, no fences.");
        return string.Join("\n", lines);
    }

    /// <summary>Strip an accidental wrapping ```/```ts fence the model sometimes adds around the code.</summary>
    internal static string CleanCode(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline > 0)
                s = s[(firstNewline + 1)..];
            if (s.EndsWith("```", StringComparison.Ordinal))
                s = s[..^3];
        }
        return s.Trim() + "\n";
    }
}
