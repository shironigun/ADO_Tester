namespace QaAutomation.Infrastructure.Common;

/// <summary>
/// Redacts secrets (PAT / API keys) from echoed error bodies before they bubble up, and truncates
/// long bodies. Byte-for-byte faithful to the Python <c>sanitize</c> (spec §5.5): only secrets with
/// length &gt; 6 are redacted to <c>***REDACTED***</c>; bodies over 2048 chars get a
/// <c>…[truncated]</c> suffix (U+2026); empty/null → <c>""</c>.
/// </summary>
public static class Sanitizer
{
    public const int MaxLength = 2048;

    public static string Sanitize(string? body, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(body))
            return "";

        var s = body;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length > 6)
                s = s.Replace(secret, "***REDACTED***");
        }

        if (s.Length > MaxLength)
            s = s[..MaxLength] + "…[truncated]";

        return s;
    }
}
