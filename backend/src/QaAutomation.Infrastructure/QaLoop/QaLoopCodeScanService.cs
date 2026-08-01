using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Builds a bounded CodeScanBundle for the QA Loop KB-grounded pipeline.
/// Primary path: FRS atlas frontend_path_includes / backend_path_includes globs read from the ADO repo.
/// Fallback: IModuleSourceDiscoveryService (branch-tree scoring).
/// On ADO failure: emits a Warn event and returns an empty bundle (never throws).
/// Scoped.
/// </summary>
public sealed class QaLoopCodeScanService : IQaLoopCodeScanService
{
    private const int MaxFrontendFiles = 40;
    private const int MaxBackendFiles  = 20;
    private const int MaxTotalBytes    = 400_000;

    private readonly IAzureDevOpsClient _ado;
    private readonly IModuleSourceDiscoveryService _discovery;
    private readonly IEventBus _bus;
    private readonly ILogger<QaLoopCodeScanService> _log;

    public QaLoopCodeScanService(
        IAzureDevOpsClient ado,
        IModuleSourceDiscoveryService discovery,
        IEventBus bus,
        ILogger<QaLoopCodeScanService> log)
    {
        _ado = ado;
        _discovery = discovery;
        _bus = bus;
        _log = log;
    }

    public async Task<CodeScanBundle> ScanAsync(
        string module, string repository, string branch,
        FrsModuleContext? frsContext, string sessionKey, CancellationToken ct)
    {
        // Short-circuit when no repo/branch configured.
        if (string.IsNullOrEmpty(repository) || string.IsNullOrEmpty(branch))
        {
            Warn(sessionKey, "Code scan skipped — no repository/branch configured.");
            return Empty("none");
        }

        try
        {
            // ── Pass 1: atlas-seeded paths ────────────────────────────────────
            if (frsContext != null
                && (frsContext.FrontendPathIncludes.Length > 0 || frsContext.BackendPathIncludes.Length > 0))
            {
                var atlasResult = await TryAtlasPathsAsync(
                    frsContext.FrontendPathIncludes, frsContext.BackendPathIncludes,
                    repository, branch, sessionKey, ct);

                if (atlasResult.FrontendFiles.Length > 0 || atlasResult.BackendFiles.Length > 0)
                    return atlasResult;

                Warn(sessionKey, "Atlas paths produced no files — falling back to discovery.");
            }

            // ── Pass 2: IModuleSourceDiscoveryService fallback ────────────────
            return await TryDiscoveryAsync(module, repository, branch, sessionKey, ct);
        }
        catch (Exception ex)
        {
            Warn(sessionKey, $"Code scan failed: {ex.Message}");
            _log.LogWarning(ex, "QaLoopCodeScanService failed for module '{Module}'", module);
            return Empty("none");
        }
    }

    // ── Atlas-seeded fetch ────────────────────────────────────────────────────

    private async Task<CodeScanBundle> TryAtlasPathsAsync(
        string[] fePatterns, string[] bePatterns,
        string repository, string branch, string sessionKey, CancellationToken ct)
    {
        // List the branch root once and filter by pattern prefix.
        IReadOnlyList<string> allPaths;
        try
        {
            allPaths = await _ado.ListRepoFilePathsAsync(
                scopePath: string.Empty, branch: branch, repoId: repository, ct: ct);
        }
        catch (Exception ex)
        {
            Warn(sessionKey, $"Atlas scan: failed to list repo tree — {ex.Message}");
            return Empty("none");
        }

        var fePaths = FilterByPatterns(allPaths, fePatterns).Take(MaxFrontendFiles).ToList();
        var bePaths = FilterByPatterns(allPaths, bePatterns).Take(MaxBackendFiles).ToList();

        var feFiles = await ReadFilesAsync(fePaths, repository, branch, sessionKey, "frontend", ct);
        var beFiles = await ReadFilesAsync(bePaths, repository, branch, sessionKey, "backend", ct);

        ApplySizeCap(feFiles, beFiles, sessionKey);
        var apiCalls = ExtractApiCalls(feFiles);

        return new CodeScanBundle(
            FrontendFiles: feFiles.ToArray(),
            BackendFiles: beFiles.ToArray(),
            ApiCallCandidates: apiCalls,
            DiscoverySource: "atlas");
    }

    // ── Discovery fallback ────────────────────────────────────────────────────

    private async Task<CodeScanBundle> TryDiscoveryAsync(
        string module, string repository, string branch, string sessionKey, CancellationToken ct)
    {
        try
        {
            var discovery = await _discovery.DiscoverAsync(repository, branch, module, null, null, ct);
            if (discovery == null)
            {
                Warn(sessionKey, "Discovery returned no results.");
                return Empty("none");
            }

            // Convert discovery's paths to CodeScanFile[] by reading content.
            var feFiles = await ReadFilesAsync(
                discovery.FrontendFiles.Take(MaxFrontendFiles).ToList(),
                repository, branch, sessionKey, "frontend", ct);
            var beFiles = await ReadFilesAsync(
                discovery.BackendFiles.Take(MaxBackendFiles).ToList(),
                repository, branch, sessionKey, "backend", ct);

            ApplySizeCap(feFiles, beFiles, sessionKey);
            var apiCalls = discovery.ApiCalls
                .Select(c => $"{c.Method} {c.Path}")
                .Distinct()
                .ToArray();

            return new CodeScanBundle(
                FrontendFiles: feFiles.ToArray(),
                BackendFiles: beFiles.ToArray(),
                ApiCallCandidates: apiCalls,
                DiscoverySource: "discovery");
        }
        catch (Exception ex)
        {
            Warn(sessionKey, $"Discovery fallback failed: {ex.Message}");
            return Empty("none");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> FilterByPatterns(
        IReadOnlyList<string> allPaths, string[] patterns)
    {
        foreach (var path in allPaths)
        {
            foreach (var pattern in patterns)
            {
                // Simple prefix/suffix/contains match for common glob patterns.
                var p = pattern.TrimStart('*', '/').TrimEnd('*', '/');
                if (string.IsNullOrEmpty(p)) continue;
                if (path.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                    break;
                }
            }
        }
    }

    private async Task<List<CodeScanFile>> ReadFilesAsync(
        IReadOnlyList<string> paths, string repository, string branch,
        string sessionKey, string kind, CancellationToken ct)
    {
        var results = new List<CodeScanFile>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var content = await _ado.GetFileContentAsync(path, @ref: branch, repoId: repository, ct: ct);
                if (!string.IsNullOrEmpty(content))
                    results.Add(new CodeScanFile(path, content));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "QaLoopCodeScanService: skipping {Kind} file '{Path}'", kind, path);
            }
        }
        return results;
    }

    private void ApplySizeCap(List<CodeScanFile> feFiles, List<CodeScanFile> beFiles, string sessionKey)
    {
        int total = feFiles.Sum(f => f.Content.Length) + beFiles.Sum(f => f.Content.Length);
        if (total <= MaxTotalBytes) return;

        // Drop files from the back until under the cap.
        while (total > MaxTotalBytes && (feFiles.Count > 0 || beFiles.Count > 0))
        {
            if (beFiles.Count > 0)
            {
                total -= beFiles[^1].Content.Length;
                beFiles.RemoveAt(beFiles.Count - 1);
            }
            else if (feFiles.Count > 0)
            {
                total -= feFiles[^1].Content.Length;
                feFiles.RemoveAt(feFiles.Count - 1);
            }
        }
        Warn(sessionKey, $"Code scan: content capped at {MaxTotalBytes / 1024} KB.");
    }

    private static string[] ExtractApiCalls(IReadOnlyList<CodeScanFile> feFiles)
    {
        // Deterministic extraction: Axios, fetch, and common service wrapper patterns.
        var pattern = new Regex(
            @"(?:axios|api|client|http|fetch|get|post|put|patch|delete)\s*[\.\(][^\n]{0,120}['""]([/][^'""]{3,80})['""]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in feFiles)
        {
            foreach (Match m in pattern.Matches(f.Content))
            {
                var path = m.Groups[1].Value.Trim();
                if (path.StartsWith("/api/") || path.StartsWith("/v") || path.Length > 5)
                    calls.Add(path);
            }
        }
        return calls.ToArray();
    }

    private void Warn(string sessionKey, string message)
    {
        _bus.Publish(sessionKey, new Core.Models.ConsoleEvent
        {
            Kind  = Core.Models.EventKinds.Result,
            Phase = Core.Models.EventPhases.Scan,
            Level = Core.Models.EventLevels.Warn,
            Text  = message,
        });
    }

    private static CodeScanBundle Empty(string source) =>
        new([], [], [], source);
}
