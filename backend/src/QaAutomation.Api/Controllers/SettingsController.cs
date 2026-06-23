using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Settings + provider + ADO discovery (spec B9) — flat under <c>/api/settings</c>. <c>/provider</c>
/// uses the camelCase ProviderInfo; <c>/config</c> and the discovery routes return RAW literal dotted-key
/// dicts (dictionary keys bypass camelCase — only nested object props are camelCased). {detail} errors.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    // Frozen suggestion list + notes for the claude-cli model picker.
    private static readonly string[] ClaudeModelSuggestions =
        ["sonnet", "opus", "haiku", "claude-sonnet-4-5", "claude-sonnet-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-haiku-4-6"];
    private const string ClaudeModelNotes =
        "Aliases like 'sonnet'/'opus'/'haiku' auto-resolve to the current latest. Leave blank to use whatever the CLI defaults to (matches your Claude Code config).";

    private readonly ILlmProviderState _providerState;
    private readonly IAppSettingsService _settings;
    private readonly IAzureDevOpsClient _ado;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOptions;
    private readonly ILogger<SettingsController> _log;

    public SettingsController(
        ILlmProviderState providerState, IAppSettingsService settings, IAzureDevOpsClient ado,
        IOptionsMonitor<AzureDevOpsOptions> adoOptions, ILogger<SettingsController> log)
    {
        _providerState = providerState;
        _settings = settings;
        _ado = ado;
        _adoOptions = adoOptions;
        _log = log;
    }

    [HttpGet("provider")]
    public async Task<ProviderInfo> GetProvider(CancellationToken cancellationToken)
        => await BuildProviderInfoAsync(cancellationToken);

    [HttpPut("provider")]
    public async Task<IActionResult> SetProvider([FromBody] SetProviderRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.GetNames<LlmProvider>().Contains(request.Provider, StringComparer.Ordinal))
            return StatusCode(400, new { detail = $"Unknown provider '{request.Provider}'." });
        await _providerState.SetAsync(Enum.Parse<LlmProvider>(request.Provider), cancellationToken);
        return Ok(await BuildProviderInfoAsync(cancellationToken));
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        try { return Ok(await _settings.GetAllForUiAsync(cancellationToken)); }
        catch (Exception e) { _log.LogError(e, "Get settings failed"); return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] UpdateAppSettingsRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _settings.UpdateAsync(request.Values ?? new(), cancellationToken)); }
        catch (Exception e) { _log.LogError(e, "Update settings failed"); return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpGet("ado/orgs")]
    public async Task<IActionResult> AdoOrgs(CancellationToken cancellationToken)
    {
        try
        {
            var orgs = await _ado.DiscoverOrganizationsAsync(cancellationToken);
            return Ok(new { organizations = orgs, supported = true });
        }
        catch (Exception e)
        {
            _log.LogInformation("Org auto-discovery not available: {Error}", e.Message);
            return Ok(new { organizations = Array.Empty<string>(), supported = false });
        }
    }

    [HttpGet("ado/projects")]
    public async Task<IActionResult> AdoProjects([FromQuery] string? organization, CancellationToken cancellationToken)
    {
        var org = string.IsNullOrWhiteSpace(organization) ? _adoOptions.CurrentValue.Organization : organization;
        if (string.IsNullOrWhiteSpace(org))
            return StatusCode(400, new { detail = "No organization configured or supplied." });
        try
        {
            var projects = await _ado.DiscoverProjectsAsync(org, cancellationToken);
            return Ok(new { organization = org, projects });
        }
        catch (Exception e)
        {
            _log.LogWarning("ADO discover projects failed for {Org}: {Error}", org, e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    [HttpGet("claude-cli/models")]
    public IActionResult ClaudeModels()
        => Ok(new { suggestions = ClaudeModelSuggestions, notes = ClaudeModelNotes });

    [HttpGet("ado/area-paths")]
    public async Task<IActionResult> AdoAreaPaths([FromQuery] string? organization, [FromQuery] string? project, CancellationToken cancellationToken)
    {
        var org = string.IsNullOrWhiteSpace(organization) ? _adoOptions.CurrentValue.Organization : organization;
        var proj = string.IsNullOrWhiteSpace(project) ? _adoOptions.CurrentValue.Project : project;
        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(proj))
            return StatusCode(400, new { detail = "Organization and project required." });
        try
        {
            var areaPaths = await _ado.DiscoverAreaPathsAsync(org, proj, cancellationToken);
            return Ok(new { organization = org, project = proj, areaPaths });
        }
        catch (Exception e)
        {
            _log.LogWarning("ADO discover area paths failed for {Org} / {Proj}: {Error}", org, proj, e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    private async Task<ProviderInfo> BuildProviderInfoAsync(CancellationToken cancellationToken) => new()
    {
        Current = (await _providerState.CurrentAsync(cancellationToken)).ToString(),
        Available = _providerState.Available.Select(p => p.ToString()).ToList(),
    };
}
