using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Frontend-module discovery for the QA Loop "New Scan" dropdown. Order:
/// <list type="number">
/// <item>Cache (Settings row, keyed by repo|branch) — so the AI pass is one-time.</item>
/// <item>Conventional <c>{root}/routes/Routes.tsx</c> via the deterministic <see cref="FrontendModuleParser"/>.</item>
/// <item>AI fallback: list the repo's <c>.tsx/.ts</c> files, have the active LLM pick the route file(s)
/// (unless the filename heuristic already nails it), read them, and extract the module/route map.</item>
/// </list>
/// The blank-repo → default-repo fallback lives in <see cref="IAzureDevOpsClient"/>, so an unset
/// <c>QALOOP_FRONTEND_REPO</c> reads from the repository configured in Azure DevOps settings.
/// </summary>
public sealed class FrontendModuleService : IFrontendModuleService
{
    private const string CacheKeyPrefix = "qaloop.frontend_modules_cache";
    private const int MaxCandidateFiles = 5;
    private const int MaxCharsPerFile = 40_000;
    private const int MaxTotalChars = 120_000;
    private const int MaxListedPaths = 300;
    private const int StrongRouteScore = 60;

    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly QaDbContext _db;
    private readonly IOptionsMonitor<QaLoopOptions> _opts;
    private readonly ILogger<FrontendModuleService> _log;

    public FrontendModuleService(
        IAzureDevOpsClient ado, ILlmClientFactory llmFactory, QaDbContext db,
        IOptionsMonitor<QaLoopOptions> opts, ILogger<FrontendModuleService> log)
    {
        _ado = ado;
        _llmFactory = llmFactory;
        _db = db;
        _opts = opts;
        _log = log;
    }

    public async Task<IReadOnlyList<FrontendModule>> DiscoverAsync(
        string? repository = null, string? branch = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var o = _opts.CurrentValue;
        var repo = string.IsNullOrWhiteSpace(repository) ? o.FrontendRepo : repository;   // blank → default repo (ADO client)
        var root = (o.FrontendRoot ?? "").Trim('/');
        var baseUrl = o.FrontendBaseUrl ?? "";
        var cacheKey = $"{CacheKeyPrefix}:{(string.IsNullOrWhiteSpace(repo) ? "(default)" : repo.Trim())}:{(branch ?? "").Trim()}";

        if (!refresh)
        {
            var cached = await ReadCacheAsync(cacheKey, cancellationToken);
            if (cached is { Count: > 0 })
                return cached;
        }

        // 1. Deterministic parse of the conventional route file.
        var modules = await TryParseConventionalAsync(repo, branch, root, baseUrl, cancellationToken);

        // 2. AI fallback when the conventional file is missing / yields nothing.
        if (modules.Count == 0)
        {
            try { modules = await AiDiscoverAsync(repo, branch, root, baseUrl, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { _log.LogWarning(e, "AI frontend-module discovery failed"); }
        }

        if (modules.Count > 0)
            await WriteCacheAsync(cacheKey, modules, cancellationToken);
        return modules;
    }

    private async Task<IReadOnlyList<FrontendModule>> TryParseConventionalAsync(
        string? repo, string? branch, string root, string baseUrl, CancellationToken ct)
    {
        try
        {
            var path = string.IsNullOrEmpty(root) ? "routes/Routes.tsx" : $"{root}/routes/Routes.tsx";
            var content = await _ado.GetFileContentAsync(path, @ref: branch, repoId: repo, ct: ct);
            return FrontendModuleParser.Parse(content, root, baseUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log.LogInformation("Conventional routes parse unavailable ({Msg}) — trying AI discovery", e.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<FrontendModule>> AiDiscoverAsync(
        string? repo, string? branch, string root, string baseUrl, CancellationToken ct)
    {
        IReadOnlyList<string> files;
        try { files = await _ado.ListRepoFilePathsAsync(root, branch, repo, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _log.LogWarning(e, "Repo file listing failed for AI discovery"); return []; }

        var tsx = files
            .Where(p => p.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains(".test.", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tsx.Count == 0)
            return [];

        var ranked = tsx.OrderByDescending(RouteScore).ThenBy(p => p.Count(c => c == '/')).ToList();
        var llm = await _llmFactory.CreateAsync(ct);

        // Filename heuristic first; only ask the LLM to pick when nothing obviously looks like a route file.
        var picks = ranked.Where(p => RouteScore(p) >= StrongRouteScore).Take(MaxCandidateFiles).ToList();
        if (picks.Count == 0)
            picks = await LlmPickRouteFilesAsync(llm, ranked.Take(MaxListedPaths).ToList(), ct);
        if (picks.Count == 0)
            return [];

        var block = new StringBuilder();
        var total = 0;
        foreach (var p in picks.Take(MaxCandidateFiles))
        {
            string content;
            try { content = await _ado.GetFileContentAsync(p, @ref: branch, repoId: repo, ct: ct); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(content))
                continue;
            var slice = content.Length > MaxCharsPerFile ? content[..MaxCharsPerFile] : content;
            if (total + slice.Length > MaxTotalChars)
                break;
            total += slice.Length;
            block.Append("// FILE: ").Append(p).Append('\n').Append(slice).Append("\n\n");
        }
        if (block.Length == 0)
            return [];

        var modules = await LlmExtractModulesAsync(llm, block.ToString(), root, baseUrl, ct);
        // The LLM may return an import/file path (e.g. components/inventory/Inventory) although
        // FrontendPath is consumed as a directory by QA Loop. Resolve it against the branch tree on
        // every AI discovery pass, then persist the actual component directory.
        return NormalizeComponentPaths(modules, tsx);
    }

    private static IReadOnlyList<FrontendModule> NormalizeComponentPaths(
        IReadOnlyList<FrontendModule> modules, IReadOnlyList<string> branchSourcePaths)
    {
        return modules.Select(module =>
        {
            var requested = module.ComponentPath.Trim('/');
            var exact = branchSourcePaths.FirstOrDefault(path =>
                SameImportTarget(path, requested));

            if (exact is not null)
                return module with { ComponentPath = ParentDirectory(exact) };

            // If the model returned an import-style file path not present in the filtered list, remove
            // its terminal component filename rather than treating that file as a folder and appending /apis.
            var leaf = requested.LastIndexOf('/');
            if (leaf > 0 && LooksLikeComponentFile(requested[(leaf + 1)..]))
                return module with { ComponentPath = requested[..leaf] };

            return module;
        }).ToList();
    }

    private static bool SameImportTarget(string filePath, string importPath)
    {
        var file = StripExtension(filePath.Trim('/'));
        var target = StripExtension(importPath.Trim('/'));
        return string.Equals(file, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(file, $"{target}/index", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? path : path[..^ext.Length];
    }

    private static string ParentDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index > 0 ? path[..index] : path;
    }

    private static bool LooksLikeComponentFile(string name) =>
        name.EndsWith("page", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("view", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("component", StringComparison.OrdinalIgnoreCase)
        || char.IsUpper(name.FirstOrDefault());

    private static int RouteScore(string path)
    {
        var lower = path.ToLowerInvariant();
        var name = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        var s = 0;
        if (name.Contains("route")) s += 100;
        if (name.Contains("router")) s += 70;
        if (lower.Contains("/routes/") || lower.Contains("/router/")) s += 80;
        if (name is "app.tsx" or "app.ts") s += 60;
        if (name.Contains("nav")) s += 20;
        if (lower.Contains("/pages/")) s += 10;
        return s;
    }

    private async Task<List<string>> LlmPickRouteFilesAsync(ILlmClient llm, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var prompt =
            "From this list of frontend source files, identify the file(s) that DEFINE THE APP'S ROUTE TABLE " +
            "(the React Router routes / navigation map that maps URL paths to page components). Return ONLY a JSON " +
            "array of up to 3 repo-relative paths from the list, best first — no prose.\n\n" +
            string.Join("\n", paths);
        try
        {
            var data = await JsonUtils.CompleteJsonAsync(
                llm, "You identify the route/navigation file(s) in a frontend repository.", prompt, '[',
                label: "Route file pick", cancellationToken: ct);
            if (data is JsonArray arr)
                return arr.Select(n => (n as JsonValue)?.ToString() ?? "")
                          .Where(s => !string.IsNullOrWhiteSpace(s))
                          .Take(MaxCandidateFiles).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _log.LogWarning(e, "LLM route-file pick failed"); }
        return [];
    }

    private async Task<IReadOnlyList<FrontendModule>> LlmExtractModulesAsync(
        ILlmClient llm, string filesBlock, string root, string baseUrl, CancellationToken ct)
    {
        var prompt =
            "You are given one or more frontend route/source files. Extract the product's TOP-LEVEL modules — the " +
            "primary navigable sections a QA tester would scan (e.g. deals, customers, reports). SKIP detail/sub-routes " +
            "(paths with params like :id), the root '/', auth/login screens, and 404/not-found.\n\n" +
            "Return ONLY a JSON array; each element:\n" +
            "  {\"moduleName\": string, \"routePath\": string, \"componentPath\": string}\n" +
            "- moduleName: short lowercase id, e.g. \"deals\"\n" +
            "- routePath: the SPA route, e.g. \"/deals\"\n" +
            "- componentPath: repo-relative folder of that module's component (best guess from its import), or \"\"\n\n" +
            "FILES:\n" + filesBlock;
        var data = await JsonUtils.CompleteJsonAsync(
            llm, "You extract a frontend app's module/route map as strict JSON.", prompt, '[',
            label: "Module extract", cancellationToken: ct);
        if (data is not JsonArray arr)
            return [];

        var b = (baseUrl ?? "").TrimEnd('/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FrontendModule>();
        foreach (var it in arr)
        {
            if (it is not JsonObject o)
                continue;
            var name = JsonUtils.StrOr(o, "moduleName", "ModuleName", "module", "name").Trim().Trim('/');
            var route = JsonUtils.StrOr(o, "routePath", "RoutePath", "route", "path").Trim();
            var comp = JsonUtils.StrOr(o, "componentPath", "ComponentPath", "component").Trim();
            if (name.Length == 0 || route.Length == 0)
                continue;
            if (route.Contains(':') || route is "/" or "")
                continue;
            if (!route.StartsWith('/'))
                route = "/" + route;
            if (!seen.Add(name.ToLowerInvariant()))
                continue;
            result.Add(new FrontendModule(
                ModuleName: name,
                RoutePath: route,
                ComponentPath: comp.Length > 0 ? comp : (string.IsNullOrEmpty(root) ? $"components/{name}" : $"{root}/components/{name}"),
                Url: $"{b}{route}"));
        }
        return result;
    }

    private async Task<IReadOnlyList<FrontendModule>?> ReadCacheAsync(string key, CancellationToken ct)
    {
        var row = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value))
            return null;
        try { return JsonSerializer.Deserialize<List<FrontendModule>>(row.Value); }
        catch { return null; }
    }

    private async Task WriteCacheAsync(string key, IReadOnlyList<FrontendModule> modules, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(modules);
            var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row is null)
                _db.Settings.Add(new AppSetting { Key = key, Value = json });
            else
                row.Value = json;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception e) { _log.LogWarning(e, "Caching frontend modules failed (non-fatal)"); }
    }
}
