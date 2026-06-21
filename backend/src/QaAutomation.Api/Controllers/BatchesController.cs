using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Batch (Epic/Feature run) endpoints (spec B8) — flat under <c>/api/batches</c>. children-expand,
/// fan-out start (200, synchronous — returns ids), detail, cancel, and the paged list. Module is
/// <c>author-tests</c>/<c>find-bugs</c>. {detail} errors. Route precedence (literal <c>children-expand</c>/
/// <c>detail</c>/<c>cancel</c> vs the bare <c>{projectId}</c>) is handled by ASP.NET literal-vs-parameter ranking.
/// </summary>
[ApiController]
[Route("api/batches")]
public sealed class BatchesController : ControllerBase
{
    private readonly IBatchService _batches;
    private readonly ILogger<BatchesController> _log;

    public BatchesController(IBatchService batches, ILogger<BatchesController> log)
    {
        _batches = batches;
        _log = log;
    }

    [HttpGet("children-expand")]
    public async Task<IActionResult> ChildrenExpand([FromQuery] int parentWorkItemId, [FromQuery] bool recursive = true, CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _batches.ChildrenExpandAsync(parentWorkItemId, recursive, cancellationToken));
        }
        catch (AdoApiError e)
        {
            return e.Status == 404
                ? StatusCode(404, new { detail = $"Work item #{parentWorkItemId} not found." })
                : StatusCode(502, new { detail = e.Message });
        }
    }

    [HttpPost("{module}/{projectId:int}/start")]
    public async Task<IActionResult> Start(string module, int projectId, [FromBody] BatchStartRequest request, CancellationToken cancellationToken)
    {
        if (module is not ("author-tests" or "find-bugs"))
            return StatusCode(404, new { detail = "Unknown module." });
        if (!request.WorkItemIds.Any(i => i > 0))
            return StatusCode(400, new { detail = "No work items selected." });

        return Ok(await _batches.StartAsync(module, projectId, request, cancellationToken));
    }

    [HttpGet("detail/{batchId:int}")]
    public async Task<IActionResult> Detail(int batchId, CancellationToken cancellationToken)
    {
        var detail = await _batches.DetailAsync(batchId, cancellationToken);
        return detail is null ? StatusCode(404, new { detail = "Batch not found." }) : Ok(detail);
    }

    [HttpPost("{batchId:int}/cancel")]
    public async Task<IActionResult> Cancel(int batchId, CancellationToken cancellationToken)
    {
        var cancelled = await _batches.CancelAsync(batchId, cancellationToken);
        return cancelled is null
            ? StatusCode(404, new { detail = "Batch not found." })
            : Ok(new CancelResponse { Cancelled = cancelled.Value });
    }

    [HttpGet("{projectId:int}")]
    public async Task<IActionResult> List(int projectId, [FromQuery] int skip = 0, [FromQuery] int take = 30, CancellationToken cancellationToken = default)
        => Ok(await _batches.ListAsync(projectId, skip, take, cancellationToken));
}
