using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Deterministic post-processing of a generated Playwright spec so auth + base URL are ALWAYS wired
/// the way <c>PlaywrightRunner</c> injects them, regardless of LLM drift. Fixes three recurring bugs
/// seen in generated endpoint/workflow specs:
/// <list type="number">
///   <item>wrong base-URL env var — <c>process.env.LEAD_API_BASE_URL ?? 'https://localhost:7071'</c>
///     instead of <c>process.env.AUTOMATION_BASE_URL ?? ''</c>;</item>
///   <item>wrong token env var — <c>process.env.LEAD_API_TOKEN</c> (never set → empty bearer → 401)
///     instead of <c>process.env.AUTOMATION_BEARER_TOKEN</c>;</item>
///   <item>a spurious <c>/api/</c> route prefix — the real QA routes are e.g. <c>/lead/deal/...</c>,
///     NOT <c>/api/lead/deal/...</c> (the latter 404s at the APIM gateway).</item>
/// </list>
/// Idempotent: specs already using the correct names/routes pass through unchanged.
/// </summary>
public static class SpecNormalizer
{
    // process.env.LEAD_API_BASE_URL [?? '<anything>']  →  process.env.AUTOMATION_BASE_URL ?? ''
    private static readonly Regex BaseUrlEnv = new(
        @"process\.env\.LEAD_API_BASE_URL(\s*\?\?\s*'[^']*')?", RegexOptions.Compiled);

    // process.env.LEAD_API_TOKEN  →  process.env.AUTOMATION_BEARER_TOKEN
    private static readonly Regex TokenEnv = new(
        @"process\.env\.LEAD_API_TOKEN", RegexOptions.Compiled);

    // ${SomeBaseVar}/api/  →  ${SomeBaseVar}/   (strip the spurious /api/ segment after the base URL)
    private static readonly Regex ApiPrefix = new(
        @"(\$\{[A-Za-z_][A-Za-z0-9_]*\})/api/", RegexOptions.Compiled);

    public static string Normalize(string? specCode)
    {
        if (string.IsNullOrEmpty(specCode)) return specCode ?? string.Empty;
        var s = BaseUrlEnv.Replace(specCode, "process.env.AUTOMATION_BASE_URL ?? ''");
        s = TokenEnv.Replace(s, "process.env.AUTOMATION_BEARER_TOKEN");
        s = ApiPrefix.Replace(s, "$1/");
        return s;
    }
}
