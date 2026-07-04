using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Results endpoints (Track A triage loop) — flat under <c>/api/results</c>. Ingests a Playwright JUnit
/// run, triages each failure, and files house-style Bugs for real bugs. Errors are <c>{ detail }</c>.
/// </summary>
[ApiController]
[Route("api/results")]
public sealed class ResultsController : ControllerBase
{
    private readonly IResultsIngestService _ingest;
    private readonly ILogger<ResultsController> _log;

    public ResultsController(IResultsIngestService ingest, ILogger<ResultsController> log)
    {
        _ingest = ingest;
        _log = log;
    }

    /// <summary>Ingest a JUnit run → triage failures → file Bugs (set <c>fileBugs=false</c> for a dry run).</summary>
    [HttpPost("{projectId:int}/ingest")]
    public async Task<IActionResult> Ingest(
        int projectId, [FromBody] IngestResultsRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return StatusCode(400, new { detail = "Request body is required." });
        try
        {
            return Ok(await _ingest.IngestAsync(projectId, request, cancellationToken));
        }
        catch (ArgumentException e)
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (FileNotFoundException e)
        {
            return StatusCode(404, new { detail = e.Message });
        }
        catch (System.Xml.XmlException e)
        {
            return StatusCode(400, new { detail = $"Invalid JUnit XML: {e.Message}" });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Results ingest failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
