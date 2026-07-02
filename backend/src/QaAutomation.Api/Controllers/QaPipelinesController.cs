using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Core;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.QaPipelines;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/qaloop/pipelines")]
public sealed class QaPipelinesController : ControllerBase
{
    private readonly QaDbContext _db;
    private readonly PipelineWatcherHostedService _watcher;
    private readonly ILogger<QaPipelinesController> _log;

    public QaPipelinesController(QaDbContext db, PipelineWatcherHostedService watcher, ILogger<QaPipelinesController> log)
    {
        _db = db;
        _watcher = watcher;
        _log = log;
    }

    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> ListWatches(int projectId, CancellationToken ct)
    {
        try
        {
            var items = await _db.PipelineWatches.Where(w => w.ProjectId == projectId)
                .OrderByDescending(w => w.CreatedAt).ToListAsync(ct);
            return Ok(new PipelineWatchList { Total = items.Count, Items = items.Select(PipelineWatchDto.From).ToList() });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("{projectId:int}")]
    public async Task<IActionResult> AddWatch(int projectId, [FromBody] AddWatchRequest req, CancellationToken ct)
    {
        try
        {
            var watch = new PipelineWatch
            {
                ProjectId = projectId,
                AdoPipelineId = req.AdoPipelineId,
                AdoPipelineName = req.AdoPipelineName,
                Module = req.Module,
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.PipelineWatches.Add(watch);
            await _db.SaveChangesAsync(ct);
            return StatusCode(201, PipelineWatchDto.From(watch));
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException e) when (
            e.InnerException?.Message.Contains("UNIQUE") == true)
        {
            return StatusCode(409, new { detail = $"A watch for pipeline {req.AdoPipelineId} already exists in this project." });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPatch("{watchId:int}")]
    public async Task<IActionResult> UpdateWatch(int watchId, [FromBody] UpdateWatchRequest req, CancellationToken ct)
    {
        try
        {
            var watch = await _db.PipelineWatches.FindAsync([watchId], ct);
            if (watch is null) return StatusCode(404, new { detail = "Watch not found." });
            if (req.Enabled.HasValue) watch.Enabled = req.Enabled.Value;
            if (req.Module is not null) watch.Module = req.Module;
            watch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(PipelineWatchDto.From(watch));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpDelete("{watchId:int}")]
    public async Task<IActionResult> DeleteWatch(int watchId, CancellationToken ct)
    {
        try
        {
            var watch = await _db.PipelineWatches.FindAsync([watchId], ct);
            if (watch is null) return StatusCode(404, new { detail = "Watch not found." });
            _db.PipelineWatches.Remove(watch);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("{watchId:int}/run-latest")]
    public async Task<IActionResult> RunLatest(int watchId, CancellationToken ct)
    {
        try
        {
            var watch = await _db.PipelineWatches.FindAsync([watchId], ct);
            if (watch is null) return StatusCode(404, new { detail = "Watch not found." });
            var runId = await _watcher.RunLatestAsync(watchId, ct);
            return StatusCode(202, new RunLatestResponse { RunId = runId, BuildId = watch.LastSeenBuildId });
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("{watchId:int}/skip-to-latest")]
    public async Task<IActionResult> SkipToLatest(int watchId, CancellationToken ct)
    {
        try
        {
            var buildId = await _watcher.SkipToLatestAsync(watchId, ct);
            var watch = await _db.PipelineWatches.FindAsync([watchId], ct);
            return Ok(new { buildId, watch = watch is null ? null : PipelineWatchDto.From(watch) });
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }
}
