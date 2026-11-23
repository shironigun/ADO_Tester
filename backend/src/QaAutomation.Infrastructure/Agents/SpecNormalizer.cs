using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Deterministic post-processing of a generated Playwright spec so auth + base URL are ALWAYS wired
/// the way the runner injects them, regardless of LLM drift.
/// <list type="number">
///   <item><b>Product mode</b> — rewrites <c>@playwright/test</c> imports to
///     <c>common/fixtures/api.fixture</c> and strips legacy env-token wiring.</item>
///   <item><b>Legacy mode</b> — fixes wrong base-URL/token env vars and spurious <c>/api/</c> prefix.</item>
/// </list>
/// Idempotent: specs already using the correct names/routes pass through unchanged.
/// </summary>
public static class SpecNormalizer
{
    private const string ApiFixtureImport =
        "import { test, expect } from '../../../../../common/fixtures/api.fixture'";

    // Legacy LEAD_API_BASE_URL [?? '...']  →  AUTOMATION_BASE_URL ?? ''
    private static readonly Regex BaseUrlEnv = new(
        @"process\.env\.LEAD_API_BASE_URL(\s*\?\?\s*'[^']*')?", RegexOptions.Compiled);

    // Legacy LEAD_API_TOKEN  →  AUTOMATION_BEARER_TOKEN
    private static readonly Regex TokenEnv = new(
        @"process\.env\.LEAD_API_TOKEN", RegexOptions.Compiled);

    // ${SomeBaseVar}/api/  →  ${SomeBaseVar}/   (strip the spurious /api/ segment after the base URL)
    private static readonly Regex ApiPrefix = new(
        @"(\$\{[A-Za-z_][A-Za-z0-9_]*\})/api/", RegexOptions.Compiled);

    private static readonly Regex PlaywrightTestImport = new(
        @"import\s*\{\s*test\s*,\s*expect\s*\}\s*from\s*'@playwright/test'\s*;?\s*\n?",
        RegexOptions.Compiled);

    private static readonly Regex PlaywrightRequestImport = new(
        @"import\s*\{\s*request\s*\}\s*from\s*'@playwright/test'\s*;?\s*\n?",
        RegexOptions.Compiled);

    private static readonly Regex AutomationBearerConst = new(
        @"^const\s+TOKEN\s*=\s*process\.env\.AUTOMATION_BEARER_TOKEN[^;]*;\s*\n?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AutomationBaseConst = new(
        @"^const\s+BASE(?:_URL)?\s*=\s*process\.env\.AUTOMATION_BASE_URL[^;]*;\s*\n?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static string Normalize(string? specCode, bool isProductMode = false) =>
        isProductMode ? NormalizeProduct(specCode) : NormalizeLegacy(specCode);

    private static string NormalizeLegacy(string? specCode)
    {
        if (string.IsNullOrEmpty(specCode)) return specCode ?? string.Empty;
        var s = BaseUrlEnv.Replace(specCode, "process.env.AUTOMATION_BASE_URL ?? ''");
        s = TokenEnv.Replace(s, "process.env.AUTOMATION_BEARER_TOKEN");
        s = ApiPrefix.Replace(s, "$1/");
        return s;
    }

    private static string NormalizeProduct(string? specCode)
    {
        if (string.IsNullOrEmpty(specCode)) return specCode ?? string.Empty;
        var s = PlaywrightTestImport.Replace(specCode, ApiFixtureImport + "\n");
        s = PlaywrightRequestImport.Replace(s, "");
        s = AutomationBearerConst.Replace(s, "");
        s = AutomationBaseConst.Replace(s, "");
        s = BaseUrlEnv.Replace(s, "");
        s = TokenEnv.Replace(s, "");
        s = ApiPrefix.Replace(s, "$1/");
        // Ensure api.fixture import when LLM omitted imports entirely but used test().
        if (!s.Contains("common/fixtures/api.fixture", StringComparison.Ordinal)
            && s.Contains("test(", StringComparison.Ordinal))
        {
            s = ApiFixtureImport + ";\n\n" + s.TrimStart();
        }
        return s;
    }
}
