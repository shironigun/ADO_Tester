using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ILlmProviderState _state;

    public SettingsController(ILlmProviderState state) => _state = state;

    /// <summary>GET /api/settings/provider — current provider + the full list.</summary>
    [HttpGet("provider")]
    public IActionResult GetProvider() => Ok(new
    {
        current = _state.Current.ToString(),
        available = _state.Available.Select(p => p.ToString()),
    });

    public sealed record SetProviderRequest(string Provider);

    /// <summary>PUT /api/settings/provider — switch the active provider at runtime.</summary>
    [HttpPut("provider")]
    public IActionResult SetProvider([FromBody] SetProviderRequest request)
    {
        if (!Enum.TryParse<LlmProvider>(request.Provider, ignoreCase: true, out var provider))
            return BadRequest($"Unknown provider '{request.Provider}'.");

        _state.Set(provider);
        return Ok(new { current = _state.Current.ToString() });
    }
}
