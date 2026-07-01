using System.Text.RegularExpressions;
using QaAutomation.Core.Models;

namespace QaAutomation.Core.Agents;

/// <summary>
/// Parses a React SPA route file (e.g. web-app/.../routes/Routes.tsx) into the product's frontend
/// modules — the strict source for the QA Loop "New Scan" module dropdown. Deterministic regex over the
/// two shapes the file actually uses: flat route-object literals ({ path: '/deals', component: Deals })
/// joined to their import statements (import Deals from 'components/deals/deals'). No LLM, no I/O.
/// </summary>
public static class FrontendModuleParser
{
    // import Foo from 'x';   OR   import { A, B as C } from 'x';
    private static readonly Regex ImportRx = new(
        @"import\s+(?:(?<default>\w+)|\{(?<named>[^}]+)\})\s+from\s+['""](?<path>[^'""]+)['""]",
        RegexOptions.Compiled);

    // Flat object literals (route objects have no nested braces).
    private static readonly Regex BlockRx = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    private static readonly Regex PathRx = new(@"path:\s*['""](?<p>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex CompRx = new(@"component:\s*(?<c>\w+)", RegexOptions.Compiled);

    /// <summary>Returns one entry per discovered module (deduped by module name, param routes skipped).</summary>
    public static IReadOnlyList<FrontendModule> Parse(string routesTsx, string frontendRoot, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(routesTsx)) return [];
        var root = frontendRoot.Trim('/');
        var b = (baseUrl ?? "").TrimEnd('/');

        // 1. component identifier → import module path
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in ImportRx.Matches(routesTsx))
        {
            var path = m.Groups["path"].Value;
            if (m.Groups["default"].Success)
                imports[m.Groups["default"].Value] = path;
            if (m.Groups["named"].Success)
                foreach (var raw in m.Groups["named"].Value.Split(','))
                {
                    var name = raw.Trim().Split(' ').Last().Trim();   // "X as Y" → Y
                    if (name.Length > 0) imports[name] = path;
                }
        }

        // 2. route objects → { routePath, componentName } → module folder
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FrontendModule>();
        foreach (Match block in BlockRx.Matches(routesTsx))
        {
            var pm = PathRx.Match(block.Value);
            var cm = CompRx.Match(block.Value);
            if (!pm.Success || !cm.Success) continue;

            var routePath = pm.Groups["p"].Value;
            if (routePath.Contains(':') || routePath.StartsWith("//") || routePath is "/" or "")
                continue;   // sub-routes / detail views / root are not modules

            if (!imports.TryGetValue(cm.Groups["c"].Value, out var importPath)) continue;

            // module = first path segment after "components/"
            var ix = importPath.IndexOf("components/", StringComparison.Ordinal);
            if (ix < 0) continue;
            var module = importPath[(ix + "components/".Length)..].Split('/')[0];
            if (module.Length == 0 || !seen.Add(module)) continue;

            result.Add(new FrontendModule(
                ModuleName: module,
                RoutePath: routePath,
                ComponentPath: $"{root}/components/{module}",
                Url: $"{b}{routePath}"));
        }
        return result;
    }
}
