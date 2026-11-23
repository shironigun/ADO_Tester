using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.TextSearch;

/// <summary>Shared FTS tokenizer helpers for PostgreSQL <c>to_tsquery</c>.</summary>
internal static partial class FtsMatch
{
    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex TokenRegex();

    /// <summary>Shared tokenizer: <c>[A-Za-z0-9_]+</c> tokens of length &gt; 1, de-duped in order, capped at 20.</summary>
    private static List<string> ExtractTokens(string query)
    {
        var seen = new HashSet<string>();
        var unique = new List<string>();
        foreach (Match m in TokenRegex().Matches(query))
        {
            var token = m.Value;
            if (token.Length <= 1)
                continue;
            if (seen.Add(token))
                unique.Add(token);
            if (unique.Count >= 20)
                break;
        }
        return unique;
    }

    /// <summary>
    /// Builds a PostgreSQL <c>to_tsquery('english', …)</c> expression from tokens, OR-joined
    /// with <c>' | '</c>. Null when the query has no usable token.
    /// </summary>
    public static string? PostgresOrTsQuery(string query)
    {
        var unique = ExtractTokens(query);
        return unique.Count == 0 ? null : string.Join(" | ", unique);
    }
}
