using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Generation endpoints (Track A Generator) — flat under <c>/api/generation</c>. Turns a KB flow into a
/// runnable Playwright E2E spec and writes it into the greenfield <c>/automation</c> folder. Errors are
/// <c>{ detail }</c> (parity with the other controllers).
/// </summary>
[ApiController]
[Route("api/generation")]
public sealed class GenerationController : ControllerBase
{
    private readonly IGenerationService _gen;
    private readonly ILogger<GenerationController> _log;

    public GenerationController(IGenerationService gen, ILogger<GenerationController> log)
    {
        _gen = gen;
        _log = log;
    }

    /// <summary>Generate a Playwright E2E spec from a KB flow and (optionally) write it into /automation.</summary>
    [HttpPost("{projectId:int}/spec")]
    public async Task<IActionResult> GenerateSpec(
        int projectId, [FromBody] GenerateSpecRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return StatusCode(400, new { detail = "Request body is required." });
        try
        {
            return Ok(await _gen.GenerateE2eSpecAsync(projectId, request, cancellationToken));
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
            _log.LogError(e, "Spec generation failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
