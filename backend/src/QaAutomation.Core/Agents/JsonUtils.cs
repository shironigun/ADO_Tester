using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Core.Agents;

/// <summary>
/// Tolerant JSON helpers for agent output — faithful port of the Python <c>json_utils</c> (spec §5.7).
/// Handles markdown-fenced JSON, prose-wrapped JSON (string-aware bracket scan), case/shape drift, and
/// a 3-attempt retry with a strict-JSON nudge. Lives in Core because it depends only on the
/// <see cref="ILlmClient"/> abstraction.
/// </summary>
public static class JsonUtils
{
    /// <summary>Strip leading ``` fences and trailing closers. <paramref name="opener"/> is '{' for
    /// objects, '[' for arrays.</summary>
    public static string StripFences(string raw, char opener = '{')
    {
        var closer = opener == '{' ? '}' : ']';
        var s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var i = s.IndexOf(opener);
            var j = s.LastIndexOf(closer);
            if (i >= 0 && j > i)
                return s[i..(j + 1)];
        }
        return s;
    }

    /// <summary>Pull the JSON value out of a possibly-noisy response: strip fences, then scan for the
    /// first balanced, string-aware bracket structure. Returns "" if no opener is found, or the
    /// remainder from the opener if unbalanced (truncated).</summary>
    public static string ExtractJson(string raw, char opener = '{')
    {
        var closer = opener == '{' ? '}' : ']';
        var s = StripFences(raw, opener);
        var start = s.IndexOf(opener);
        if (start < 0)
            return "";
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') inStr = true;
            else if (ch == opener) depth++;
            else if (ch == closer)
            {
                depth--;
                if (depth == 0)
                    return s[start..(i + 1)];
            }
        }
        return s[start..]; // unbalanced (truncated) — let the parser decide
    }

    /// <summary>Call the LLM and parse JSON robustly: tolerate prose-wrapped JSON and retry on
    /// empty/invalid output with a strict-JSON nudge. Throws <see cref="AgentException"/> after all
    /// attempts. Returns the parsed value (a <see cref="JsonObject"/> or <see cref="JsonArray"/>).</summary>
    public static async Task<JsonNode> CompleteJsonAsync(
        ILlmClient llm, string systemPrompt, string userPrompt, char opener = '{',
        StreamProgress? progress = null, Action<LlmEmit>? emit = null,
        int attempts = 3, string label = "Agent", CancellationToken cancellationToken = default)
    {
        Exception? lastErr = null;
        var lastRaw = "";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var sysP = systemPrompt;
            if (attempt > 1)
                sysP = systemPrompt
                    + "\n\nIMPORTANT: Your previous response was not valid JSON. Respond with ONLY the "
                    + "JSON value — no prose, no markdown fences, no explanation. Start your reply with "
                    + $"`{opener}`.";

            lastRaw = await llm.CompleteAsync(sysP, userPrompt, model: null, progress, emit, cancellationToken);
            var candidate = ExtractJson(lastRaw, opener);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    var node = JsonNode.Parse(candidate);
                    if (node is not null)
                    {
                        // Force-build the backing dictionaries now so a duplicate-key payload fails HERE
                        // (→ graceful retry/AgentException) rather than as an unwrapped ArgumentException
                        // later when the agent reads a property.
                        Materialize(node);
                        return node;
                    }
                    lastErr = new InvalidOperationException("null JSON value");
                }
                catch (JsonException e) { lastErr = e; }
                catch (ArgumentException e) { lastErr = e; }   // duplicate object keys
            }
            else
            {
                lastErr = new InvalidOperationException("empty or non-JSON response");
            }
        }

        var snippet = (lastRaw ?? "").Trim();
        if (snippet.Length > 200) snippet = snippet[..200];
        snippet = snippet.Replace("\n", " ");
        throw new AgentException(
            $"{label} returned invalid JSON after {attempts} attempts: {lastErr?.Message}. Raw response head: '{snippet}'");
    }

    /// <summary>Recursively touch every object's properties so a JsonObject with duplicate keys throws
    /// its ArgumentException eagerly (inside the caller's try) instead of later during property access.</summary>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj)
                    Materialize(kvp.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    Materialize(item);
                break;
        }
    }

    /// <summary>Replace em/en dashes (and the horizontal bar) with a plain hyphen. House style: the
    /// generated artifacts must never contain "—"/"–" (LLMs emit them constantly).</summary>
    public static string StripEmDashes(string? s)
        => string.IsNullOrEmpty(s) ? (s ?? "") : s.Replace('—', '-').Replace('–', '-').Replace('―', '-');

    /// <summary>Flatten any JSON value into a single readable string (Python <c>coerce_string</c>).</summary>
    public static string CoerceString(JsonNode? value)
    {
        if (value is null)
            return "";
        switch (value.GetValueKind())
        {
            case JsonValueKind.Null:
                return "";
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return value.ToJsonString(); // numeric literal as written (e.g. "5", "5.5")
            case JsonValueKind.Object:
                var parts = ((JsonObject)value).Select(kvp => $"{kvp.Key}: {CoerceString(kvp.Value)}");
                return string.Join("; ", parts);
            case JsonValueKind.Array:
                return value.ToJsonString(); // ~ json.dumps(list)
            default:
                return value.ToString();
        }
    }

    /// <summary>First of <paramref name="keys"/> whose value coerces to a non-empty string (mirrors the
    /// Python <c>item.get("a") or item.get("A") or ""</c> idiom used in agent output parsing).</summary>
    public static string StrOr(JsonObject obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetPropertyValue(k, out var v) && v is not null)
            {
                var s = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        return "";
    }

    /// <summary>Coerce any JSON value into a list of strings (Python <c>coerce_string_list</c>).</summary>
    public static List<string> CoerceStringList(JsonNode? value)
    {
        if (value is null)
            return [];
        switch (value.GetValueKind())
        {
            case JsonValueKind.Null:
                return [];
            case JsonValueKind.String:
                var s = value.GetValue<string>();
                return string.IsNullOrWhiteSpace(s) ? [] : [s];
            case JsonValueKind.Array:
                var outList = new List<string>();
                foreach (var item in (JsonArray)value)
                    if (item is not null && item.GetValueKind() != JsonValueKind.Null)
                        outList.Add(CoerceString(item));
                return outList;
            default:
                return [CoerceString(value)];
        }
    }
}
