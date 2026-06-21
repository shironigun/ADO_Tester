using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QaAutomation.Core.Agents;

namespace QaAutomation.Core.Models;

/// <summary>
/// Structured expected-behavior analysis produced by the Analyst agent. <see cref="ToJson"/> emits the
/// frozen camelCase shape stored as <c>AnalysisJson</c> (consumed by the UI); <see cref="FromJson"/> is
/// dual-case tolerant + coercing, mirroring the Python <c>AnalystOutput.from_dict</c> (spec §5.7).
/// </summary>
public sealed class AnalystOutput
{
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("feature")] public string Feature { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("happyPath")] public string HappyPath { get; set; } = "";
    [JsonPropertyName("acceptanceCriteria")] public List<string> AcceptanceCriteria { get; set; } = [];
    [JsonPropertyName("businessRules")] public List<string> BusinessRules { get; set; } = [];
    [JsonPropertyName("edgeCases")] public List<string> EdgeCases { get; set; } = [];
    [JsonPropertyName("negativeCases")] public List<string> NegativeCases { get; set; } = [];
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = [];
    [JsonPropertyName("crossFlow")] public List<string> CrossFlow { get; set; } = [];
    [JsonPropertyName("openQuestions")] public List<string> OpenQuestions { get; set; } = [];

    /// <summary>QA-supplied answers, index-aligned to <see cref="OpenQuestions"/> (empty = unanswered).
    /// Not produced by the Analyst — filled via the UI.</summary>
    [JsonPropertyName("questionAnswers")] public List<string> QuestionAnswers { get; set; } = [];

    /// <summary>Serialize to the frozen camelCase JSON (key order matches the Python <c>to_json</c>).</summary>
    public string ToJson()
    {
        static JsonArray Arr(List<string> xs)
        {
            var a = new JsonArray();
            foreach (var x in xs) a.Add(x);
            return a;
        }

        var obj = new JsonObject
        {
            ["module"] = Module,
            ["feature"] = Feature,
            ["summary"] = Summary,
            ["happyPath"] = HappyPath,
            ["acceptanceCriteria"] = Arr(AcceptanceCriteria),
            ["businessRules"] = Arr(BusinessRules),
            ["edgeCases"] = Arr(EdgeCases),
            ["negativeCases"] = Arr(NegativeCases),
            ["permissions"] = Arr(Permissions),
            ["crossFlow"] = Arr(CrossFlow),
            ["openQuestions"] = Arr(OpenQuestions),
            ["questionAnswers"] = Arr(QuestionAnswers),
        };
        return obj.ToJsonString();
    }

    /// <summary>Parse from a JSON object, accepting any key casing (camelCase or snake_case) and
    /// coercing odd value shapes — mirrors the Python <c>from_dict</c>.</summary>
    public static AnalystOutput FromJson(JsonNode? node)
    {
        var lower = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (node is JsonObject obj)
            foreach (var kvp in obj)
                lower[kvp.Key.ToLowerInvariant()] = kvp.Value;

        string Gets(string key) =>
            JsonUtils.CoerceString(lower.TryGetValue(key.ToLowerInvariant(), out var v) ? v : null);

        List<string> Getl(params string[] keys)
        {
            foreach (var k in keys)
                if (lower.TryGetValue(k.ToLowerInvariant(), out var v))
                    return JsonUtils.CoerceStringList(v);
            return [];
        }

        return new AnalystOutput
        {
            Module = Gets("module"),
            Feature = Gets("feature"),
            Summary = Gets("summary"),
            HappyPath = Gets("happyPath"),
            AcceptanceCriteria = Getl("acceptanceCriteria", "acceptance_criteria"),
            BusinessRules = Getl("businessRules", "business_rules"),
            EdgeCases = Getl("edgeCases", "edge_cases"),
            NegativeCases = Getl("negativeCases", "negative_cases"),
            Permissions = Getl("permissions"),
            CrossFlow = Getl("crossFlow", "cross_flow"),
            OpenQuestions = Getl("openQuestions", "open_questions"),
            QuestionAnswers = Getl("questionAnswers", "question_answers"),
        };
    }
}
