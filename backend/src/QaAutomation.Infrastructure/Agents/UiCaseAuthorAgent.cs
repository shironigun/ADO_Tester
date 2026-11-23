using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>Output record produced by <see cref="UiCaseAuthorAgent"/>.</summary>
public sealed record UiCaseDraft(
    string Title,
    string Preconditions,
    string StepsJson,
    string Bucket);

/// <summary>
/// Generates manual UI test cases (bucket-tagged) for a module from its inventory.
/// Called during the QA Loop UI authoring phase (QLV2-203).
/// </summary>
internal sealed class UiCaseAuthorAgent(ILlmClient llm)
{
    public async Task<List<UiCaseDraft>> AuthorCasesAsync(
        string module,
        InventoryDoc inventory,
        string? additionalContext = null,
        StreamProgress? progress = null,
        Action<LlmEmit>? emit = null,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, inventory, additionalContext);
        var data = await JsonUtils.CompleteJsonAsync(
            llm, AgentPrompts.UiCaseAuthorSystem, userPrompt,
            opener: '[', progress: progress, emit: emit,
            label: "UiCaseAuthor", cancellationToken: ct);

        if (data is not JsonArray arr)
            throw new AgentException("UI case author did not return a JSON array.");

        return ParseCases(module, arr);
    }

    private static string BuildPrompt(string module, InventoryDoc inventory, string? additionalContext)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n\n");
        sb.Append("## Mandatory bootstrap (include as the first three steps of EVERY case)\n");
        sb.Append("1. Login to the application\n");
        sb.Append("2. Close the email registration popup if it appears\n");
        sb.Append($"3. Click on {module} module in the side menu\n\n");
        sb.Append("## Inventory\n");
        sb.Append(JsonSerializer.Serialize(inventory));
        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            sb.Append("\n\n## Additional Context (from tester)\n");
            sb.Append(additionalContext);
        }
        return sb.ToString();
    }

    private static List<UiCaseDraft> ParseCases(string module, JsonArray arr)
    {
        var results = new List<UiCaseDraft>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var title = JsonUtils.StrOr(obj, "title").Trim();
            if (title.Length == 0) continue;
            var preconditions = JsonUtils.StrOr(obj, "preconditions").Trim();
            var bucket = NormaliseBucket(JsonUtils.StrOr(obj, "bucket"));
            var stepsJson = QaLoopUiCaseAuthorAgent.EnsureBootstrapSteps(module, obj["steps"]?.ToJsonString() ?? "[]");
            results.Add(new UiCaseDraft(title, preconditions, stepsJson, bucket));
        }
        return results;
    }

    private static string NormaliseBucket(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "smoke"      => "smoke",
        "e2e"        => "e2e",
        _            => "regression",
    };
}
