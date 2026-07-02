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

            // Spec file paths replace --grep-by-title so [tcId] no longer needs to appear in
            // test names. Each pushed draft stores the absolute path Playwright was written to.
            var specFilePaths = drafts
                .Where(d => d.SpecFilePath != null && File.Exists(d.SpecFilePath))
                .Select(d => d.SpecFilePath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Map spec file BASENAME → all draft (tcId, title) pairs. The draft stores an absolute
            // path but the JUnit reporter emits a relative one (testsuite name), so we key on the
            // file name only. Endpoint specs share one physical file across the 200/401/400
            // case-drafts (QAL-067), so a file can host multiple cases; title matching disambiguates.
            var specPathDrafts = new Dictionary<string, List<(int TcId, string Title)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drafts.Where(d => d.SpecFilePath != null && d.AdoTestCaseId.HasValue))
            {
                var key = SpecKey(d.SpecFilePath!);
                if (!specPathDrafts.TryGetValue(key, out var lst))
                    specPathDrafts[key] = lst = [];
                lst.Add((d.AdoTestCaseId!.Value, d.Title));
            }

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
                SpecFilePaths     = specFilePaths,
                Module            = run.Module ?? "",
                LevelFilter       = run.LevelFilter,
                TriggerSourceWire  = run.TriggerSource.ToWire(),
                TriggerPipelineId  = run.TriggerPipelineId,
                TriggerBuildId     = run.TriggerBuildId,
                TriggerCommitSha  = run.TriggerCommitSha,
                AdoTestRunId      = adoTestRunId,
                ActivityEnabled   = run.ActivityEnabled,
            };
            var result = await _runner.RunAsync(spec, eventKey, ct);

            // Resolve each failed test to its ADO Test Case id ONCE (spec-path candidates + title
            // similarity, with a legacy [tcId]-in-title fallback). Reused for both the ADO result
            // outcomes and the persisted TestFailure records so they can't disagree.
            var failedTcIds = new HashSet<int>();
            var failureTcById = new List<(FailedTestResult Failure, int? TcId)>();
            foreach (var failure in result.FailedTests)
            {
                var tcId = ResolveFailureTcId(failure, specPathDrafts);
                failureTcById.Add((failure, tcId));
                if (tcId is int id) failedTcIds.Add(id);
            }

            // 4 — Post results to ADO Test Run
            if (adoTestRunId.HasValue && pointMap.Count > 0)
            {
                await PostAdoResultsAsync(adoTestRunId.Value, eventKey, grepTags, pointMap, failedTcIds, ct);
            }

            // 5 — Persist run counts + paths
            run.Passed     = result.Passed;
            run.Failed     = result.Failed;
            run.Skipped    = result.Skipped;
            run.DurationMs = result.DurationMs;
            run.JunitPath  = result.JunitPath;
            run.ReportPath = result.ReportPath;
            run.FinishedAt = DateTime.UtcNow;
            run.Status     = result.Failed > 0 ? TestRunStatus.Failed : TestRunStatus.Passed;

            // 6 — Persist failure records (triage classification deferred to QAL-035)
            foreach (var (failure, tcId) in failureTcById)
            {
                _db.TestFailures.Add(new TestFailure
                {
                    RunId            = runId,
                    TestTitle        = failure.TestTitle,
                    AdoTestCaseId    = tcId,
                    Classification   = FailureClassification.TestIssue,
                    Confidence       = "low",
                    Reason           = failure.ErrorMessage ?? "",
                });
            }

            // Publish the terminal event BEFORE the final commit so the captured narrative below
            // includes the "complete" line the Console tab replays after the stream closes.
            Pub(eventKey, EventKinds.System, "run",
                $"Run {runId} complete — {run.Passed} passed · {run.Failed} failed · {run.Skipped} skipped",
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
            _log.LogError(e, "TestRun {RunId} failed", runId);
            run.Status = TestRunStatus.Error;
            run.ErrorMessage = e.Message;
            run.FinishedAt = DateTime.UtcNow;
            run.EventLogJson = SerializeNarrative(eventKey);
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
        int adoTestRunId, string eventKey,
        IReadOnlyList<int> allTcIds, IReadOnlyDictionary<int, int> pointMap,
        IReadOnlySet<int> failedTcIds,
        CancellationToken ct)
    {
        try
        {
            // ADO auto-created one result per point when the run was created; read them so we can
            // PATCH each with its final outcome (testPointId → resultId).
            var pointToResult = await _ado.GetAdoTestResultIdsAsync(adoTestRunId, ct);
            var updates = new List<AdoTestResultUpdate>();
            foreach (var tcId in allTcIds)
            {
                if (!pointMap.TryGetValue(tcId, out var ptId)) continue;
                if (!pointToResult.TryGetValue(ptId, out var resultId)) continue;
                var outcome = failedTcIds.Contains(tcId) ? "Failed" : "Passed";
                updates.Add(new AdoTestResultUpdate(resultId, outcome, null, 0));
            }

            await _ado.UpdateAdoTestResultsAsync(adoTestRunId, updates, ct);
            await _ado.CompleteAdoTestRunAsync(adoTestRunId, ct);
            var passed = updates.Count(u => u.Outcome == "Passed");
            Pub(eventKey, EventKinds.Result, "run",
                $"ADO Test Run #{adoTestRunId} completed — {passed}/{updates.Count} passed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ADO Test Run result posting failed for run {Id}", adoTestRunId);
            Pub(eventKey, EventKinds.Error, "run",
                $"ADO Test Run #{adoTestRunId} result posting failed: {ex.Message}", EventLevels.Warn);
        }
    }

    /// <summary>Map one failed test to its ADO Test Case id: prefer the spec-file candidates (title
    /// similarity when a file hosts multiple cases), falling back to a legacy [tcId] title tag.</summary>
    private static int? ResolveFailureTcId(
        FailedTestResult failure,
        IReadOnlyDictionary<string, List<(int TcId, string Title)>> specPathDrafts)
    {
        int? tcId = null;
        if (failure.SpecFilePath != null
            && specPathDrafts.TryGetValue(SpecKey(failure.SpecFilePath), out var candidates))
        {
            tcId = candidates.Count == 1
                ? candidates[0].TcId
                : candidates
                    .OrderByDescending(c => TitleSimilarity(c.Title, failure.TestTitle))
                    .First().TcId;
        }
        return tcId ?? ExtractTcId(failure.TestTitle);
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

    private static string NormPath(string p) => p.Replace('\\', '/');

    /// <summary>Match key for a spec file: the file name only (lowercased). The draft stores an
    /// absolute path while the JUnit reporter emits a relative one, so we compare basenames.</summary>
    private static string SpecKey(string p) => Path.GetFileName(NormPath(p)).ToLowerInvariant();

    /// <summary>Simple word-overlap score used to pick the best-matching draft when multiple
    /// endpoint cases share one spec file. Higher = more words in common.</summary>
    private static int TitleSimilarity(string draftTitle, string testTitle)
    {
        var dt = draftTitle.ToLowerInvariant();
        var tt = testTitle.ToLowerInvariant();
        return tt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                 .Count(w => dt.Contains(w, StringComparison.Ordinal));
    }

    private static int? ExtractTcId(string testTitle)
    {
        var m = TcIdRegex.Match(testTitle);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
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
        catch { }
    }
}
