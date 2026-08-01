using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QaAutomation.Core.Models;

/// <summary>Identity payload baked as a <c>// @qa-meta {...}</c> comment on line 1 of every generated
/// spec file (Scripts Board, Slab 3 QLV2-301), so the board can reconcile disk↔DB without relying solely
/// on the DB row surviving. <see cref="Type"/> is <c>"api"</c> or <c>"ui"</c>; <see cref="Level"/> is the
/// wire-string test level (<c>contract|endpoint|workflow|ui</c>); <see cref="Bucket"/> is UI-only
/// (<c>smoke|regression|e2e</c>). <see cref="Tags"/> holds pack/feature tokens extracted from the first
/// <c>test(</c> title (optional — omitted from JSON when null so legacy headers stay byte-stable).</summary>
public sealed record QaMeta(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("bucket")] string? Bucket,
    [property: JsonPropertyName("tcId")] int? TcId,
    [property: JsonPropertyName("sourceDraftId")] int? SourceDraftId,
    [property: JsonPropertyName("generatedAt")] DateTime GeneratedAt,
    [property: JsonPropertyName("specHash")] string SpecHash,
    [property: JsonPropertyName("tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Tags = null);

/// <summary>Build/parse/upsert helpers for the <c>@qa-meta</c> header line. The header is always line 1
/// of the file (before any <c>import</c>) — <c>SpecNormalizer</c> only rewrites env/route strings and
/// ignores comments, so this survives every downstream rewrite untouched.</summary>
public static class QaMetaHeader
{
    private const string Prefix = "// @qa-meta ";

    private static readonly Regex TagToken =
        new(@"@[a-z][a-z0-9\-]+", RegexOptions.Compiled);

    public static string Build(QaMeta meta) => Prefix + JsonSerializer.Serialize(meta) + "\n";

    /// <summary>Returns null for specs with no header (legacy specs from before QLV2-301, or a
    /// hand-added spec) — never throws.</summary>
    public static QaMeta? TryParse(string specCode)
    {
        var firstLine = FirstLine(specCode);
        if (firstLine is null || !firstLine.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<QaMeta>(firstLine[Prefix.Length..]); }
        catch { return null; }
    }

    /// <summary>Returns the spec with any existing header line removed (used to compute a stable hash
    /// over the body only, so re-stamping the header never changes the hash of unrelated body edits).</summary>
    public static string StripHeader(string specCode)
    {
        var firstLine = FirstLine(specCode);
        if (firstLine is null || !firstLine.StartsWith(Prefix, StringComparison.Ordinal)) return specCode;
        var idx = specCode.IndexOf('\n');
        return idx < 0 ? "" : specCode[(idx + 1)..];
    }

    /// <summary>Idempotent: replaces an existing header line in place, else prepends one. Never produces
    /// more than one header line.</summary>
    public static string Upsert(string specCode, QaMeta meta) => Build(meta) + StripHeader(specCode);

    public static string ComputeHash(string specCodeBody) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specCodeBody)))[..12];

    /// <summary>Extracts <c>@[a-z][a-z0-9\-]+</c> tokens from the first line containing <c>test(</c>
    /// (typically the Playwright test title). Skips import lines so <c>@playwright/test</c> is never
    /// captured. Deduplicates while preserving order. Never throws.</summary>
    public static IReadOnlyList<string> ExtractTags(string specCode)
    {
        if (string.IsNullOrEmpty(specCode)) return [];
        foreach (var raw in specCode.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.IndexOf("test(", StringComparison.Ordinal) < 0) continue;
            var matches = TagToken.Matches(line);
            if (matches.Count == 0) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tags = new List<string>(matches.Count);
            foreach (Match m in matches)
                if (seen.Add(m.Value)) tags.Add(m.Value);
            return tags;
        }
        return [];
    }

    private static string? FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var idx = s.IndexOf('\n');
        return idx < 0 ? s : s[..idx];
    }
}
