using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Finds source evidence from the complete selected branch when convention-based QA Loop paths fail.
/// It deliberately bounds the tree-derived file set before any LLM sees content.
/// </summary>
public sealed class ModuleSourceDiscoveryService : IModuleSourceDiscoveryService
{
    private const int MaxFrontendFiles = 10;
    private const int MaxBackendFiles = 8;
    private const int MaxCharsPerFile = 16_000;
    // The branch tree is complete, but an LLM only needs a focused evidence bundle. Keeping this
    // bounded prevents local/cloud models from silently dropping a completion on oversized prompts.
    private const int MaxTotalBundleChars = 60_000;
    private static readonly Regex ClientCall = new(
        """(?ix)(?:axios|api|client)\s*\.\s*(get|post|put|patch|delete)\s*\(\s*['"`]([^'"`$]+)""",
        RegexOptions.Compiled);
    private static readonly Regex FetchCall = new(
        """(?ix)fetch\s*\(\s*['"`]([^'"`$]+)(?:\s*,\s*\{[^}]*?\bmethod\s*:\s*['"`](get|post|put|patch|delete)['"`])?""",
        RegexOptions.Compiled);

    private readonly IAzureDevOpsClient _ado;
    private readonly ILogger<ModuleSourceDiscoveryService> _log;

    public ModuleSourceDiscoveryService(IAzureDevOpsClient ado, ILogger<ModuleSourceDiscoveryService> log)
    {
        _ado = ado;
        _log = log;
    }

    public async Task<ModuleSourceDiscovery?> DiscoverAsync(
        string repository, string branch, string module, string? frontendPath, string? frontendRoute,
        CancellationToken cancellationToken = default)
    {
        var allPaths = await _ado.ListRepoFilePathsAsync("/", branch, repository, cancellationToken);
        if (allPaths.Count == 0) return null;

        var key = module.Trim().ToLowerInvariant();
        var frontendPaths = allPaths
            .Where(IsFrontendSource)
            .OrderByDescending(path => FrontendScore(path, key, frontendPath))
            .ThenBy(path => path.Count(c => c == '/'))
            .Take(MaxFrontendFiles)
            .ToList();

        if (frontendPaths.Count == 0) return null;

        var source = new StringBuilder();
        var calls = new List<FrontendApiCall>();
        foreach (var path in frontendPaths)
        {
            if (source.Length >= MaxTotalBundleChars) break;
            try
            {
                var content = await _ado.GetFileContentAsync(path, branch, repository, ct: cancellationToken);
                if (string.IsNullOrWhiteSpace(content)) continue;
                var slice = SliceForBundle(content, source.Length);
                calls.AddRange(ExtractCalls(slice, path));
                AppendFile(source, path, slice, "FRONTEND");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable discovered frontend file {Path}", path);
            }
        }

        // Module page components commonly delegate HTTP work to shared `api`, `services`, hooks, or
        // RTK Query files outside the component folder. If the first component-focused pass found no
        // calls, perform a separate API-client-focused pass before concluding the module has no APIs.
        if (calls.Count == 0 && source.Length < MaxTotalBundleChars)
        {
            var apiCandidates = allPaths
                .Where(IsFrontendSource)
                .Where(path => !frontendPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(path => ApiClientScore(path, key))
                .ThenBy(path => path.Count(c => c == '/'))
                .Take(MaxFrontendFiles)
                .ToList();

            foreach (var path in apiCandidates)
            {
                if (source.Length >= MaxTotalBundleChars) break;
                try
                {
                    var content = await _ado.GetFileContentAsync(path, branch, repository, ct: cancellationToken);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    var slice = SliceForBundle(content, source.Length);
                    var extracted = ExtractCalls(slice, path).ToList();
                    if (extracted.Count == 0) continue;
                    frontendPaths.Add(path);
                    calls.AddRange(extracted);
                    AppendFile(source, path, slice, "FRONTEND API CLIENT");
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping unreadable API-client candidate {Path}", path);
                }
            }
        }

        var backendPaths = allPaths
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => BackendScore(path, key, calls))
            .ThenBy(path => path.Count(c => c == '/'))
            .Take(MaxBackendFiles)
            .ToList();

        foreach (var path in backendPaths)
        {
            if (source.Length >= MaxTotalBundleChars) break;
            try
            {
                var content = await _ado.GetFileContentAsync(path, branch, repository, ct: cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                    AppendFile(source, path, SliceForBundle(content, source.Length), "BACKEND");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable discovered backend file {Path}", path);
            }
        }

        if (source.Length == 0) return null;
        var dirs = frontendPaths.Concat(backendPaths)
            .Select(ParentDirectory)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var routeFile = frontendPaths.FirstOrDefault(path => path.Contains("route", StringComparison.OrdinalIgnoreCase));
        var confidence = calls.Count > 0 && backendPaths.Count > 0 ? "high"
            : frontendPaths.Count > 0 ? "medium" : "low";

        return new ModuleSourceDiscovery(
            routeFile,
            frontendRoute,
            frontendPaths,
            calls.Distinct().ToList(),
            backendPaths,
            dirs,
            confidence,
            $"Discovered {frontendPaths.Count} frontend file(s), {calls.Count} API call(s), and {backendPaths.Count} backend candidate file(s) from the selected branch.",
            source.ToString());
    }

    private static bool IsFrontendSource(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);

    private static int FrontendScore(string path, string module, string? frontendPath)
    {
        var lower = path.ToLowerInvariant();
        var score = 0;
        if (!string.IsNullOrWhiteSpace(frontendPath) &&
            lower.Contains(frontendPath.Trim('/').ToLowerInvariant(), StringComparison.Ordinal)) score += 500;
        if (lower.Contains(module, StringComparison.Ordinal)) score += 300;
        if (lower.Contains("/components/") || lower.Contains("/pages/") || lower.Contains("/features/")) score += 80;
        if (lower.Contains("api") || lower.Contains("service") || lower.Contains("query") || lower.Contains("hook")) score += 60;
        if (lower.Contains("route")) score += 40;
        if (lower.Contains(".spec.") || lower.Contains(".test.")) score -= 200;
        return score;
    }

    private static int BackendScore(string path, string module, IReadOnlyList<FrontendApiCall> calls)
    {
        var lower = path.ToLowerInvariant();
        var score = lower.Contains(module, StringComparison.Ordinal) ? 300 : 0;
        if (lower.Contains("controller")) score += 150;
        if (lower.Contains("/api/") || lower.Contains("/controllers/")) score += 80;
        foreach (var call in calls)
        {
            var segment = call.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(segment) && lower.Contains(segment.ToLowerInvariant(), StringComparison.Ordinal))
                score += 40;
        }
        return score;
    }

    private static int ApiClientScore(string path, string module)
    {
        var lower = path.ToLowerInvariant();
        var score = lower.Contains(module, StringComparison.Ordinal) ? 200 : 0;
        if (lower.Contains("/api/") || lower.Contains("/apis/")) score += 180;
        if (lower.Contains("service") || lower.Contains("client")) score += 120;
        if (lower.Contains("query") || lower.Contains("endpoint") || lower.Contains("hook")) score += 100;
        if (lower.Contains("axios") || lower.Contains("http")) score += 80;
        if (lower.Contains(".spec.") || lower.Contains(".test.")) score -= 300;
        return score;
    }

    private static IEnumerable<FrontendApiCall> ExtractCalls(string content, string sourceFile)
    {
        foreach (Match match in ClientCall.Matches(content))
            yield return new FrontendApiCall(match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value, sourceFile);
        foreach (Match match in FetchCall.Matches(content))
            yield return new FrontendApiCall(
                string.IsNullOrEmpty(match.Groups[2].Value) ? "GET" : match.Groups[2].Value.ToUpperInvariant(),
                match.Groups[1].Value,
                sourceFile);
    }

    private static string ParentDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index > 0 ? path[..index] : "";
    }

    private static void AppendFile(StringBuilder target, string path, string content, string kind)
    {
        target.Append("# ").Append(kind).Append(" FILE: ").Append(path).Append('\n');
        target.Append("```\n").Append(content).Append("\n```\n\n");
    }

    private static string SliceForBundle(string content, int currentLength)
    {
        var remaining = Math.Max(0, MaxTotalBundleChars - currentLength - 128);
        var count = Math.Min(content.Length, Math.Min(MaxCharsPerFile, remaining));
        return count == content.Length ? content : content[..count];
    }
}
