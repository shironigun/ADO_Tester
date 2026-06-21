using System.Text.RegularExpressions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Pure helpers for the Find-Bugs code-analysis phase (spec B5). Ported verbatim from the Python
/// bug orchestrator: short Code-Search query building, TS/JS relative-import extraction + resolution,
/// severity parsing, and the single-verdict "actual behavior" summary.
/// </summary>
internal static partial class BugHelpers
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
        { "via", "with", "and", "the", "for", "a", "an", "of", "to", "in", "on", "by", "file", "guided" };

    private static readonly string[] ImportExts = [".ts", ".tsx", ".js", ".jsx", ".cs"];

    [GeneratedRegex("[A-Za-z][A-Za-z]+")]
    private static partial Regex WordRegex();

    // import/require specifiers in TS/JS source.
    [GeneratedRegex("""(?:from|import|require\()\s*['"]([^'"]+)['"]""")]
    private static partial Regex ImportRegex();

    /// <summary>Build a SHORT Code-Search query (ADO ANDs all terms, so a long title returns nothing):
    /// the first ~3 significant words of the first non-empty source.</summary>
    public static string SearchQuery(params string?[] sources)
    {
        var text = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        var words = WordRegex().Matches(text)
            .Select(m => m.Value)
            .Where(w => !Stopwords.Contains(w.ToLowerInvariant()))
            .Take(3);
        return string.Join(" ", words);
    }

    /// <summary>Relative ('.'-prefixed) import specifiers in TS/JS source — they resolve to repo files.</summary>
    public static List<string> ExtractRelativeImports(string? content)
        => ImportRegex().Matches(content ?? "")
            .Select(m => m.Groups[1].Value)
            .Where(s => s.StartsWith('.'))
            .ToList();

    /// <summary>Resolve a relative import specifier to candidate repo paths (extension/index guesses),
    /// mirroring the Python <c>posixpath</c>-based resolver.</summary>
    public static List<string> ResolveImport(string spec, string fromPath)
    {
        var baseDir = fromPath.Contains('/') ? fromPath[..fromPath.LastIndexOf('/')] : "";
        var target = PosixNormpath(PosixJoin(baseDir, spec));
        var last = target.Contains('/') ? target[(target.LastIndexOf('/') + 1)..] : target;
        if (last.Contains('.'))   // already has an extension
            return [target];
        var result = ImportExts.Select(e => target + e).ToList();
        result.AddRange(ImportExts.Take(4).Select(e => $"{target}/index{e}"));
        return result;
    }

    public static BugSeverity ParseSeverity(string? value)
    {
        var low = (value ?? "").Trim().ToLowerInvariant();
        if (low.Contains("crit") || low == "1") return BugSeverity.Critical;
        if (low.Contains("high") || low == "2") return BugSeverity.High;
        if (low.Contains("low") || low == "4") return BugSeverity.Low;
        return BugSeverity.Medium;
    }

    /// <summary>Compose the "observed actual behavior" signal handed to the Bug Writer for a single
    /// failed test case (bug-from-verdict agent mode).</summary>
    public static string VerdictActualSummary(string title, IReadOnlyList<string> repro, string expected, string actual)
    {
        var parts = new List<string> { $"A verified test case failed: {title}." };
        if (repro.Count > 0)
            parts.Add("Steps:\n" + string.Join("\n", repro.Select(r => $"- {r}")));
        if (expected.Length > 0)
            parts.Add($"Expected: {expected}");
        if (actual.Length > 0)
            parts.Add($"Observed (from static code verification): {actual}");
        return string.Join("\n\n", parts);
    }

    // ── posix path helpers ──────────────────────────────────────────────────

    private static string PosixJoin(string baseDir, string spec)
        => baseDir.Length == 0 ? spec : $"{baseDir}/{spec}";

    /// <summary>Minimal <c>posixpath.normpath</c>: collapse '.', '..', and empty segments.</summary>
    private static string PosixNormpath(string path)
    {
        if (path.Length == 0)
            return ".";
        var absolute = path.StartsWith('/');
        var stack = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                    stack.RemoveAt(stack.Count - 1);
                else if (!absolute)
                    stack.Add("..");
            }
            else
            {
                stack.Add(part);
            }
        }
        var result = string.Join("/", stack);
        if (absolute)
            result = "/" + result;
        return result.Length == 0 ? (absolute ? "/" : ".") : result;
    }
}
