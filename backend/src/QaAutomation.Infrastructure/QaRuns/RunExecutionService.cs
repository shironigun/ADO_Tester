using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>
/// Drives one Playwright run end-to-end: draft resolution, ADO Test Run lifecycle,
/// Playwright execution, result persistence, and failure record creation.
/// Scoped service — the caller owns the DI scope and DbContext lifetime.
/// </summary>
public sealed class RunExecutionService
{
    private static readonly Regex TcIdRegex = new(@"\[(\d+)\]", RegexOptions.Compiled);

    private readonly QaDbContext _db;
    private readonly IAzureDevOpsClient _ado;
    private readonly IPlaywrightRunner _runner;
    private readonly IEventBus _bus;
    private readonly ILogger<RunExecutionService> _log;

    public RunExecutionService(
        QaDbContext db, IAzureDevOpsClient ado, IPlaywrightRunner runner,
        IEventBus bus, ILogger<RunExecutionService> log)
    {
        _db = db;
        _ado = ado;
        _runner = runner;
        _bus = bus;
        _log = log;
    }

    public async Task ExecuteAsync(int runId, CancellationToken ct = default)
    {
        var eventKey = $"run:{runId}";
        var run = await _db.TestRuns.FindAsync([runId], ct)
            ?? throw new InvalidOperationException($"TestRun {runId} not found.");

        run.Status = TestRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Pub(eventKey, EventKinds.Phase, "run", $"TestRun {runId} starting");

        try
        {
            // 1 — Resolve which Pushed drafts to run
            var drafts = await ResolveDraftsAsync(run, ct);
            if (drafts.Count == 0)
            {
                run.Status = TestRunStatus.Cancelled;
                run.ErrorMessage = "No pushed drafts found for this run.";
                run.FinishedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                Pub(eventKey, EventKinds.System, text: "No pushed drafts — run cancelled", level: EventLevels.Warn);
                return;
            }

            var grepTags = drafts
                .Where(d => d.AdoTestCaseId.HasValue)
                .Select(d => d.AdoTestCaseId!.Value)
                .ToList();

            // 2 — Attempt ADO Test Run setup (skip on failure — run still proceeds)
            int? adoTestRunId = null;
            IReadOnlyDictionary<int, int> pointMap = new Dictionary<int, int>();
            var (planId, suiteId) = await ResolvePlanSuiteAsync(run, drafts, ct);
            if (planId.HasValue && suiteId.HasValue)
            {
                try
                {
                    pointMap = await _ado.GetTestPointsAsync(planId.Value, suiteId.Value, grepTags, ct);
                    var runName = $"QA Loop Run {runId} — {run.Module ?? "module"} — {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
                    adoTestRunId = await _ado.CreateAdoTestRunAsync(
                        planId.Value, suiteId.Value, runName, pointMap.Values.ToList(), ct);
                    run.AdoTestRunId = adoTestRunId;
                    await _db.SaveChangesAsync(ct);
                    Pub(eventKey, EventKinds.Result, "run",
                        $"ADO Test Run #{adoTestRunId} created ({pointMap.Count} point(s))");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "CreateAdoTestRunAsync failed — proceeding without ADO Test Run");
                    run.ErrorMessage = $"ADO Test Run setup failed (non-fatal): {ex.Message}";
                    await _db.SaveChangesAsync(ct);
                }
            }

            // 3 — Run Playwright
            var spec = new PlaywrightRunSpec
            {
                RunId             = runId,
                Profile           = run.Profile.ToWire(),
                GrepTags          = grepTags,
                Module            = run.Module ?? "",
                LevelFilter       = run.LevelFilter,
                TriggerSourceWire = run.TriggerSource.ToWire(),
                TriggerBuildId    = run.TriggerBuildId,
                TriggerCommitSha  = run.TriggerCommitSha,
                AdoTestRunId      = adoTestRunId,
            };
            var result = await _runner.RunAsync(spec, eventKey, ct);

            // 4 — Post results to ADO Test Run
            if (adoTestRunId.HasValue && pointMap.Count > 0)
            {
                await PostAdoResultsAsync(adoTestRunId.Value, result, grepTags, pointMap, ct);
            }

            // 5 — Persist run counts + paths
            run.Passed    = result.Passed;
            run.Failed    = result.Failed;
            run.Skipped   = result.Skipped;
            run.DurationMs = result.DurationMs;
            run.JunitPath  = result.JunitPath;
            run.FinishedAt = DateTime.UtcNow;
            run.Status     = result.Failed > 0 ? TestRunStatus.Failed : TestRunStatus.Passed;

            // 6 — Persist failure records (triage classification deferred to QAL-035)
            foreach (var failure in result.FailedTests)
            {
                var tcId = ExtractTcId(failure.TestTitle);
                _db.TestFailures.Add(new TestFailure
                {
                    RunId            = runId,
                    TestTitle        = failure.TestTitle,
                    AdoTestCaseId    = tcId,
                    Classification   = FailureClassification.TestIssue, // QAL-035: AI triage will override
                    Confidence       = "low",
                    Reason           = failure.ErrorMessage ?? "",
                });
            }

            await _db.SaveChangesAsync(ct);
            Pub(eventKey, EventKinds.System, "run",
                $"Run {runId} complete — {run.Passed} passed · {run.Failed} failed · {run.Skipped} skipped",
                run.Status == TestRunStatus.Passed ? EventLevels.Success : EventLevels.Warn,
                new() { ["terminal"] = true, ["status"] = run.Status.ToString() });
        }
        catch (OperationCanceledException)
        {
            run.Status = TestRunStatus.Cancelled;
            run.FinishedAt = DateTime.UtcNow;
            await SafeCommitAsync();
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "TestRun {RunId} failed", runId);
            run.Status = TestRunStatus.Error;
            run.ErrorMessage = e.Message;
            run.FinishedAt = DateTime.UtcNow;
            await SafeCommitAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<ApiTestDraft>> ResolveDraftsAsync(TestRun run, CancellationToken ct)
    {
        // Prefer ImpactAnalysis-scoped ids if set
        var impactedIds = ParseImpactedIds(run.ImpactAnalysisJson);
        if (impactedIds.Count > 0)
        {
            return await _db.ApiTestDrafts
                .Where(d => impactedIds.Contains(d.Id)
                            && d.Status == ApiDraftStatus.Pushed
                            && d.AdoTestCaseId != null)
                .ToListAsync(ct);
        }

        // Fall back to module-wide or level-scoped query
        var q = _db.ApiTestDrafts
            .Where(d => d.Status == ApiDraftStatus.Pushed && d.AdoTestCaseId != null);
        if (!string.IsNullOrEmpty(run.Module))
        {
            // Join via ApiTestSession to filter by module
            var sessionIds = await _db.ApiTestSessions
                .Where(s => s.Module == run.Module)
                .Select(s => s.Id)
                .ToListAsync(ct);
            q = q.Where(d => sessionIds.Contains(d.SessionId));
        }
        if (!string.IsNullOrEmpty(run.LevelFilter) &&
            Enum.TryParse<TestLevel>(run.LevelFilter, ignoreCase: true, out var level))
        {
            q = q.Where(d => d.Level == level);
        }
        return await q.ToListAsync(ct);
    }

    private async Task<(int? PlanId, int? SuiteId)> ResolvePlanSuiteAsync(
        TestRun run, List<ApiTestDraft> drafts, CancellationToken ct)
    {
        // Caller may have pre-populated the run
        if (run.TestPlanId.HasValue && run.TestSuiteId.HasValue)
            return (run.TestPlanId, run.TestSuiteId);

        var sessionIds = drafts.Select(d => d.SessionId).Distinct().ToList();
        var sessions = await _db.ApiTestSessions
            .Where(s => sessionIds.Contains(s.Id) && s.TestPlanId != null && s.TestSuiteId != null)
            .Select(s => new { s.TestPlanId, s.TestSuiteId })
            .Distinct()
            .ToListAsync(ct);

        if (sessions.Count == 1)
        {
            run.TestPlanId = sessions[0].TestPlanId;
            run.TestSuiteId = sessions[0].TestSuiteId;
            return (sessions[0].TestPlanId, sessions[0].TestSuiteId);
        }

        if (sessions.Count > 1)
            _log.LogWarning("Run {RunId}: drafts span {Count} plan/suite pairs — skipping ADO Test Run", run.Id, sessions.Count);
        else
            _log.LogInformation("Run {RunId}: no plan/suite found — skipping ADO Test Run", run.Id);

        return (null, null);
    }

    private async Task PostAdoResultsAsync(
        int adoTestRunId, PlaywrightRunResult result,
        IReadOnlyList<int> allTcIds, IReadOnlyDictionary<int, int> pointMap,
        CancellationToken ct)
    {
        try
        {
            foreach (var tcId in allTcIds)
            {
                if (!pointMap.TryGetValue(tcId, out var ptId)) continue;
                var failed = result.FailedTests.FirstOrDefault(f => ExtractTcId(f.TestTitle) == tcId);
                var outcome = failed is null ? "Passed" : "Failed";
                var errMsg  = failed?.ErrorMessage;
                await _ado.SetAdoTestResultAsync(adoTestRunId, ptId, outcome, errMsg, 0, ct);
            }

            await _ado.CompleteAdoTestRunAsync(adoTestRunId, ct);
            Pub($"run:{adoTestRunId}", EventKinds.Result, "run", $"ADO Test Run #{adoTestRunId} completed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ADO Test Run result posting failed for run {Id}", adoTestRunId);
        }
    }

    private static List<int> ParseImpactedIds(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var impact = System.Text.Json.JsonSerializer.Deserialize<ImpactAnalysis>(json);
            return impact?.ImpactedDraftIds ?? [];
        }
        catch { return []; }
    }

    private static int? ExtractTcId(string testTitle)
    {
        var m = TcIdRegex.Match(testTitle);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private async Task SafeCommitAsync()
    {
        try { await _db.SaveChangesAsync(); }
        catch { }
    }
}
