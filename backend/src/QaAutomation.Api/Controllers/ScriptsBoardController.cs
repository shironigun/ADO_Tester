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
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly RunCancellationRegistry _runCancel;
    private readonly ILogger<ScriptsBoardController> _log;

    public ScriptsBoardController(
        IServiceScopeFactory scopeFactory, IScriptInventoryService inventory, IEventBus bus,
        QaDbContext db, RunCancellationRegistry runCancel, ILogger<ScriptsBoardController> log)
    {
        _scopeFactory = scopeFactory;
        _inventory = inventory;
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

    [HttpPost("runs")]
    public async Task<IActionResult> StartRun([FromBody] ScriptBoardRunStartRequest req, CancellationToken ct)
    {
        var filter = new ScriptBoardRunFilter(
            req.Module, req.Level, req.Type, req.Bucket, req.Tag, req.SpecFilePaths, req.Headed, req.Workers);

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
}
