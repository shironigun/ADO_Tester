using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Api.Streaming;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.QaRuns;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/qaloop/runs")]
public sealed class QaRunsController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IResultsIngestService _ingest;
    private readonly QaDbContext _db;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly IOptions<AzureDevOpsOptions> _adoOpts;
    private readonly ITeamsNotifier _teams;
    private readonly RunExecutionService _runExec;
    private readonly IPlaywrightRunner _runner;
    private readonly ILogger<QaRunsController> _log;

    public QaRunsController(
        IServiceScopeFactory scopeFactory, IEventBus bus, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        IResultsIngestService ingest, QaDbContext db, IOptions<QaLoopOptions> opts,
        IOptions<AzureDevOpsOptions> adoOpts, ITeamsNotifier teams, RunExecutionService runExec,
        IPlaywrightRunner runner, ILogger<QaRunsController> log)
    {
        _runExec = runExec;
        _runner = runner;
        _scopeFactory = scopeFactory;
        _bus = bus;
        _ado = ado;
        _llmFactory = llmFactory;
        _ingest = ingest;
        _db = db;
        _opts = opts;
        _adoOpts = adoOpts;
        _teams = teams;
        _log = log;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] RunStartRequest req, CancellationToken ct)
    {
        // Resolve project id from the first QA Loop session (single-project assumption)
        var projectId = await _db.ApiTestSessions.Select(s => s.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId == 0) projectId = 1; // fallback to seed id

        // Resolve profile
        if (!Enum.TryParse<DmsProfile>(req.Profile, ignoreCase: true, out var profile) &&
            !TryParseProfileWire(req.Profile, out profile))
            profile = DmsProfile.Infinity;

        var run = new TestRun
        {
            ProjectId = projectId,
            Module = req.ModuleFilter,
            LevelFilter = req.LevelFilter,
            TagFilter = req.TagFilter,
            Profile = profile,
            TriggerSource = TriggerSource.Manual,
            Status = TestRunStatus.Queued,
            ActivityEnabled = _opts.Value.ActivityEnabled,
            CreatedAt = DateTime.UtcNow,
        };

        if (req.DraftIds?.Count > 0)
        {
            var impactAnalysis = new ImpactAnalysis
            {
                ImpactedDraftIds = req.DraftIds,
                Reasoning = "Manual draft selection",
                Confidence = "high",
            };
            run.ImpactAnalysisJson = System.Text.Json.JsonSerializer.Serialize(impactAnalysis);
        }

        _db.TestRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var runId = run.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<RunExecutionService>();
                await svc.ExecuteAsync(runId);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Run {RunId} background task failed", runId);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                    var r = await db.TestRuns.FindAsync(runId);
                    if (r is { Status: TestRunStatus.Queued or TestRunStatus.Running })
                    {
                        r.Status = TestRunStatus.Failed;
                        r.ErrorMessage = e.Message;
                        r.FinishedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
                catch { /* best-effort status update */ }
            }
        });

        return StatusCode(202, new RunStartResponse { RunId = runId });
    }

    [HttpGet("{runId:int}/stream")]
    public async Task Stream(int runId, [FromQuery] string? lastEventId, CancellationToken ct)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"run:{runId}", afterSeq, ct);
    }

    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> ListRuns(
        int projectId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);
        try
        {
            var q = _db.TestRuns.Where(r => r.ProjectId == projectId);
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(r => r.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
            return Ok(new TestRunList { Total = total, Items = items.Select(TestRunSummary.From).ToList() });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpGet("detail/{runId:int}")]
    public async Task<IActionResult> Detail(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.TestRuns.FindAsync([runId], ct);
            if (run is null) return StatusCode(404, new { detail = "Run not found." });
            var failures = await _db.TestFailures.Where(f => f.RunId == runId).ToListAsync(ct);
            return Ok(TestRunDetail.From(run, failures));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Re-posts the run-complete Teams card for a run. ?template=compact|developer|scheduled
    /// posts that one; omit to post all three (template preview/compare).</summary>
    [HttpPost("{runId:int}/notify")]
    public async Task<IActionResult> Notify(int runId, [FromQuery] string? template, CancellationToken ct)
    {
        var run = await _db.TestRuns.FindAsync([runId], ct);
        if (run is null) return StatusCode(404, new { detail = "Run not found." });

        // Backfill for runs that finished before per-level stats were persisted.
        if (string.IsNullOrEmpty(run.LevelBreakdownJson))
        {
            run.LevelBreakdownJson = await _runExec.ComputeLevelBreakdownAsync(run, ct);
            await _db.SaveChangesAsync(ct);
        }

        var templates = template is null
            ? new[] { "compact", "developer", "scheduled" }
            : new[] { template };
        foreach (var t in templates)
            await _teams.NotifyRunCompleteAsync(run, t, ct);
        return Ok(new { posted = templates });
    }

    /// <summary>Resolved impact analysis for the Impact tab: reasoning/confidence plus the
    /// impacted draft ids resolved to test titles and levels (raw ids are useless to a reader).</summary>
    [HttpGet("{runId:int}/impact")]
    public async Task<IActionResult> Impact(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.TestRuns.FindAsync([runId], ct);
            if (run is null) return StatusCode(404, new { detail = "Run not found." });

            ImpactAnalysis? impact = null;
            if (!string.IsNullOrEmpty(run.ImpactAnalysisJson))
                try { impact = System.Text.Json.JsonSerializer.Deserialize<ImpactAnalysis>(run.ImpactAnalysisJson); }
                catch { /* malformed → treat as no impact */ }
            if (impact is null) return Ok(new { hasImpact = false });

            var ids = impact.ImpactedDraftIds;
            var tests = await _db.ApiTestDrafts
                .Where(d => ids.Contains(d.Id))
                .OrderBy(d => d.Level).ThenBy(d => d.Title)
                .Select(d => new { d.Id, d.Title, Level = d.Level.ToString(), d.AdoTestCaseId })
                .ToListAsync(ct);

            return Ok(new
            {
                hasImpact = true,
                confidence = impact.Confidence,
                reasoning = impact.Reasoning,
                fallback = impact.Fallback,
                skippedReason = impact.SkippedReason,
                impactedCount = ids.Count,
                trigger = new
                {
                    source = run.TriggerSource.ToWire(),
                    buildId = run.TriggerBuildId,
                    commitSha = run.TriggerCommitSha,
                    pipelineId = run.TriggerPipelineId,
                },
                tests,
            });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Re-enrich + regenerate the Allure report for a past run (level grouping, failure
    /// categories, AI failure-analysis attachments). No Playwright execution, no ADO writes.</summary>
    [HttpPost("{runId:int}/report/rebuild")]
    public async Task<IActionResult> RebuildReport(int runId, CancellationToken ct)
    {
        var run = await _db.TestRuns.FindAsync([runId], ct);
        if (run is null) return StatusCode(404, new { detail = "Run not found." });

        try
        {
            var spec = new PlaywrightRunSpec
            {
                RunId = runId,
                Module = run.Module ?? "",
                Profile = run.Profile.ToWire(),
                LevelFilter = run.LevelFilter,
                TriggerSourceWire = run.TriggerSource.ToWire(),
                TriggerPipelineId = run.TriggerPipelineId,
                TriggerBuildId = run.TriggerBuildId,
                TriggerCommitSha = run.TriggerCommitSha,
                AdoTestRunId = run.AdoTestRunId,
                ImpactAnalysisJson = run.ImpactAnalysisJson,
                ActivityEnabled = run.ActivityEnabled,
            };
            var path = await _runner.RebuildReportAsync(spec, $"run:{runId}", ct);
            if (path is null) return StatusCode(404, new { detail = "No allure-results preserved for this run." });

            run.ReportPath = path;
            await _db.SaveChangesAsync(ct);
            return Ok(new { reportPath = path, reportUrl = $"/api/qaloop/runs/{runId}/report/" });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>AI triage of failed tests — populates Classification+BugDraftJson. Never files to ADO.</summary>
    [HttpPost("{runId:int}/triage")]
    public async Task<IActionResult> Triage(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.TestRuns.FindAsync([runId], ct);
            if (run is null) return StatusCode(404, new { detail = "Run not found." });
            var result = await _ingest.TriageAsync(runId, ct);
            var failures = await _db.TestFailures.Where(f => f.RunId == runId).ToListAsync(ct);
            return Ok(new { result.TriagedCount, result.RealBugCount, Failures = failures.Select(TestFailureDto.From) });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>File selected failures as ADO Bugs from their pre-built BugDraftJson. No second LLM call.</summary>
    [HttpPost("{runId:int}/file-bugs")]
    public async Task<IActionResult> FileBugs(
        int runId, [FromBody] FileBugsRequest req, CancellationToken ct)
    {
        if (req.FailureIds.Count == 0)
            return StatusCode(400, new { detail = "failureIds must not be empty." });
        try
        {
            var run = await _db.TestRuns.FindAsync([runId], ct);
            if (run is null) return StatusCode(404, new { detail = "Run not found." });
            var result = await _ingest.FileBugsAsync(runId, req.FailureIds, req.StoryId, ct);
            return Ok(new FileBugsResponse { FiledCount = result.FiledCount, BugIds = result.BugIds });
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Dropdown facets for the New Run launcher: modules that have at least one Pushed
    /// draft (i.e. a completed QA Loop with runnable specs), plus fixed level/profile enums,
    /// distinct known tags, and the AUTOMATION_PROFILE env default.</summary>
    [HttpGet("facets")]
    public async Task<IActionResult> Facets(CancellationToken ct)
    {
        try
        {
            var pushedSessionIds = await _db.ApiTestDrafts
                .Where(d => d.Status == ApiDraftStatus.Pushed)
                .Select(d => d.SessionId)
                .Distinct()
                .ToListAsync(ct);

            var modules = await _db.ApiTestSessions
                .Where(s => pushedSessionIds.Contains(s.Id) && s.Module != null && s.Module != "")
                .Select(s => s.Module)
                .Distinct()
                .OrderBy(m => m)
                .ToListAsync(ct);

            var tagJsons = await _db.ApiTestDrafts
                .Where(d => d.Status == ApiDraftStatus.Pushed && d.Tags != null)
                .Select(d => d.Tags!)
                .ToListAsync(ct);
            var tagSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in tagJsons)
            {
                try
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                    if (arr is null) continue;
                    foreach (var t in arr)
                        if (!string.IsNullOrWhiteSpace(t)) tagSet.Add(t.Trim());
                }
                catch { /* ponytail: skip malformed rows, not worth blocking dropdown */ }
            }

            var envDefault = Environment.GetEnvironmentVariable("AUTOMATION_PROFILE");
            var defaultProfile = string.IsNullOrWhiteSpace(envDefault)
                ? "infinity"
                : envDefault.Trim().ToLowerInvariant();

            return Ok(new
            {
                modules,
                levels = new[] { "contract", "endpoint", "workflow" },
                profiles = new[] { "ideal", "infinity", "aspen", "quantum" },
                tags = tagSet.ToList(),
                defaultProfile,
                adoBaseUrl = _adoOpts.Value.BaseUrl,
            });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("preview-impact")]
    public async Task<IActionResult> PreviewImpact([FromBody] PreviewImpactRequest req, CancellationToken ct)
    {
        try
        {
            var inventory = await _db.ApiTestDrafts
                .Where(d => d.Status == ApiDraftStatus.Pushed)
                .ToListAsync(ct);
            var sessionIds = inventory.Select(d => d.SessionId).Distinct().ToList();
            var moduleMap = await _db.ApiTestSessions
                .Where(s => sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Module, ct);

            var items = inventory.Select(d => new DraftInventoryItem(
                d.Id, moduleMap.GetValueOrDefault(d.SessionId, ""), d.Level.ToWire(),
                d.Title, TryDeserializeList(d.SourceRoutesJson), TryDeserializeList(d.Tags))).ToList();

            var llm = await _llmFactory.CreateAsync(ct);
            var result = await new ImpactAnalyzerAgent(llm).AnalyzeAsync(
                req.ChangedFiles, req.CommitMessages, items, ct: ct);
            return Ok(result);
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Serves the run's Allure/Playwright HTML report. Allure has no single-file bundling
    /// mode — its report is index.html plus a directory of chunked JS/CSS/font assets — so subPath
    /// serves the relative asset requests the browser makes after loading index.html at the bare
    /// route. dl=true zips the whole report directory instead of just index.html, since downloading
    /// index.html alone would be missing every asset it references.</summary>
    [HttpGet("{runId:int}/report/{**subPath}")]
    public async Task<IActionResult> Report(
        int runId, string? subPath, [FromQuery] bool dl = false, CancellationToken ct = default)
    {
        var run = await _db.TestRuns.FindAsync([runId], ct);
        if (run?.ReportPath is null || !System.IO.File.Exists(run.ReportPath))
            return NotFound(new { detail = "Report not available." });

        var reportDir = Path.GetDirectoryName(run.ReportPath)!;

        // Allure's index.html references its assets with bare relative paths (styles.css, app.js,
        // fonts). If the browser loads ".../report" WITHOUT a trailing slash, those resolve one level
        // too high (".../{runId}/styles.css") and 404. Redirect the bare route to the trailing-slash
        // form so every relative asset resolves under ".../report/". (dl bypasses this.)
        if (!dl && string.IsNullOrEmpty(subPath) && !(Request.Path.Value?.EndsWith('/') ?? false))
        {
            var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
            return Redirect($"{Request.Path.Value}/{query}");
        }

        if (dl)
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"qa-run-{runId}-report-{Guid.NewGuid():N}.zip");
            try
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(reportDir, zipPath);
                var bytes = await System.IO.File.ReadAllBytesAsync(zipPath, ct);
                return File(bytes, "application/zip", $"qa-run-{runId}-report.zip");
            }
            finally
            {
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
            }
        }

        var relative = string.IsNullOrEmpty(subPath) ? "index.html" : subPath;
        var fullPath = Path.GetFullPath(Path.Combine(reportDir, relative));
        if (!fullPath.StartsWith(Path.GetFullPath(reportDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, Path.GetFullPath(reportDir), StringComparison.OrdinalIgnoreCase))
            return NotFound();
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var typeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        if (!typeProvider.TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";
        return PhysicalFile(fullPath, contentType);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseProfileWire(string wire, out DmsProfile profile)
    {
        profile = QaLoopEnumExtensions.ToDmsProfile(wire);
        return true;
    }

    private static List<string> TryDeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
