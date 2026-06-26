using QaAutomation.Core.Abstractions;

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
