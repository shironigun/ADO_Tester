using System.Text.Json;
using System.Text.Json.Serialization;

namespace QaAutomation.Core.Models;

/// <summary>
/// Tolerant <c>List&lt;string&gt;</c> converter. The LLM sometimes returns objects/numbers/bools
/// inside arrays meant to be string lists (e.g. <c>"crossFlow": [{ "module": "X", "concern": "Y" }]</c>).
/// We flatten non-string elements into a single readable line instead of throwing.
/// </summary>
public sealed class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<string>();
        if (reader.TokenType == JsonTokenType.Null) return list;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            return list;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected array, got {reader.TokenType}");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            list.Add(ReadOne(ref reader));
        }
        return list;
    }

    private static string ReadOne(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return reader.GetString() ?? string.Empty;
            case JsonTokenType.Number: return reader.GetDouble().ToString();
            case JsonTokenType.True: return "true";
            case JsonTokenType.False: return "false";
            case JsonTokenType.Null: return string.Empty;
            case JsonTokenType.StartObject:
            {
                // Flatten an object to "key: value; key: value"
                using var doc = JsonDocument.ParseValue(ref reader);
                var parts = new List<string>();
                foreach (var p in doc.RootElement.EnumerateObject())
                    parts.Add($"{p.Name}: {p.Value.ToString()}");
                return string.Join("; ", parts);
            }
            case JsonTokenType.StartArray:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.ToString();
            }
            default: return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
