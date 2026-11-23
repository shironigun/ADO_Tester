using System.Text.Json;
using System.Text.Json.Nodes;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// INT-302 — merge spec paths into <c>resources/registry.{unit}.json</c> without replacing
/// CRM-shaped <c>entries</c> objects.
/// </summary>
internal static class RegistryFileMerge
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static string Merge(string? existingJson, string unitSlug, string rootRelativeSpec)
    {
        var normalized = rootRelativeSpec.Replace('\\', '/');
        var root = ParseRoot(existingJson);
        if (root["unit"] is null)
            root["unit"] = unitSlug;

        MergeSpecs(root, normalized);

        var field = InferField(normalized);
        if (field is not null)
            MergeEntry(root, InferEntryKey(normalized), field, normalized);

        return root.ToJsonString(WriteOpts);
    }

    private static JsonObject ParseRoot(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return new JsonObject();
        try
        {
            return JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void MergeSpecs(JsonObject root, string normalized)
    {
        var specs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root["specs"] is JsonArray arr)
        {
            foreach (var e in arr)
            {
                if (e?.GetValue<string>() is { Length: > 0 } s)
                    specs.Add(s);
            }
        }
        specs.Add(normalized);
        root["specs"] = new JsonArray(specs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => (JsonNode)x).ToArray());
    }

    private static void MergeEntry(JsonObject root, string entryKey, string fieldName, string normalized)
    {
        if (root["entries"] is not JsonObject entries)
        {
            entries = new JsonObject();
            root["entries"] = entries;
        }

        if (entries[entryKey] is not JsonObject entry)
        {
            entry = new JsonObject();
            entries[entryKey] = entry;
        }

        entry[fieldName] = normalized;
    }

    internal static string InferEntryKey(string rootRelativeSpec)
    {
        var file = Path.GetFileName(rootRelativeSpec);
        if (file.EndsWith(".schema.ts", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
        if (file.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(file);
        return Path.GetFileNameWithoutExtension(file);
    }

    internal static string? InferField(string rootRelativeSpec)
    {
        var p = rootRelativeSpec.Replace('\\', '/');
        if (p.Contains("/api/schemas/", StringComparison.OrdinalIgnoreCase)) return "schemaFile";
        if (p.Contains("/api/tests/contracts/", StringComparison.OrdinalIgnoreCase)) return "contractTest";
        if (p.Contains("/api/tests/endpoints/", StringComparison.OrdinalIgnoreCase)) return "endpointTest";
        if (p.Contains("/api/tests/workflows/", StringComparison.OrdinalIgnoreCase)) return "workflowTest";
        return null;
    }
}
