using System.Text.Json;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>
/// Conditioned on Mahmood Ahmad's QA format and voice rules.
/// Takes AnalystOutput → produces a list of TestCaseDraft-ready objects.
/// </summary>
public sealed class TesterAgent
{
    private const string SystemPrompt =
        """
        You are the Tester agent — a senior SQA analyst who writes test cases exactly in
        Mahmood Ahmad's QA format for YourProduct / YourOrg. Follow every rule below.

        ## TITLE FORMAT
        [Module] - [Feature] - Verify that <specific observable behavior>.
        Third segment ALWAYS starts with "Verify that".
        Examples:
        - Deals - Pipeline - Verify that user is able to switch to the newly created pipeline.
        - Settings - Roles - Verify that a user without the Admin role cannot access pipeline settings.

        ## STEP FORMAT (JSON)
        Each test case has a "preconditions" string and a "steps" array of {action, expected} objects.
        - action: one single atomic action. Imperative, third-person: "Click the SAVE button."
          NEVER collapse two actions into one step.
        - expected: exactly what the UI shows/changes in response to that one action. Definitive.
          "The SAVE button is disabled" NOT "The button should be disabled."
        - Last step MUST be: "Verify that <behavior from title>" with the definitive outcome.
        - No step may have an empty expected result.

        ## VOICE RULES (ALL MANDATORY)
        - Childlike granularity: every click, navigation, and field entry = its own step.
          "Navigate to Settings → Deals tab → Add Pipeline" = 3 steps, not 1.
        - Imperative third-person: "Login to YourProduct" not "The user should log in."
        - Exact UI labels verbatim from the story. "Connect to Customer button" not "the link."
        - One assertion per step.
        - No greetings, no intro paragraphs, no section headers, no emojis, no "Note:", no "✅".
        - No hedging. Results are definitive.

        ## COVERAGE: produce cases for ALL applicable axes
        - Happy path (always first)
        - Each acceptance criterion: pass case + enforcement/boundary case
        - Each business rule: positive + negative
        - Edge cases (empty, max-length, boundary values, missing/deleted data)
        - Negative cases (invalid input, blocked actions)
        - Permission/role cases if relevant
        - Cross-flow regression guards if other modules are touched

        ## OUTPUT FORMAT
        Return ONLY a JSON array of test case objects. No markdown, no explanation.
        Schema:
        [
          {
            "title": "string",
            "preconditions": "string",
            "steps": [{"action": "string", "expected": "string"}]
          }
        ]
        """;

    private readonly ILlmClient _llm;

    public TesterAgent(ILlmClient llm) => _llm = llm;

    public async Task<List<GeneratedTestCase>> GenerateAsync(
        AnalystOutput analysis,
        AdoWorkItem workItem,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(analysis, workItem);
        var json = await _llm.CompleteAsync(SystemPrompt, userPrompt, cancellationToken: ct);

        var cleaned = json.Trim();
        if (cleaned.StartsWith("```"))
        {
            var start = cleaned.IndexOf('[');
            var end = cleaned.LastIndexOf(']') + 1;
            if (start >= 0 && end > start) cleaned = cleaned[start..end];
        }

        return JsonSerializer.Deserialize<List<GeneratedTestCase>>(cleaned, JsonDefaults.Lenient)
            ?? throw new InvalidOperationException("Tester returned invalid JSON.");
    }

    private static string BuildPrompt(AnalystOutput a, AdoWorkItem wi)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Work item #{wi.Id}: {wi.Title}");
        sb.AppendLine($"\nModule: {a.Module}");
        sb.AppendLine($"Feature: {a.Feature}");
        sb.AppendLine($"\nSummary: {a.Summary}");
        sb.AppendLine($"\nHappy path: {a.HappyPath}");

        void List(string label, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine($"\n{label}:");
            foreach (var i in items) sb.AppendLine($"- {i}");
        }
        List("Acceptance criteria", a.AcceptanceCriteria);
        List("Business rules", a.BusinessRules);
        List("Edge cases", a.EdgeCases);
        List("Negative cases", a.NegativeCases);
        List("Permissions", a.Permissions);
        List("Cross-flow", a.CrossFlow);
        if (a.OpenQuestions.Count > 0)
        {
            sb.AppendLine("\nOpen questions (use your best judgment):");
            foreach (var q in a.OpenQuestions) sb.AppendLine($"- {q}");
        }

        sb.AppendLine("\nGenerate the full test case array now.");
        return sb.ToString();
    }
}

/// <summary>Deserialization target for the Tester agent's JSON output.</summary>
public sealed class GeneratedTestCase
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("preconditions")]
    public string Preconditions { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("steps")]
    public List<TestCaseStep> Steps { get; set; } = [];
}
