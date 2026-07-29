using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.TextSearch;

/// <summary>Shared SQLite FTS5 match-expression builder. Extracted from KnowledgebaseService /
/// ExistingTestsIndexService so the tokenizer lives in one place; behavior is byte-identical to the
/// original (tokens &gt; 1 char, de-duped in order, capped at 20, quoted and OR-joined).</summary>
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

    /// <summary>Builds the SQLite FTS5 MATCH expression, or null when the query has no usable token.</summary>
    public static string? SqliteMatchExpression(string query)
    {
        var unique = ExtractTokens(query);
        return unique.Count == 0 ? null : string.Join(" OR ", unique.Select(t => $"\"{t}\""));
    }

    /// <summary>
    /// Builds a PostgreSQL <c>to_tsquery('english', …)</c> expression from the same tokens, OR-joined
    /// with <c>' | '</c>, so retrieval is broad match-any — mirroring the SQLite OR behavior. This is
    /// what keeps the pipeline's long "title + acceptance criteria" query from AND-narrowing to zero
    /// hits (which <c>websearch_to_tsquery</c> would). Tokens are <c>[A-Za-z0-9_]+</c> so the result is
    /// always valid tsquery syntax (no operator chars to escape). Null when the query has no usable token.
    /// </summary>
    public static string? PostgresOrTsQuery(string query)
    {
        var unique = ExtractTokens(query);
        return unique.Count == 0 ? null : string.Join(" | ", unique);
    }
}
