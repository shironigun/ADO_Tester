using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.ScriptsBoard;

/// <summary>Scans the two generated-spec roots and reconciles them against <c>ApiTestDraft</c> rows.
/// Scoped — uses the request/background-task <see cref="QaDbContext"/>. See
/// <see cref="IScriptInventoryService"/> for the reconcile-state contract.</summary>
public sealed class ScriptInventoryService : IScriptInventoryService
{
    private readonly QaDbContext _db;
    private readonly string _scriptsDir;
    private readonly string? _uiGeneratedDir;

    public ScriptInventoryService(QaDbContext db, IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop)
    {
        _db = db;
        _scriptsDir = Path.GetFullPath(Path.Combine(storage.Value.DataDirectory, qaLoop.Value.ScriptsFolderName));
        _uiGeneratedDir = string.IsNullOrWhiteSpace(qaLoop.Value.PlaywrightRoot)
            ? null
            : Path.GetFullPath(Path.Combine(qaLoop.Value.PlaywrightRoot, "generated", "ui"));
    }

    public async Task<IReadOnlyList<ScriptInventoryItem>> GetInventoryAsync(CancellationToken ct = default)
    {
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

        // 1 — Disk scan: API specs under {DataDirectory}/scripts/{module}/{level}/*.spec.ts
        if (Directory.Exists(_scriptsDir))
        {
            foreach (var file in Directory.EnumerateFiles(_scriptsDir, "*.spec.ts", SearchOption.AllDirectories))
            {
                var norm = NormalizePath(file);
                matchedPaths.Add(norm);
                var meta = TryReadMeta(file);
                draftsByPath.TryGetValue(norm, out var draft);
                var hasDraft = draft.Id != 0;

                var module = meta?.Module ?? (hasDraft ? sessionModules.GetValueOrDefault(draft.SessionId, "") : DeriveModuleFromApiPath(file));
                var level = meta?.Level ?? (hasDraft ? draft.Level.ToWire() : DeriveLevelFromApiPath(file));

                items.Add(new ScriptInventoryItem(
                    file, module, meta?.Type ?? "api", level, meta?.Bucket ?? draft.Bucket,
                    meta?.TcId ?? draft.AdoTestCaseId, hasDraft ? draft.Id : null,
                    ParseTags(draft.Tags), meta,
                    !hasDraft ? ScriptReconcileStates.OrphanDisk
                        : meta is null ? ScriptReconcileStates.NoMeta
                        : ScriptReconcileStates.Linked));
            }
        }

        // 2 — Disk scan: UI specs under {PlaywrightRoot}/generated/ui/{bucket}/*.ui.spec.ts (Slab-2
        // layout — no module segment in the path, so module comes from @qa-meta when present).
        if (_uiGeneratedDir is not null && Directory.Exists(_uiGeneratedDir))
        {
            foreach (var file in Directory.EnumerateFiles(_uiGeneratedDir, "*.ui.spec.ts", SearchOption.AllDirectories))
            {
                var norm = NormalizePath(file);
                matchedPaths.Add(norm);
                var meta = TryReadMeta(file);
                draftsByPath.TryGetValue(norm, out var draft);
                var hasDraft = draft.Id != 0;

                var module = meta?.Module ?? (hasDraft ? sessionModules.GetValueOrDefault(draft.SessionId, "") : "");
                var bucket = meta?.Bucket ?? draft.Bucket ?? DeriveBucketFromUiPath(file, _uiGeneratedDir);

                items.Add(new ScriptInventoryItem(
                    file, module, "ui", "ui", bucket,
                    meta?.TcId ?? draft.AdoTestCaseId, hasDraft ? draft.Id : null,
                    ParseTags(draft.Tags), meta,
                    !hasDraft ? ScriptReconcileStates.OrphanDisk
                        : meta is null ? ScriptReconcileStates.NoMeta
                        : ScriptReconcileStates.Linked));
            }
        }

        // 3 — DB rows whose SpecFilePath no longer exists on disk (drifted — synthetic items, no file
        // content to read, so Meta is always null here).
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

    private static QaMeta? TryReadMeta(string file)
    {
        try { return QaMetaHeader.TryParse(File.ReadAllText(file)); }
        catch { return null; }   // unreadable file (locked/deleted mid-scan) — treat as no metadata
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Fallback when a spec has no @qa-meta and no matching draft row: derive module/level from
    /// the known {scripts}/{module}/{level-dir}/{file} layout.</summary>
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
