using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.QaRuns;

namespace QaAutomation.Infrastructure.QaPipelines;

public sealed class PipelineWatcherHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IEventBus _bus;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly ILogger<PipelineWatcherHostedService> _log;

    private readonly Dictionary<int, DateTime> _backOffUntil = new();
    private const int BackOffMinutes = 15;

    public PipelineWatcherHostedService(
        IServiceScopeFactory scopeFactory, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        IEventBus bus, IOptions<QaLoopOptions> opts, ILogger<PipelineWatcherHostedService> log)
    {
        _scopeFactory = scopeFactory;
        _ado = ado;
        _llmFactory = llmFactory;
        _bus = bus;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_opts.Value.PollIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAllAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "PipelineWatcher outer loop error"); }
            await Task.Delay(interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var watches = await db.PipelineWatches.Where(w => w.Enabled).ToListAsync(ct);
        foreach (var watch in watches)
        {
            if (_backOffUntil.TryGetValue(watch.Id, out var until) && DateTime.UtcNow < until)
                continue;
            try
            {
                await ProcessWatchAsync(watch, db, ct);
                watch.LastPollAt = DateTime.UtcNow;
                watch.LastPollError = null;
                watch.UpdatedAt = DateTime.UtcNow;
                _backOffUntil.Remove(watch.Id);
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poll failed for PipelineWatch {Id} ({Pipeline})", watch.Id, watch.AdoPipelineId);
                watch.LastPollError = ex.Message;
                watch.LastPollAt = DateTime.UtcNow;
                watch.UpdatedAt = DateTime.UtcNow;
                _backOffUntil[watch.Id] = DateTime.UtcNow.AddMinutes(BackOffMinutes);
                try { await db.SaveChangesAsync(ct); } catch { }
            }
        }
    }

    private async Task ProcessWatchAsync(PipelineWatch watch, QaDbContext db, CancellationToken ct)
    {
        // First-run guard: if no LastSeenBuildId has been recorded yet, skip the entire
        // historical backlog by advancing to the most recent build and returning.
        // This prevents a newly-created watch on a busy shared pipeline from churning
        // through thousands of old builds and flooding the runs table with cancellations.
        if (watch.LastSeenBuildId is null)
        {
            var allBuilds = await _ado.GetPipelineBuildsAsync(watch.AdoPipelineId, ct: ct);
            var latest = allBuilds.MaxBy(b => b.Id);
            if (latest is not null)
            {
                _log.LogInformation(
                    "PipelineWatch {Id} ({Pipeline}): first poll — skipping to latest build {BuildId}",
                    watch.Id, watch.AdoPipelineId, latest.Id);
                watch.LastSeenBuildId = latest.Id;
                watch.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var builds = await _ado.GetPipelineBuildsAsync(watch.AdoPipelineId, watch.LastSeenBuildId, ct);
        foreach (var build in builds.OrderBy(b => b.Id))
        {
            // Re-read the enabled flag each iteration so the user can abort a long backlog
            // catch-up by disabling the watch — takes effect within one build's processing.
            await db.Entry(watch).ReloadAsync(ct);
            if (!watch.Enabled) break;

            await ProcessBuildAsync(watch, build, db, ct);
            watch.LastSeenBuildId = build.Id;
            watch.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<int?> ProcessBuildAsync(
        PipelineWatch watch, AdoBuild build, QaDbContext db, CancellationToken ct, bool force = false)
    {
        // Idempotency guard: a build that already produced a TestRun (any status) is never
        // re-processed by the poller — only the explicit "Run latest" button (force) may re-run.
        if (!force && await db.TestRuns.AnyAsync(
                r => r.TriggerPipelineId == watch.AdoPipelineId && r.TriggerBuildId == build.Id, ct))
        {
            _log.LogInformation(
                "PipelineWatch {Id}: build {BuildId} already has a run — skipping", watch.Id, build.Id);
            return null;
        }

        // Collect changed files from all commits in this build
        var buildChanges = await _ado.GetBuildChangesAsync(build.Id, ct);
        var allFiles = new List<string>();
        foreach (var change in buildChanges)
        {
            try
            {
                var files = await _ado.GetCommitChangedFilesAsync(change.Id, ct: ct);
                allFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not get changed files for commit {Sha}", change.Id);
            }
        }
        allFiles = allFiles.Distinct().ToList();

        // Build test inventory for this module
        var inventory = await BuildInventoryAsync(watch.Module, db, ct);

        if (inventory.Count == 0)
        {
            var noRun = CreateBaseRun(watch, build);
            noRun.Status = TestRunStatus.Cancelled;
            noRun.ErrorMessage = "no impacted tests";
            noRun.FinishedAt = DateTime.UtcNow;
            db.TestRuns.Add(noRun);
            return null;
        }

        // Empty change set (manually re-queued build, no new commits since the previous one):
        // the impact analyzer has nothing to reason over and would cancel with "no impacted
        // tests" — but a re-deployed build is exactly when a full regression is wanted. Skip the
        // LLM and run the whole module.
        var commitMessages = buildChanges.Select(c => c.Message).ToList();
        var impact = allFiles.Count == 0 && commitMessages.Count == 0
            ? new ImpactAnalysis
            {
                ImpactedDraftIds = inventory.Select(d => d.Id).ToList(),
                Reasoning = "No changed files detected for this build (manual re-queue or empty change set) — running the full module regression.",
                Confidence = "high",
            }
            : await AnalyzeImpactWithFallbackAsync(allFiles, commitMessages, inventory, ct);

        if (impact.ImpactedDraftIds.Count == 0)
        {
            var noRun = CreateBaseRun(watch, build);
            noRun.Status = TestRunStatus.Cancelled;
            noRun.ErrorMessage = "no impacted tests";
            noRun.ImpactAnalysisJson = JsonSerializer.Serialize(impact);
            noRun.FinishedAt = DateTime.UtcNow;
            db.TestRuns.Add(noRun);
            return null;
        }

        var run = CreateBaseRun(watch, build);
        run.ImpactAnalysisJson = JsonSerializer.Serialize(impact);
        run.ActivityEnabled = _opts.Value.ActivityEnabled;
        db.TestRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var runId = run.Id;
        _ = Task.Run(async () =>
        {
            using var runScope = _scopeFactory.CreateScope();
            var svc = runScope.ServiceProvider.GetRequiredService<RunExecutionService>();
            try { await svc.ExecuteAsync(runId); }
            catch (Exception ex) { _log.LogError(ex, "Background run {RunId} failed", runId); }
        });

        return runId;
    }

    private static TestRun CreateBaseRun(PipelineWatch watch, AdoBuild build) => new()
    {
        ProjectId = watch.ProjectId,
        Module = watch.Module,
        Profile = DmsProfile.Infinity,
        TriggerSource = TriggerSource.PipelineWatcher,
        TriggerPipelineId = watch.AdoPipelineId,
        TriggerBuildId = build.Id,
        TriggerCommitSha = build.SourceVersion,
        Status = TestRunStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        ActivityEnabled = true,
    };

    private async Task<ImpactAnalysis> AnalyzeImpactWithFallbackAsync(
        IReadOnlyList<string> changedFiles, IReadOnlyList<string> commitMessages,
        IReadOnlyList<DraftInventoryItem> inventory, CancellationToken ct)
    {
        AgentException? lastEx = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var llm = await _llmFactory.CreateAsync(ct);
                return await new ImpactAnalyzerAgent(llm).AnalyzeAsync(changedFiles, commitMessages, inventory, ct: ct);
            }
            catch (AgentException ex)
            {
                lastEx = ex;
                if (attempt == 0) _log.LogWarning(ex, "ImpactAnalyzer attempt 1 failed, retrying");
                else _log.LogWarning(ex, "ImpactAnalyzer attempt 2 failed, falling back to module-wide run");
            }
        }
        _ = lastEx; // suppress unused warning
        return new ImpactAnalysis
        {
            ImpactedDraftIds = inventory.Select(d => d.Id).ToList(),
            Reasoning = "Impact analysis failed after retry; falling back to module-wide run",
            Confidence = "low",
            Fallback = true,
        };
    }

    private static async Task<List<DraftInventoryItem>> BuildInventoryAsync(
        string module, QaDbContext db, CancellationToken ct)
    {
        var sessionIds = await db.ApiTestSessions
            .Where(s => s.Module == module)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var drafts = await db.ApiTestDrafts
            .Where(d => sessionIds.Contains(d.SessionId) && d.Status == ApiDraftStatus.Pushed)
            .ToListAsync(ct);

        return drafts.Select(d => new DraftInventoryItem(
            Id: d.Id,
            Module: module,
            Level: d.Level.ToWire(),
            Title: d.Title,
            SourceRoutes: TryDeserializeStringList(d.SourceRoutesJson),
            Tags: TryDeserializeStringList(d.Tags)
        )).ToList();
    }

    private static List<string> TryDeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    // ── Public API (called by QaPipelinesController) ──────────────────────────

    /// <summary>
    /// Fetches the most recent succeeded build for the given watch, runs the full impact-analysis
    /// path, advances LastSeenBuildId, and returns the new TestRun id (null if no build found or
    /// no impacted tests).
    /// </summary>
    public async Task<int?> RunLatestAsync(int watchId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var watch = await db.PipelineWatches.FindAsync([watchId], ct)
            ?? throw new InvalidOperationException($"PipelineWatch {watchId} not found.");

        var builds = await _ado.GetPipelineBuildsAsync(watch.AdoPipelineId, ct: ct);
        var latest = builds.MaxBy(b => b.Id);
        if (latest is null) return null;

        var runId = await ProcessBuildAsync(watch, latest, db, ct, force: true);
        watch.LastSeenBuildId = Math.Max(watch.LastSeenBuildId ?? 0, latest.Id);
        watch.LastPollAt = DateTime.UtcNow;
        watch.LastPollError = null;
        watch.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return runId;
    }

    /// <summary>
    /// Advances LastSeenBuildId to the most recent succeeded build without creating any test run.
    /// Useful when a watch is stuck catching up on a large historical backlog.
    /// Returns the build id that was set, or null if no builds exist for the pipeline.
    /// </summary>
    public async Task<int?> SkipToLatestAsync(int watchId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var watch = await db.PipelineWatches.FindAsync([watchId], ct)
            ?? throw new InvalidOperationException($"PipelineWatch {watchId} not found.");

        var builds = await _ado.GetPipelineBuildsAsync(watch.AdoPipelineId, ct: ct);
        var latest = builds.MaxBy(b => b.Id);
        if (latest is null) return null;

        watch.LastSeenBuildId = latest.Id;
        watch.LastPollAt = DateTime.UtcNow;
        watch.LastPollError = null;
        watch.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("PipelineWatch {Id} ({Pipeline}): skipped to latest build {BuildId}", watchId, watch.AdoPipelineId, latest.Id);
        return latest.Id;
    }
}
