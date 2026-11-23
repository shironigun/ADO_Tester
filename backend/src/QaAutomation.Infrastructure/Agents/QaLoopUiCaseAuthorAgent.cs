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
        IReadOnlyList<LinkedTestCase>? existingSuiteCases = null,
        StreamProgress? progress = null,
        Action<LlmEmit>? emit = null,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(module, analysis, additionalContext, existingSuiteCases);
        var data = await JsonUtils.CompleteJsonAsync(
            llm, AgentPrompts.QaLoopUiCaseAuthorSystem, userPrompt,
            opener: '[', progress: progress, emit: emit,
            label: "QaLoopUiCaseAuthor", cancellationToken: ct);

        if (data is not JsonArray arr)
            throw new AgentException("QA Loop UI case author did not return a JSON array.");

        return ParseCases(module, arr);
    }

    private static string BuildPrompt(
        string module, QaLoopUiAnalystOutput analysis, string? additionalContext,
        IReadOnlyList<LinkedTestCase>? existingSuiteCases)
    {
        var sb = new StringBuilder();
        sb.Append($"Module: {module}\n\n");
        sb.Append("## Writing for Playwright agents\n");
        sb.Append("Each step must be ONE click / ONE field / ONE navigation with its own expected result.\n");
        sb.Append("Never bundle actions with 'and' or 'then'. Expand every UI move so an automation agent can follow the list blindly.\n\n");
        sb.Append("## Mandatory bootstrap (first three steps of EVERY case)\n");
        sb.Append($"1. Login to {ProductName}\n");
        sb.Append("2. Close the email registration popup if it appears\n");
        sb.Append($"3. Click on {module} module in the side menu\n");
        sb.Append("Then continue with atomic feature steps (every click thereafter is its own step).\n\n");
        sb.Append("## UI Analysis\n");
        sb.Append(JsonSerializer.Serialize(analysis, JsonOpts));
        if (existingSuiteCases is { Count: > 0 })
        {
            sb.Append("\n\n## Existing ADO / Plan-imported cases (do NOT duplicate titles; fill gaps only)\n");
            foreach (var c in existingSuiteCases.Take(60))
                sb.AppendLine($"- ado#{c.Id} steps={c.Steps.Count} · {c.Title}");
        }
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
            var stepsJson = EnsureBootstrapSteps(module, obj["steps"]?.ToJsonString() ?? "[]");
            results.Add(new UiCaseDraft(title, preconditions, stepsJson, bucket));
        }
        return results;
    }

    /// <summary>
    /// Guarantees every case starts with Login → dismiss email popup → open module,
    /// then the remaining feature steps (bootstrap-like leading steps are de-duped).
    /// </summary>
    internal static string EnsureBootstrapSteps(string module, string stepsJson)
    {
        var feature = new List<(string Action, string Expected)>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    var action = s.TryGetProperty("action", out var a) ? a.GetString() ?? ""
                        : s.TryGetProperty("Action", out var a2) ? a2.GetString() ?? "" : "";
                    var expected = s.TryGetProperty("expected", out var e) ? e.GetString() ?? ""
                        : s.TryGetProperty("Expected", out var e2) ? e2.GetString() ?? "" : "";
                    if (IsBootstrapStep(action, module)) continue;
                    feature.Add((action, expected));
                }
            }
        }
        catch
        {
            // Malformed JSON — still emit bootstrap + empty feature list.
        }

        var product = ProductName;
        var mod = string.IsNullOrWhiteSpace(module) ? "the target" : module.Trim();
        var all = new List<object>
        {
            new { action = $"Login to {product}", expected = "User is logged in and the main dashboard loads" },
            new
            {
                action = "Close the email registration popup if it appears",
                expected = "Popup is dismissed (or was not shown) and the dashboard remains usable",
            },
            new
            {
                action = $"Click on {mod} module in the side menu",
                expected = $"The {mod} module page loads",
            },
        };
        foreach (var (action, expected) in feature)
            all.Add(new { action, expected });

        return JsonSerializer.Serialize(all);
    }

    internal static bool IsBootstrapStep(string action, string module)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        var a = action.Trim().ToLowerInvariant();
        if (a.Contains("login to") || a.StartsWith("login ") || a == "login") return true;
        if (a.Contains("email registration") || a.Contains("email login") || a.Contains("not now")) return true;
        var mod = (module ?? "").Trim().ToLowerInvariant();
        if (mod.Length > 0 && a.Contains(mod) &&
            (a.Contains("side menu") || a.Contains("navigate to") || a.Contains("open the") ||
             a.StartsWith("click on ") || a.StartsWith("click the ")))
            return true;
        return false;
    }

    private static string ProductName =>
        Environment.GetEnvironmentVariable("PROMPT_PRODUCT") is { Length: > 0 } p
            ? p
            : "the application";

    private static string NormaliseBucket(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "smoke" => "smoke",
        "e2e" => "e2e",
        _ => "regression",
    };
}
