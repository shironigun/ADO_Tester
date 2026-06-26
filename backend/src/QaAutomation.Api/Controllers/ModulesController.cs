using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Module endpoints (Track A module run) — flat under <c>/api/modules</c>. Analyze a whole module:
/// source + existing plan/suite test cases → discovered flows + coverage map + a stored KB doc.
/// </summary>
[ApiController]
[Route("api/modules")]
public sealed class ModulesController : ControllerBase
{
    private readonly IModuleAnalysisService _analysis;
    private readonly ILogger<ModulesController> _log;

    public ModulesController(IModuleAnalysisService analysis, ILogger<ModulesController> log)
    {
        _analysis = analysis;
        _log = log;
    }

    [HttpPost("{projectId:int}/analyze")]
    public async Task<IActionResult> Analyze(
        int projectId, [FromBody] ModuleAnalyzeRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return StatusCode(400, new { detail = "Request body is required." });
        try
        {
            return Ok(await _analysis.AnalyzeAsync(projectId, request, cancellationToken));
        }
        catch (ArgumentException e)
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Module analysis failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    /// <summary>Generate a Playwright spec for ONE analyzed flow on demand: build its detailed KB, then emit
    /// the spec (covered flow tags its existing test case; a gap creates + tags a new ADO Test Case).</summary>
    [HttpPost("{projectId:int}/flow-spec")]
    public async Task<IActionResult> GenerateFlowSpec(
        int projectId, [FromBody] ModuleFlowSpecRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return StatusCode(400, new { detail = "Request body is required." });
        try
        {
            return Ok(await _analysis.GenerateFlowSpecAsync(projectId, request, cancellationToken));
        }
        catch (ArgumentException e)
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (FileNotFoundException e)
        {
            return StatusCode(404, new { detail = e.Message });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Module flow spec generation failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
