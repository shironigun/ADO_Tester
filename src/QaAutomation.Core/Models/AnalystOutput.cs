using System.Text.Json.Serialization;

namespace QaAutomation.Core.Models;

/// <summary>Structured expected-behavior analysis produced by the Analyst agent.</summary>
public sealed class AnalystOutput
{
    [JsonPropertyName("module")]            public string Module { get; set; } = string.Empty;
    [JsonPropertyName("feature")]           public string Feature { get; set; } = string.Empty;
    [JsonPropertyName("summary")]           public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("happyPath")]         public string HappyPath { get; set; } = string.Empty;

    [JsonPropertyName("acceptanceCriteria"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> AcceptanceCriteria { get; set; } = [];

    [JsonPropertyName("businessRules"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> BusinessRules { get; set; } = [];

    [JsonPropertyName("edgeCases"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> EdgeCases { get; set; } = [];

    [JsonPropertyName("negativeCases"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> NegativeCases { get; set; } = [];

    [JsonPropertyName("permissions"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("crossFlow"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> CrossFlow { get; set; } = [];

    [JsonPropertyName("openQuestions"), JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> OpenQuestions { get; set; } = [];
}
