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
    private readonly ITeamsNotifier _teams;
    private readonly ILogger<RunExecutionService> _log;

    public RunExecutionService(
        QaDbContext db, IAzureDevOpsClient ado, IPlaywrightRunner runner,
        IEventBus bus, ITeamsNotifier teams, ILogger<RunExecutionService> log)
    {
        _db = db;
        _ado = ado;
        _runner = runner;
        _bus = bus;
        _teams = teams;
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
                ImpactAnalysisJson = run.ImpactAnalysisJson,
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
            run.LevelBreakdownJson = BuildLevelBreakdownJson(drafts, failedTcIds);
            run.Passed     = result.Passed;
            run.Failed     = result.Failed;
            run.Skipped    = result.Skipped;
            run.DurationMs = result.DurationMs;
            run.JunitPath  = result.JunitPath;
            run.ReportPath = result.ReportPath;
            run.FinishedAt = DateTime.UtcNow;
            run.Status     = result.Failed > 0 ? TestRunStatus.Failed : TestRunStatus.Passed;

            // 6 — Persist failure records with a deterministic bug draft each (no LLM), so every run
            // yields ready-to-file Draft Bugs. The manual "AI Triage" can later enrich/reclassify.
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
                    BugDraftJson     = BuildBugDraftJson(run.Module, failure.TestTitle, tcId, failure.ErrorMessage),
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

            // Teams card for every completed run (notifier is non-blocking + skips Cancelled/no-webhook).
            await _teams.NotifyRunCompleteAsync(run, ct: CancellationToken.None);
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
            await _teams.NotifyRunCompleteAsync(run, ct: CancellationToken.None);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Recompute the per-level breakdown for an existing run (backfill for runs that
    /// finished before LevelBreakdownJson existed — used by the Teams notify endpoint).</summary>
    public async Task<string> ComputeLevelBreakdownAsync(TestRun run, CancellationToken ct = default)
    {
        var drafts = await ResolveDraftsAsync(run, ct);
        var failedTcIds = (await _db.TestFailures
            .Where(f => f.RunId == run.Id && f.AdoTestCaseId != null)
            .Select(f => f.AdoTestCaseId!.Value)
            .ToListAsync(ct)).ToHashSet();
        return BuildLevelBreakdownJson(drafts, failedTcIds);
    }

    /// <summary>Per-level Executed/Passed/Failed from the run's draft set. Failures that never
    /// resolved to a test case id aren't attributed to a level, so per-level failed can undercount
    /// relative to the run total.</summary>
    private static string BuildLevelBreakdownJson(IReadOnlyList<ApiTestDraft> drafts, IReadOnlySet<int> failedTcIds)
    {
        var stats = drafts
            .GroupBy(d => d.Level)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var failed = g.Count(d => d.AdoTestCaseId is int id && failedTcIds.Contains(id));
                return new LevelStat(g.Key.ToString(), g.Count(), Math.Max(0, g.Count() - failed), failed);
            })
            .ToList();
        return JsonSerializer.Serialize(stats);
    }

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

    /// <summary>Build a deterministic Bug draft (same JSON shape TriageAsync produces + a little extra
    /// provenance) from a failed test so every run yields house-style, ready-to-file Draft Bugs without
    /// an LLM call. FileBugsAsync consumes Title/Severity/Preconditions/ReproSteps/Expected/Observations
    /// verbatim; the extra fields enrich the Draft Bugs card only. Mirrors the reference bug format
    /// (descriptive title · Preconditions · numbered Steps · Expected Result · Actual Result).</summary>
    private static string BuildBugDraftJson(string? module, string testTitle, int? tcId, string? errorMessage)
    {
        var mod = string.IsNullOrWhiteSpace(module) ? "QA" : module.Trim();
        var reason = FirstLine(errorMessage);
        var (method, route, caseDesc) = ParseTitle(testTitle);
        var (expectedCode, actualCode) = ParseStatusCodes(reason);
        var severity = InferSeverity(reason, actualCode);

        // Descriptive, problem-focused title (not "test X failed").
        string title;
        if (method is not null && route is not null && expectedCode is not null && actualCode is not null)
            title = $"{method} {route} returns HTTP {actualCode} instead of {expectedCode}";
        else if (method is not null && route is not null)
            title = $"{method} {route} — {caseDesc}";
        else
            title = caseDesc;

        var steps = new List<string> { "Authenticate against the QA environment and obtain a valid bearer token." };
        if (method is not null && route is not null)
            steps.Add($"Send a {method} request to {route} with the bearer token in the Authorization header.");
        else
            steps.Add($"Execute the automated API test: {caseDesc}.");
        steps.Add("Inspect the HTTP response status code and body.");

        var expected = expectedCode is not null
            ? $"The endpoint returns HTTP {expectedCode} with a response body that matches its contract."
            : "The endpoint behaves per its contract (correct status code and response shape).";

        var observations = new List<string>();
        if (actualCode is not null)
            observations.Add($"The endpoint returned HTTP {actualCode}.");
        observations.Add(string.IsNullOrWhiteSpace(reason) ? "The automated assertion failed." : reason);

        var draft = new
        {
            Title = title,
            Severity = severity,
            Preconditions = $"Logged in to the QA environment with access to the {mod} module; a valid bearer token is available.",
            ReproSteps = steps,
            Expected = expected,
            Observations = observations,
            // Card-only provenance (ignored by FileBugsAsync):
            FoundBy = "Filed from a failed QA Loop Playwright run.",
            AutomatedTest = caseDesc,
            AdoTestCaseId = tcId,
        };
        return JsonSerializer.Serialize(draft);
    }

    /// <summary>Pull "{METHOD} {route}" (from the "Endpoint:/Contract:/Workflow:" describe segment) and
    /// the human case description (last "›" segment) out of a Playwright test title.</summary>
    private static (string? Method, string? Route, string CaseDesc) ParseTitle(string title)
    {
        var segs = title.Split('\u203a').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var subject = segs.FirstOrDefault(s =>
            s.StartsWith("Endpoint:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Contract:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Workflow:", StringComparison.OrdinalIgnoreCase)) ?? "";
        var caseDesc = CleanTestTitle(title);
        string? method = null, route = null;
        if (subject.Contains(':'))
        {
            var body = subject[(subject.IndexOf(':') + 1)..].Trim();
            var m = MethodRouteRegex.Match(body);
            if (m.Success) { method = m.Groups[1].Value.ToUpperInvariant(); route = "/" + m.Groups[2].Value.TrimStart('/'); }
        }
        return (method, route, caseDesc);
    }

    private static (int? Expected, int? Actual) ParseStatusCodes(string reason)
    {
        int? exp = null, act = null;
        var e = Regex.Match(reason, @"Expected:\s*(\d{3})");
        if (e.Success) exp = int.Parse(e.Groups[1].Value);
        var r = Regex.Match(reason, @"Received:\s*(\d{3})");
        if (r.Success) act = int.Parse(r.Groups[1].Value);
        return (exp, act);
    }

    /// <summary>Strip the "[chromium] › file:line › describe › " scaffolding and the [&lt;tcId-pending&gt;]
    /// placeholder, leaving the human-readable test title.</summary>
    private static string CleanTestTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Automated test";
        var parts = title.Split('\u203a');                       // '›'
        var last = parts.Length > 0 ? parts[^1].Trim() : title.Trim();
        last = TcPendingRegex.Replace(last, "").Trim();          // drop [<tcId-pending[:N]>]
        last = TcIdRegex.Replace(last, "").Trim();               // drop a leading [12345]
        return last.Length == 0 ? title.Trim() : last;
    }

    private static string FirstLine(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Split('\n').FirstOrDefault()?.Trim() ?? "";

    private static string InferSeverity(string reason, int? actualCode)
    {
        // Return the plain words CreateBugAsync maps to ADO severities ("High" → "2 - High", etc.).
        if (actualCode is >= 500 and < 600) return "High";
        if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
            return "High";
        return "Medium";
    }

    private static readonly Regex TcPendingRegex =
        new(@"\[<tcId-pending(?::\d+)?>\]", RegexOptions.Compiled);
    private static readonly Regex MethodRouteRegex =
        new(@"^(GET|POST|PUT|DELETE|PATCH)\s+(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
