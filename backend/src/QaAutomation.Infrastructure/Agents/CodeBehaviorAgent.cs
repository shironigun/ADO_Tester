using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Code-behavior agent — reads the (already byte-budgeted) discovered repo code and distills what the
/// implementation ACTUALLY does into a short list of plain-English behavior bullets for the reviewer to
/// annotate before the Tester runs. This is a new feature (not a parity-frozen agent), so the system
/// prompt lives inline. Output is a JSON array of <c>{ "behavior": "…" }</c>; the orchestrator assigns
/// ids and empty comment slots. The code is capped again here (belt-and-suspenders) so the call stays cheap.
/// </summary>
public sealed class CodeBehaviorAgent
{
    private const int MaxCodeChars = 60_000;

    private const string SystemPrompt =
        "You are a senior engineer reviewing an implementation to brief a QA analyst. You are given code " +
        "that was discovered for a specific work item (changed files from linked PRs/commits plus related " +
        "files). Read it and explain, in plain English, what the implementation ACTUALLY does — the " +
        "observable behaviors, validation rules, side effects, default values, and edge/boundary handling " +
        "that a tester would need to know.\n\n" +
        "Rules:\n" +
        "- Output ONLY a JSON array. Each element is an object: {\"behavior\": \"<one concrete behavior>\"}.\n" +
        "- One behavior per bullet, concrete and testable (not vague summaries like \"handles the request\").\n" +
        "- Prefer 5–12 bullets. Focus on behavior relevant to testing this work item; ignore boilerplate.\n" +
        "- Describe what the code does today, not what it should do. Do NOT invent behavior not in the code.\n" +
        "- No markdown, no prose, no code fences — just the JSON array.";

    private readonly ILlmClient _llm;

    public CodeBehaviorAgent(ILlmClient llm) => _llm = llm;

    /// <summary>Analyze the discovered code → list of behavior bullets (may be empty). Never throws on an
    /// empty result; the caller decides what to do when nothing meaningful is produced.</summary>
    public async Task<List<string>> AnalyzeAsync(
        AdoWorkItem workItem, string codeContext,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var code = codeContext.Length > MaxCodeChars ? codeContext[..MaxCodeChars] : codeContext;
        var prompt =
            $"## Work Item\n**Title:** {workItem.Title}\n\n" +
            "## Discovered code\n" +
            "```\n" + code + "\n```\n\n" +
            "Return the JSON array of behavior bullets now.";

        var data = await JsonUtils.CompleteJsonAsync(
            _llm, SystemPrompt, prompt, '[', progress, emit, label: "Code behavior", cancellationToken: cancellationToken);
        if (data is not JsonArray arr)
            return [];

        var bullets = new List<string>();
        foreach (var item in arr)
        {
            var text = item switch
            {
                JsonObject o => JsonUtils.StrOr(o, "behavior", "Behavior"),
                JsonValue v => v.ToString(),
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(text))
                bullets.Add(text.Trim());
        }
        return bullets;
    }
}
