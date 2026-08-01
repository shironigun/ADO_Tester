using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// QA Loop UI Case Author — turns <see cref="QaLoopUiAnalystOutput"/> into bucket-tagged
/// manual UI test cases. Separate from the deprecated inventory-based <see cref="UiCaseAuthorAgent"/>.
/// Reuses <see cref="UiCaseDraft"/> (same shape / StepsJson format).
/// </summary>
internal sealed class QaLoopUiCaseAuthorAgent(ILlmClient llm)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<UiCaseDraft>> AuthorCasesAsync(
        string module,
        QaLoopUiAnalystOutput analysis,
        string? additionalContext = null,
        StreamProgress? progress = null,
        Action<LlmEmit>? emit = null,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, analysis, additionalContext);
        var data = await JsonUtils.CompleteJsonAsync(
            llm, AgentPrompts.QaLoopUiCaseAuthorSystem, userPrompt,
            opener: '[', progress: progress, emit: emit,
            label: "QaLoopUiCaseAuthor", cancellationToken: ct);

        if (data is not JsonArray arr)
            throw new AgentException("QA Loop UI case author did not return a JSON array.");

        return ParseCases(arr);
    }

    private static string BuildPrompt(string module, QaLoopUiAnalystOutput analysis, string? additionalContext)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n\n");
        sb.Append("## UI Analysis\n");
        sb.Append(JsonSerializer.Serialize(analysis, JsonOpts));
        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            sb.Append("\n\n## Additional Context (from tester)\n");
            sb.Append(additionalContext);
        }
        return sb.ToString();
    }

    private static List<UiCaseDraft> ParseCases(JsonArray arr)
    {
        var results = new List<UiCaseDraft>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var title = JsonUtils.StrOr(obj, "title").Trim();
            if (title.Length == 0) continue;
            var preconditions = JsonUtils.StrOr(obj, "preconditions").Trim();
            var bucket = NormaliseBucket(JsonUtils.StrOr(obj, "bucket"));
            var stepsJson = obj["steps"]?.ToJsonString() ?? "[]";
            results.Add(new UiCaseDraft(title, preconditions, stepsJson, bucket));
        }
        return results;
    }

    private static string NormaliseBucket(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "smoke" => "smoke",
        "e2e" => "e2e",
        _ => "regression",
    };
}
