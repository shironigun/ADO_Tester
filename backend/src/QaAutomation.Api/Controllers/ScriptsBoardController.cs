using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Api.Streaming;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Scripts Board endpoints (Slab 3, QLV2-303) — flat under <c>/api/scripts-board</c>. Read-only
/// inventory reconcile + ad-hoc execution of the generated automation framework, independent of QA
/// Runs' session/pipeline-driven flow. Background-task-with-own-DI-scope pattern mirrors
/// <c>QaRunsController.Start</c> exactly; cancellation reuses the same <see cref="RunCancellationRegistry"/>
/// singleton keyed by <c>-runId</c> (negative) so it can never collide with a positive <c>TestRun</c> id.
/// </summary>
[ApiController]
[Route("api/scripts-board")]
public sealed class ScriptsBoardController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScriptInventoryService _inventory;
    private readonly IImpactMapService _impactMap;
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly RunCancellationRegistry _runCancel;
    private readonly ILogger<ScriptsBoardController> _log;

    public ScriptsBoardController(
        IServiceScopeFactory scopeFactory, IScriptInventoryService inventory, IImpactMapService impactMap,
        IEventBus bus, QaDbContext db, RunCancellationRegistry runCancel, ILogger<ScriptsBoardController> log)
    {
        _scopeFactory = scopeFactory;
        _inventory = inventory;
        _impactMap = impactMap;
        _bus = bus;
        _db = db;
        _runCancel = runCancel;
        _log = log;
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory(CancellationToken ct)
    {
        try
        {
            var items = await _inventory.GetInventoryAsync(ct);
            var dtos = items.Select(ScriptInventoryItemDto.From).ToList();
            return Ok(new ScriptInventoryResponse { Total = dtos.Count, Items = dtos });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>
    /// Seed/extend product <c>automation/resources/impact-map.json</c> from read-only
    /// <c>*/build/build-qa.yml</c> path includes (INT-401). Writes only under AUTOMATION_ROOT.
    /// </summary>
    [HttpPost("impact/seed")]
    public IActionResult SeedImpactMap()
    {
        try
        {
            var result = _impactMap.SeedFromBuildQa();
            return Ok(result);
        }
        catch (InvalidOperationException e) { return StatusCode(400, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Path-impact simulate: changed files → Playwright grep tags (mirrors run-impacted.ts).</summary>
    [HttpPost("impact/simulate")]
    public IActionResult SimulateImpact([FromBody] ImpactSimulateRequest req)
    {
        try
        {
            var files = req.ChangedFiles ?? [];
            return Ok(_impactMap.Simulate(files));
        }
        catch (InvalidOperationException e) { return StatusCode(400, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Read-only spec body for the power-console preview drawer. Path must appear in the
    /// live inventory (prevents arbitrary filesystem reads).</summary>
    [HttpGet("spec-content")]
    public async Task<IActionResult> SpecContent([FromQuery] string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return StatusCode(400, new { detail = "path is required." });
        try
        {
            var items = await _inventory.GetInventoryAsync(ct);
            var match = items.FirstOrDefault(i =>
                string.Equals(i.SpecFilePath, path, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return StatusCode(404, new { detail = "Spec is not in inventory." });
            if (!System.IO.File.Exists(match.SpecFilePath))
                return StatusCode(404, new { detail = "Spec file missing on disk." });

            var code = await System.IO.File.ReadAllTextAsync(match.SpecFilePath, ct);
            return Ok(new ScriptSpecContentResponse
            {
                SpecFilePath = match.SpecFilePath,
                Code = code,
                DraftId = match.DraftId,
                AdoTestCaseId = match.AdoTestCaseId,
                ReconcileState = match.ReconcileState,
                Meta = match.Meta,
            });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("runs")]
    public async Task<IActionResult> StartRun([FromBody] ScriptBoardRunStartRequest req, CancellationToken ct)
    {
        var filter = new ScriptBoardRunFilter(
            req.Module, req.Level, req.Type, req.Bucket, req.Tag, req.SpecFilePaths, req.Headed, req.Workers,
            req.Profile);

        int runId;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IScriptBoardExecutionService>();
            runId = await svc.StartAsync(filter, ct);
        }
        catch (ArgumentException e) { return StatusCode(400, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }

        // Register a cancellation source keyed by -runId (negative) BEFORE launching the background
        // task, so a same-tick POST {runId}/cancel can never race ahead of registration.
        var cts = _runCancel.Register(-runId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IScriptBoardExecutionService>();
                await svc.ExecuteAsync(runId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // ExecuteAsync already set Status=Cancelled before rethrowing.
            }
            catch (Exception e)
            {
                _log.LogError(e, "Scripts Board run {RunId} background task failed", runId);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                    var r = await db.ScriptBoardRuns.FindAsync(runId);
                    if (r is { Status: TestRunStatus.Queued or TestRunStatus.Running })
                    {
                        r.Status = TestRunStatus.Error;
                        r.ErrorMessage = e.Message;
                        r.FinishedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
                catch { /* best-effort status update */ }
            }
            finally
            {
                _runCancel.Remove(-runId);
            }
        });

        return StatusCode(202, new ScriptBoardRunStartResponse { RunId = runId });
    }

    [HttpGet("runs")]
    public async Task<IActionResult> ListRuns(
        [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);
        try
        {
            var q = _db.ScriptBoardRuns.AsQueryable();
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(r => r.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
            return Ok(new ScriptBoardRunList { Total = total, Items = items.Select(ScriptBoardRunSummary.From).ToList() });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpGet("runs/{runId:int}")]
    public async Task<IActionResult> Detail(int runId, CancellationToken ct)
    {
        var run = await _db.ScriptBoardRuns.FindAsync([runId], ct);
        if (run is null) return StatusCode(404, new { detail = "Run not found." });
        return Ok(ScriptBoardRunDetail.From(run));
    }

    [HttpGet("runs/{runId:int}/stream")]
    public async Task Stream(int runId, [FromQuery] string? lastEventId, CancellationToken ct)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"scripts-board-run:{runId}", afterSeq, ct);
    }

    /// <summary>Cancel an in-flight run. Mirrors <c>QaRunsController.Cancel</c> — signals the registered
    /// token (keyed by -runId), or flips the row directly if the process was orphaned by a restart.</summary>
    [HttpPost("runs/{runId:int}/cancel")]
    public async Task<IActionResult> Cancel(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.ScriptBoardRuns.FindAsync([runId], ct);
            if (run is null) return StatusCode(404, new { detail = "Run not found." });
            if (run.Status is not (TestRunStatus.Queued or TestRunStatus.Running))
                return StatusCode(409, new { detail = $"Run is {run.Status} — only a Queued or Running run can be cancelled." });

            var signalled = _runCancel.Cancel(-runId);
            if (!signalled)
            {
                run.Status = TestRunStatus.Cancelled;
                run.ErrorMessage = "Cancelled by user.";
                run.FinishedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return Ok(new { cancelled = true });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    /// <summary>Serves the Scripts Board run's Playwright/Allure HTML report (same semantics as
    /// <c>QaRunsController.Report</c> — trailing-slash redirect + optional zip download).</summary>
    [HttpGet("runs/{runId:int}/report/{**subPath}")]
    public async Task<IActionResult> Report(
        int runId, string? subPath, [FromQuery] bool dl = false, CancellationToken ct = default)
    {
        var run = await _db.ScriptBoardRuns.FindAsync([runId], ct);
        if (run?.ReportPath is null || !System.IO.File.Exists(run.ReportPath))
            return NotFound(new { detail = "Report not available." });

        var reportDir = Path.GetDirectoryName(run.ReportPath)!;

        if (!dl && string.IsNullOrEmpty(subPath) && !(Request.Path.Value?.EndsWith('/') ?? false))
        {
            var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
            return Redirect($"{Request.Path.Value}/{query}");
        }

        if (dl)
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"scripts-board-run-{runId}-report-{Guid.NewGuid():N}.zip");
            try
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(reportDir, zipPath);
                var bytes = await System.IO.File.ReadAllBytesAsync(zipPath, ct);
                return File(bytes, "application/zip", $"scripts-board-run-{runId}-report.zip");
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
}
