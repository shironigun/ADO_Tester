using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Claude CLI subscription auth for the web UI — flat under <c>/api/claude-auth</c>. Lets an app user
/// sign the local <c>claude</c> CLI in/out from the browser (open the URL, paste the code back) instead
/// of needing shell/`docker exec` access. Protected by the app login gate like every other endpoint.
/// </summary>
[ApiController]
[Route("api/claude-auth")]
public sealed class ClaudeAuthController : ControllerBase
{
    private readonly IClaudeAuthService _svc;

    public ClaudeAuthController(IClaudeAuthService svc) => _svc = svc;

    [HttpGet("status")]
    public Task<ClaudeAuthStatus> Status(CancellationToken cancellationToken)
        => _svc.GetStatusAsync(cancellationToken);

    [HttpPost("login/start")]
    public async Task<IActionResult> StartLogin(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _svc.StartLoginAsync(cancellationToken));
        }
        catch (Exception e)
        {
            return BadRequest(new { detail = e.Message });
        }
    }

    [HttpPost("login/complete")]
    public Task<ClaudeLoginResult> CompleteLogin([FromBody] SubmitClaudeCodeRequest request, CancellationToken cancellationToken)
        => _svc.SubmitCodeAsync(request.LoginId, request.Code, cancellationToken);

    [HttpPost("logout")]
    public Task<ClaudeAuthStatus> Logout(CancellationToken cancellationToken)
        => _svc.LogoutAsync(cancellationToken);
}
