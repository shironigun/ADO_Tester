using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.ScriptsBoard;

/// <summary>Scans generated-spec roots and reconciles them against <c>ApiTestDraft</c> rows.
/// When <see cref="QaLoopOptions.AutomationRoot"/> is set, also inventories the product framework
/// under <c>{AUTOMATION_ROOT}/modules/{unit}/api|ui/...</c> (INT-102). Scoped — uses the
/// request/background-task <see cref="QaDbContext"/>. See <see cref="IScriptInventoryService"/>.</summary>
public sealed class ScriptInventoryService : IScriptInventoryService
{
    private readonly QaDbContext _db;
    private readonly string _scriptsDir;
    private readonly IOptionsMonitor<QaLoopOptions> _qaLoop;

    public ScriptInventoryService(QaDbContext db, IOptions<StorageOptions> storage, IOptionsMonitor<QaLoopOptions> qaLoop)
    {
        _db = db;
        _qaLoop = qaLoop;
        _scriptsDir = Path.GetFullPath(Path.Combine(storage.Value.DataDirectory, qaLoop.CurrentValue.ScriptsFolderName));
    }

    private string? UiGeneratedDir
    {
        get
        {
            var root = _qaLoop.CurrentValue.PlaywrightRoot;
            return string.IsNullOrWhiteSpace(root)
                ? null
                : Path.GetFullPath(Path.Combine(root, "generated", "ui"));
        }
    }

    private string? AutomationRoot
    {
        get
        {
            var root = _qaLoop.CurrentValue.AutomationRoot;
            return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root.Trim());
        }
    }

    public async Task<IReadOnlyList<ScriptInventoryItem>> GetInventoryAsync(CancellationToken ct = default)
    {
        var uiGeneratedDir = UiGeneratedDir;
        var automationRoot = AutomationRoot;
        // DB side: every draft that actually produced a spec file (excludes UiCase — manual cases have
        // no script). Keyed by normalized absolute path for O(1) disk↔DB matching.
        var draftRows = await _db.ApiTestDrafts
            .Where(d => d.SpecFilePath != null && d.DraftKind != ApiDraftKind.UiCase)
            .Select(d => new { d.Id, d.SessionId, d.Level, d.Bucket, d.AdoTestCaseId, d.Tags, d.SpecFilePath, d.DraftKind })
            .ToListAsync(ct);

        var sessionModules = await _db.ApiTestSessions
            .Select(s => new { s.Id, s.Module })
            .ToDictionaryAsync(s => s.Id, s => s.Module, ct);

        var draftsByPath = new Dictionary<string, (int Id, int SessionId, TestLevel Level, string? Bucket,
            int? AdoTestCaseId, string? Tags, ApiDraftKind? DraftKind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in draftRows)
            draftsByPath[NormalizePath(d.SpecFilePath!)] = (d.Id, d.SessionId, d.Level, d.Bucket, d.AdoTestCaseId, d.Tags, d.DraftKind);

        var items = new List<ScriptInventoryItem>();
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var productMode = automationRoot is not null;

        // INT-901: when AUTOMATION_ROOT is set, product framework is the only inventory root —
        // skip legacy backend/data/scripts and private Playwright generated/ui.
        if (!productMode)
        {
            // 1 — Legacy ADO_Tester API specs: {DataDirectory}/scripts/{module}/{level}/*.spec.ts
            if (Directory.Exists(_scriptsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_scriptsDir, "*.spec.ts", SearchOption.AllDirectories))
                    AddApiFile(file, items, matchedPaths, draftsByPath, sessionModules, frameworkLayout: false);
            }

            // 2 — Legacy UI: {PlaywrightRoot}/generated/ui/{bucket}/*.ui.spec.ts
            if (uiGeneratedDir is not null && Directory.Exists(uiGeneratedDir))
            {
                foreach (var file in Directory.EnumerateFiles(uiGeneratedDir, "*.ui.spec.ts", SearchOption.AllDirectories))
                    AddUiFile(file, uiGeneratedDir, items, matchedPaths, draftsByPath, sessionModules, frameworkLayout: false);
            }
        }

        // 3 — Product framework (INT-102): {AUTOMATION_ROOT}/modules/{unit}/api|ui/...
        if (automationRoot is not null)
        {
            var modulesRoot = Path.Combine(automationRoot, "modules");
            if (Directory.Exists(modulesRoot))
            {
                foreach (var file in Directory.EnumerateFiles(modulesRoot, "*.spec.ts", SearchOption.AllDirectories))
                {
                    var kind = ClassifyFrameworkSpec(file, modulesRoot);
                    if (kind == FrameworkSpecKind.Api)
                        AddApiFile(file, items, matchedPaths, draftsByPath, sessionModules, true, automationRoot);
                    else if (kind == FrameworkSpecKind.Ui)
                        AddUiFile(file, modulesRoot, items, matchedPaths, draftsByPath, sessionModules, true, automationRoot);
                }
            }
        }

        // 4 — DB rows whose SpecFilePath no longer exists on disk.
        foreach (var d in draftRows)
        {
            var norm = NormalizePath(d.SpecFilePath!);
            if (matchedPaths.Contains(norm)) continue;

            var isUi = d.DraftKind == ApiDraftKind.UiScript;
            items.Add(new ScriptInventoryItem(
                d.SpecFilePath!, sessionModules.GetValueOrDefault(d.SessionId, ""), isUi ? "ui" : "api",
                d.Level.ToWire(), d.Bucket, d.AdoTestCaseId, d.Id, ParseTags(d.Tags), Meta: null,
                ScriptReconcileStates.OrphanDb));
        }

        return items;
    }

    private void AddApiFile(
        string file,
        List<ScriptInventoryItem> items,
        HashSet<string> matchedPaths,
        Dictionary<string, (int Id, int SessionId, TestLevel Level, string? Bucket, int? AdoTestCaseId, string? Tags, ApiDraftKind? DraftKind)> draftsByPath,
        Dictionary<int, string> sessionModules,
        bool frameworkLayout,
        string? automationRoot = null)
    {
        var norm = NormalizePath(file);
        if (!matchedPaths.Add(norm)) return;

        var meta = TryReadMeta(file);
        draftsByPath.TryGetValue(norm, out var draft);
        var hasDraft = draft.Id != 0;

        var module = meta?.Module;
        if (string.IsNullOrEmpty(module) && hasDraft)
            module = sessionModules.GetValueOrDefault(draft.SessionId, "");
        if (string.IsNullOrEmpty(module))
            module = frameworkLayout
                ? DeriveModuleFromFrameworkPath(file, automationRoot)
                : DeriveModuleFromApiPath(file);

        var level = meta?.Level;
        if (string.IsNullOrEmpty(level) && hasDraft)
            level = draft.Level.ToWire();
        if (string.IsNullOrEmpty(level))
            level = frameworkLayout
                ? DeriveLevelFromFrameworkPath(file, automationRoot)
                : DeriveLevelFromApiPath(file);

        items.Add(new ScriptInventoryItem(
            file, module, meta?.Type ?? "api", level, meta?.Bucket ?? draft.Bucket,
            meta?.TcId ?? draft.AdoTestCaseId, hasDraft ? draft.Id : null,
            ParseTags(draft.Tags), meta,
            !hasDraft ? ScriptReconcileStates.OrphanDisk
                : meta is null ? ScriptReconcileStates.NoMeta
                : ScriptReconcileStates.Linked));
    }

    private void AddUiFile(
        string file,
        string uiRoot,
        List<ScriptInventoryItem> items,
        HashSet<string> matchedPaths,
        Dictionary<string, (int Id, int SessionId, TestLevel Level, string? Bucket, int? AdoTestCaseId, string? Tags, ApiDraftKind? DraftKind)> draftsByPath,
        Dictionary<int, string> sessionModules,
        bool frameworkLayout,
        string? automationRoot = null)
    {
        var norm = NormalizePath(file);
        if (!matchedPaths.Add(norm)) return;

        var meta = TryReadMeta(file);
        draftsByPath.TryGetValue(norm, out var draft);
        var hasDraft = draft.Id != 0;

        var module = meta?.Module;
        if (string.IsNullOrEmpty(module) && hasDraft)
            module = sessionModules.GetValueOrDefault(draft.SessionId, "");
        if (string.IsNullOrEmpty(module) && frameworkLayout)
            module = DeriveModuleFromFrameworkPath(file, automationRoot);
        module ??= "";

        var bucket = meta?.Bucket ?? draft.Bucket
            ?? (frameworkLayout
                ? DeriveBucketFromFrameworkPath(file, automationRoot)
                : DeriveBucketFromUiPath(file, uiRoot));

        items.Add(new ScriptInventoryItem(
            file, module, "ui", "ui", bucket,
            meta?.TcId ?? draft.AdoTestCaseId, hasDraft ? draft.Id : null,
            ParseTags(draft.Tags), meta,
            !hasDraft ? ScriptReconcileStates.OrphanDisk
                : meta is null ? ScriptReconcileStates.NoMeta
                : ScriptReconcileStates.Linked));
    }

    private enum FrameworkSpecKind { Skip, Api, Ui }

    /// <summary>modules/{unit}/api/tests/... → Api; modules/{unit}/ui/tests/... → Ui; schemas etc. → Skip.</summary>
    private static FrameworkSpecKind ClassifyFrameworkSpec(string file, string modulesRoot)
    {
        var rel = Path.GetRelativePath(modulesRoot, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // {unit}/api/tests/... or {unit}/ui/tests/...
        if (parts.Length < 3) return FrameworkSpecKind.Skip;
        return parts[1].ToLowerInvariant() switch
        {
            "api" => FrameworkSpecKind.Api,
            "ui" => FrameworkSpecKind.Ui,
            _ => FrameworkSpecKind.Skip,
        };
    }

    private static string DeriveModuleFromFrameworkPath(string file, string? automationRoot)
    {
        if (automationRoot is null) return "";
        var modulesRoot = Path.Combine(automationRoot, "modules");
        var rel = Path.GetRelativePath(modulesRoot, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 0 ? parts[0] : "";
    }

    private static string DeriveLevelFromFrameworkPath(string file, string? automationRoot)
    {
        if (automationRoot is null) return "";
        var modulesRoot = Path.Combine(automationRoot, "modules");
        var rel = Path.GetRelativePath(modulesRoot, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // deals/api/tests/contracts/foo.spec.ts → contracts
        var dir = parts.Length > 3 ? parts[3] : (parts.Length > 2 ? parts[2] : "");
        return dir switch
        {
            "contracts" => "contract",
            "endpoints" => "endpoint",
            "workflows" => "workflow",
            _ => dir,
        };
    }

    private static string? DeriveBucketFromFrameworkPath(string file, string? automationRoot)
    {
        if (automationRoot is null) return null;
        var modulesRoot = Path.Combine(automationRoot, "modules");
        var rel = Path.GetRelativePath(modulesRoot, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // deals/ui/tests/smoke/foo.spec.ts → smoke
        return parts.Length > 3 ? parts[3] : null;
    }

    private static QaMeta? TryReadMeta(string file)
    {
        try { return QaMetaHeader.TryParse(File.ReadAllText(file)); }
        catch { return null; }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private string DeriveModuleFromApiPath(string file)
    {
        var rel = Path.GetRelativePath(_scriptsDir, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 0 ? parts[0] : "";
    }

    private string DeriveLevelFromApiPath(string file)
    {
        var rel = Path.GetRelativePath(_scriptsDir, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = parts.Length > 1 ? parts[1] : "";
        return dir switch
        {
            "contracts" => "contract",
            "endpoints" => "endpoint",
            "workflows" => "workflow",
            _ => dir,
        };
    }

    private static string? DeriveBucketFromUiPath(string file, string uiRoot)
    {
        var rel = Path.GetRelativePath(uiRoot, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? []; }
        catch { return [tagsJson]; }
    }
}
