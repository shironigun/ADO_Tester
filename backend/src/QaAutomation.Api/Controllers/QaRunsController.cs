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
    private readonly IServiceProvider _sp;
    private readonly IEventBus _bus;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IResultsIngestService _ingest;
    private readonly QaDbContext _db;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly ILogger<QaRunsController> _log;

    public QaRunsController(
        IServiceProvider sp, IEventBus bus, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        IResultsIngestService ingest, QaDbContext db, IOptions<QaLoopOptions> opts, ILogger<QaRunsController> log)
    {
        _sp = sp;
        _bus = bus;
        _ado = ado;
        _llmFactory = llmFactory;
        _ingest = ingest;
        _db = db;
        _opts = opts;
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
            using var scope = _sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<RunExecutionService>();
            try { await svc.ExecuteAsync(runId); }
            catch (Exception e) { _log.LogError(e, "Run {RunId} background task failed", runId); }
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

    /// <summary>Serve the run's HTML report (Allure single-file or Playwright html).</summary>
    [HttpGet("{runId:int}/report")]
    public async Task<IActionResult> Report(
        int runId, [FromQuery] bool dl = false, CancellationToken ct = default)
    {
        var run = await _db.TestRuns.FindAsync([runId], ct);
        if (run?.ReportPath is null || !System.IO.File.Exists(run.ReportPath))
            return NotFound(new { detail = "Report not available." });
        return dl
            ? PhysicalFile(run.ReportPath, "text/html; charset=utf-8", $"qa-run-{runId}-report.html")
            : PhysicalFile(run.ReportPath, "text/html; charset=utf-8");
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
