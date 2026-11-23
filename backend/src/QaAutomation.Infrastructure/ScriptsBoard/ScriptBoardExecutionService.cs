using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.ScriptsBoard;

/// <summary>Ad-hoc Scripts Board execution (Slab 3, QLV2-303): resolves a selection filter against the
/// live inventory, persists a <see cref="ScriptBoardRun"/>, and executes it via
/// <see cref="IScriptsBoardRunner"/>. <c>RunId = -runId</c> is passed to the runner so its on-disk
/// artifacts under <c>{runsDir}/{runId}</c> never collide with a positive <see cref="TestRun"/> id (see
/// blueprint.md → SLAB 3 grounding). Scoped — mirrors <c>RunExecutionService</c>'s lifecycle/error
/// handling exactly.</summary>
public sealed class ScriptBoardExecutionService : IScriptBoardExecutionService
{
    private readonly QaDbContext _db;
    private readonly IScriptInventoryService _inventory;
    private readonly IScriptsBoardRunner _runner;
    private readonly IEventBus _bus;

    public ScriptBoardExecutionService(
        QaDbContext db, IScriptInventoryService inventory, IScriptsBoardRunner runner, IEventBus bus)
    {
        _db = db;
        _inventory = inventory;
        _runner = runner;
        _bus = bus;
    }

    public async Task<int> StartAsync(ScriptBoardRunFilter filter, CancellationToken ct = default)
    {
        List<string> paths;
        string? module;

        if (filter.SpecFilePaths is { Count: > 0 })
        {
            paths = filter.SpecFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            module = filter.Module;
        }
        else
        {
            var matched = (await _inventory.GetInventoryAsync(ct))
                .Where(i => i.ReconcileState != ScriptReconcileStates.OrphanDb)   // no file to run
                .Where(i => filter.Module is null || string.Equals(i.Module, filter.Module, StringComparison.OrdinalIgnoreCase))
                .Where(i => filter.Level is null || string.Equals(i.Level, filter.Level, StringComparison.OrdinalIgnoreCase))
                .Where(i => filter.Type is null || string.Equals(i.Type, filter.Type, StringComparison.OrdinalIgnoreCase))
                .Where(i => filter.Bucket is null || string.Equals(i.Bucket, filter.Bucket, StringComparison.OrdinalIgnoreCase))
                .Where(i => filter.Tag is null || i.Tags.Any(t => string.Equals(t, filter.Tag, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            paths = matched.Select(i => i.SpecFilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var distinctModules = matched.Select(i => i.Module).Where(m => !string.IsNullOrEmpty(m)).Distinct().ToList();
            module = filter.Module ?? (distinctModules.Count == 1 ? distinctModules[0] : null);
        }

        if (paths.Count == 0)
            throw new ArgumentException("No specs match the selection.");

        var projectId = await _db.ApiTestSessions.Select(s => s.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId == 0) projectId = 1;

        var run = new ScriptBoardRun
        {
            ProjectId = projectId,
            SelectionSummary = BuildSummary(filter, paths.Count),
            Module = module,
            SpecFilePathsJson = JsonSerializer.Serialize(paths),
            Headed = filter.Headed,
            Workers = filter.Workers,
            Profile = string.IsNullOrWhiteSpace(filter.Profile) ? null : filter.Profile.Trim(),
            Status = TestRunStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ScriptBoardRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task ExecuteAsync(int runId, CancellationToken ct = default)
    {
        var eventKey = $"scripts-board-run:{runId}";
        var run = await _db.ScriptBoardRuns.FindAsync([runId], ct)
            ?? throw new InvalidOperationException($"ScriptBoardRun {runId} not found.");

        run.Status = TestRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Pub(eventKey, EventKinds.Phase, "scripts-board", $"Scripts Board run {runId} starting");

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(run.SpecFilePathsJson) ?? [];
            var isUi = paths.Any(p => p.EndsWith(".ui.spec.ts", StringComparison.OrdinalIgnoreCase)
                || p.Contains($"{Path.DirectorySeparatorChar}ui{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/ui/", StringComparison.OrdinalIgnoreCase));
            var spec = new PlaywrightRunSpec
            {
                RunId = -runId,
                Module = run.Module ?? "_all",
                SpecFilePaths = paths,
                Headed = run.Headed,
                Workers = run.Workers,
                Profile = run.Profile ?? "",
                ActivityEnabled = true,
                IsUiRun = isUi,
                EnableCapture = isUi,
            };
            var result = await _runner.RunWithLiveTermAsync(spec, eventKey, ct);

            run.Passed = result.Passed;
            run.Failed = result.Failed;
            run.Skipped = result.Skipped;
            run.DurationMs = result.DurationMs;
            run.JunitPath = result.JunitPath;
            run.ReportPath = result.ReportPath;
            run.FinishedAt = DateTime.UtcNow;
            run.Status = result.ResolveStatus();
            run.ErrorMessage = result.ResolveErrorMessage();

            Pub(eventKey, EventKinds.System, "scripts-board",
                $"Run {runId} complete — {run.Passed} passed · {run.Failed} failed · {run.Skipped} skipped" +
                (run.ErrorMessage is { } err ? $" — {err}" : ""),
                run.Status == TestRunStatus.Passed ? EventLevels.Success : EventLevels.Warn,
                new() { ["terminal"] = true, ["status"] = run.Status.ToString() });
            run.EventLogJson = SerializeNarrative(eventKey);
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            run.Status = TestRunStatus.Cancelled;
            run.FinishedAt = DateTime.UtcNow;
            run.EventLogJson = SerializeNarrative(eventKey);
            await SafeCommitAsync();
            throw;
        }
        catch (Exception e)
        {
            run.Status = TestRunStatus.Error;
            run.ErrorMessage = e.Message;
            run.FinishedAt = DateTime.UtcNow;
            run.EventLogJson = SerializeNarrative(eventKey);
            await SafeCommitAsync();
        }
    }

    private static string BuildSummary(ScriptBoardRunFilter filter, int resolvedCount)
    {
        if (filter.SpecFilePaths is { Count: > 0 })
            return $"{filter.SpecFilePaths.Count} explicit spec(s)";

        var parts = new List<string>();
        if (filter.Module is not null) parts.Add($"module={filter.Module}");
        if (filter.Level is not null) parts.Add($"level={filter.Level}");
        if (filter.Type is not null) parts.Add($"type={filter.Type}");
        if (filter.Bucket is not null) parts.Add($"bucket={filter.Bucket}");
        if (filter.Tag is not null) parts.Add($"tag={filter.Tag}");
        var scope = parts.Count > 0 ? string.Join(", ", parts) : "all";
        return $"{scope} ({resolvedCount} spec(s))";
    }

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private string? SerializeNarrative(string eventKey)
    {
        try { return JsonSerializer.Serialize(_bus.Narrative(eventKey)); }
        catch { return null; }
    }

    private async Task SafeCommitAsync()
    {
        try { await _db.SaveChangesAsync(); }
        catch { /* best-effort */ }
    }
}
