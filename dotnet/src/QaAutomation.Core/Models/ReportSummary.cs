using System.Text.Json;
using System.Text.Json.Nodes;

namespace QaAutomation.Core.Models;

/// <summary>A cluster of tickets the summarizer believes share a theme.</summary>
public sealed class ThemeCluster
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<int> TicketIds { get; set; } = [];
}

/// <summary>Structured AI insights consumed by the report HTML builder (spec B7). All fields optional —
/// the builder degrades gracefully. <see cref="ToJson"/>/<see cref="FromJson"/> mirror the Python.</summary>
public sealed class ReportSummary
{
    public List<string> Tldr { get; set; } = [];
    public string Headline { get; set; } = "";
    public List<ThemeCluster> Themes { get; set; } = [];
    public Dictionary<string, string> SpeakerScript { get; set; } = new();
    public List<string> Risks { get; set; } = [];
    public List<string> Wins { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(new
    {
        tldr = Tldr,
        headline = Headline,
        themes = Themes.Select(t => new { name = t.Name, description = t.Description, ticketIds = t.TicketIds }),
        speakerScript = SpeakerScript,
        risks = Risks,
        wins = Wins,
    });

    public static ReportSummary FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ReportSummary();
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { return new ReportSummary(); }
        if (root is not JsonObject obj)
            return new ReportSummary();

        List<string> StrList(string key) =>
            (obj[key] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? [];

        var summary = new ReportSummary
        {
            Tldr = StrList("tldr"),
            Headline = obj["headline"]?.ToString() ?? "",
            Risks = StrList("risks"),
            Wins = StrList("wins"),
        };
        if (obj["speakerScript"] is JsonObject script)
            foreach (var kvp in script)
                summary.SpeakerScript[kvp.Key] = kvp.Value?.ToString() ?? "";
        if (obj["themes"] is JsonArray themes)
            foreach (var t in themes.OfType<JsonObject>())
            {
                var ids = (t["ticketIds"] ?? t["ticket_ids"]) as JsonArray;
                summary.Themes.Add(new ThemeCluster
                {
                    Name = t["name"]?.ToString() ?? "",
                    Description = t["description"]?.ToString() ?? "",
                    TicketIds = ids?.Select(i => i?.ToString() ?? "").Where(s => int.TryParse(s, out _)).Select(int.Parse).ToList() ?? [],
                });
            }
        return summary;
    }
}
