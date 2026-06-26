using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// KB-extractor agent (Track A — hackathon KB foundation). Reads a module's source code + existing tests
/// and emits knowledgebase markdown — one document per flow, plus a module-level <c>rules.md</c> — that
/// grounds test generation. Unlike the ported agents, this is net-new, so its system prompt is authored
/// (not byte-frozen from Python) and safe to iterate on. Output is markdown, so it calls
/// <see cref="ILlmClient.CompleteAsync"/> directly rather than the JSON helper.
/// </summary>
public sealed class KbExtractorAgent
{
    private readonly ILlmClient _llm;

    public KbExtractorAgent(ILlmClient llm) => _llm = llm;

    /// <summary>Produce the KB document for a single flow. Per-flow calls keep each generation small,
    /// which sidesteps the Claude-CLI large-output failure that bites whole-module generations.</summary>
    public async Task<string> ExtractFlowAsync(
        string module, string flowName, string sourceBundle, IReadOnlyList<string> testCaseTitles,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildFlowPrompt(module, flowName, sourceBundle, testCaseTitles);
        var raw = await _llm.CompleteAsync(AgentPrompts.KbExtractorSystem, prompt, null, progress, emit, cancellationToken);
        return CleanMarkdown(raw);
    }

    /// <summary>Produce the module-level <c>rules.md</c> ("how to analyze" guide) for the covered flows.</summary>
    public async Task<string> ExtractRulesAsync(
        string module, IReadOnlyList<string> flowNames, string sourceBundle,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildRulesPrompt(module, flowNames, sourceBundle);
        var raw = await _llm.CompleteAsync(AgentPrompts.KbExtractorSystem, prompt, null, progress, emit, cancellationToken);
        return CleanMarkdown(raw);
    }

    internal static string BuildFlowPrompt(
        string module, string flowName, string sourceBundle, IReadOnlyList<string> testCaseTitles)
    {
        var lines = new List<string>
        {
            $"## Task: produce the FLOW document for the **{flowName}** flow in the **{module}** module.",
            "",
            "Use the exact `# Flow:` structure from your instructions. Ground every detail in the source below;",
            "where the source doesn't show something, write `TODO: confirm` instead of guessing.",
        };
        if (testCaseTitles.Count > 0)
        {
            lines.Add("");
            lines.Add("## Existing ADO test cases for this module (titles — align names and link ids where obvious):");
            foreach (var t in testCaseTitles)
                lines.Add($"- {t}");
        }
        lines.Add("");
        lines.Add("## Source code & existing tests");
        lines.Add(sourceBundle);
        return string.Join("\n", lines);
    }

    internal static string BuildRulesPrompt(string module, IReadOnlyList<string> flowNames, string sourceBundle)
    {
        var lines = new List<string>
        {
            $"## Task: produce the module-level **rules.md** for the **{module}** module.",
            "",
            $"Flows covered by this KB: {string.Join(", ", flowNames)}.",
            "Follow the RULES document guidance from your instructions. Be concrete and module-specific.",
            "",
            "## Source code & existing tests",
            sourceBundle,
        };
        return string.Join("\n", lines);
    }

    /// <summary>Strip an accidental wrapping ```/```markdown fence the model sometimes adds around the whole
    /// document, and normalize to a single trailing newline.</summary>
    internal static string CleanMarkdown(string raw)
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
        return JsonUtils.StripEmDashes(s.Trim()) + "\n";
    }
}
