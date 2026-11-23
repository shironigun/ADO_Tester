using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Infrastructure.Llm;

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
    private const string ClaudeModelNotes =
        "Aliases like 'sonnet'/'opus'/'haiku' auto-resolve to the current latest. Leave blank to use whatever the CLI defaults to (matches your Claude Code config).";
    private const string CopilotModelNotes =
        "Leave blank to use the CLI's default. Run `copilot -p \"List all models\" --allow-all-tools` in a terminal to see your current entitlements — model availability depends on your Copilot plan.";

    private readonly ILlmProviderState _providerState;
    private readonly IAppSettingsService _settings;
    private readonly IAzureDevOpsClient _ado;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOptions;
    private readonly IOptionsMonitor<AnthropicOptions> _anthropic;
    private readonly IOptionsMonitor<AzureOpenAiOptions> _azure;
    private readonly IOptionsMonitor<OpenAiCompatibleOptions> _openai;
    private readonly IOptionsMonitor<OllamaOptions> _ollama;
    private readonly IOptionsMonitor<ClaudeCliOptions> _claudeCli;
    private readonly IOptionsMonitor<CopilotCliOptions> _copilotCli;
    private readonly IOptionsMonitor<CursorCliOptions> _cursorCli;
    private readonly ILogger<SettingsController> _log;

    public SettingsController(
        ILlmProviderState providerState, IAppSettingsService settings, IAzureDevOpsClient ado,
        IOptionsMonitor<AzureDevOpsOptions> adoOptions,
        IOptionsMonitor<AnthropicOptions> anthropic,
        IOptionsMonitor<AzureOpenAiOptions> azure,
        IOptionsMonitor<OpenAiCompatibleOptions> openai,
        IOptionsMonitor<OllamaOptions> ollama,
        IOptionsMonitor<ClaudeCliOptions> claudeCli,
        IOptionsMonitor<CopilotCliOptions> copilotCli,
        IOptionsMonitor<CursorCliOptions> cursorCli,
        ILogger<SettingsController> log)
    {
        _providerState = providerState;
        _settings = settings;
        _ado = ado;
        _adoOptions = adoOptions;
        _anthropic = anthropic;
        _azure = azure;
        _openai = openai;
        _ollama = ollama;
        _claudeCli = claudeCli;
        _copilotCli = copilotCli;
        _cursorCli = cursorCli;
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
        => Ok(new
        {
            suggestions = CliModelCatalogs.ClaudeCatalog().Select(m => m.Id).ToArray(),
            notes = ClaudeModelNotes,
        });

    [HttpGet("claude-cli/catalog")]
    public IActionResult ClaudeCatalog()
        => Ok(new { models = CliModelCatalogs.ClaudeCatalog().Select(CliModelCatalogs.ToApiModel).ToArray() });

    [HttpGet("copilot-cli/models")]
    public IActionResult CopilotModels()
        => Ok(new
        {
            suggestions = CliModelCatalogs.CopilotCatalog().Select(m => m.Id).ToArray(),
            notes = CopilotModelNotes,
        });

    [HttpGet("copilot-cli/catalog")]
    public IActionResult CopilotCatalog()
        => Ok(new { models = CliModelCatalogs.CopilotCatalog().Select(CliModelCatalogs.ToApiModel).ToArray() });

    [HttpGet("cursor-cli/catalog")]
    public IActionResult CursorCatalog()
    {
        var raw = CursorCli.ListModelsRaw(_cursorCli.CurrentValue.Path);
        var models = CliModelCatalogs.ParseCursorListModels(raw);
        return Ok(new { models = models.Select(CliModelCatalogs.ToApiModel).ToArray() });
    }

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

    [HttpGet("ado/work-item-types")]
    public async Task<IActionResult> AdoWorkItemTypes([FromQuery] string? organization, [FromQuery] string? project, CancellationToken cancellationToken)
    {
        var org = string.IsNullOrWhiteSpace(organization) ? _adoOptions.CurrentValue.Organization : organization;
        var proj = string.IsNullOrWhiteSpace(project) ? _adoOptions.CurrentValue.Project : project;
        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(proj))
            return StatusCode(400, new { detail = "Organization and project required." });
        try
        {
            var workItemTypes = await _ado.DiscoverWorkItemTypesAsync(org, proj, cancellationToken);
            return Ok(new { organization = org, project = proj, workItemTypes });
        }
        catch (Exception e)
        {
            _log.LogWarning("ADO discover work item types failed for {Org} / {Proj}: {Error}", org, proj, e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    [HttpGet("ado/repositories")]
    public async Task<IActionResult> AdoRepositories(CancellationToken cancellationToken)
    {
        try
        {
            var repos = await _ado.ListRepositoriesAsync(cancellationToken);
            return Ok(new { repositories = repos.Select(r => r.Name) });
        }
        catch (Exception e)
        {
            _log.LogWarning("ADO discover repositories failed: {Error}", e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    [HttpGet("ado/test-plans")]
    public async Task<IActionResult> AdoTestPlans(CancellationToken cancellationToken)
    {
        try { return Ok(new { plans = await _ado.GetTestPlansAsync(cancellationToken) }); }
        catch (Exception e) { _log.LogWarning("ADO test plans failed: {Error}", e.Message); return StatusCode(502, new { detail = e.Message }); }
    }

    [HttpGet("ado/test-suites")]
    public async Task<IActionResult> AdoTestSuites([FromQuery] int planId, CancellationToken cancellationToken)
    {
        if (planId <= 0) return StatusCode(400, new { detail = "planId is required." });
        try { return Ok(new { suites = await _ado.GetTestSuitesAsync(planId, cancellationToken) }); }
        catch (Exception e) { _log.LogWarning("ADO test suites failed: {Error}", e.Message); return StatusCode(502, new { detail = e.Message }); }
    }

    [HttpGet("ado/test-cases")]
    public async Task<IActionResult> AdoSuiteTestCases(
        [FromQuery] int planId, [FromQuery] int suiteId, [FromQuery] bool recurse = true, CancellationToken cancellationToken = default)
    {
        if (planId <= 0 || suiteId <= 0) return StatusCode(400, new { detail = "planId and suiteId are required." });
        try
        {
            var cases = await _ado.GetSuiteTestCasesAsync(planId, suiteId, recurse, cancellationToken);
            return Ok(new { count = cases.Count, testCases = cases.Select(c => new { c.Id, c.Title, stepCount = c.Steps.Count }) });
        }
        catch (Exception e) { _log.LogWarning("ADO suite test cases failed: {Error}", e.Message); return StatusCode(502, new { detail = e.Message }); }
    }

    [HttpGet("ado/branches")]
    public async Task<IActionResult> AdoBranches([FromQuery] string? repository, CancellationToken cancellationToken)
    {
        try
        {
            var branches = await _ado.DiscoverBranchesAsync(repository, cancellationToken);
            return Ok(new { branches });
        }
        catch (Exception e)
        {
            _log.LogWarning("ADO discover branches failed: {Error}", e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    private async Task<ProviderInfo> BuildProviderInfoAsync(CancellationToken cancellationToken)
    {
        var current = await _providerState.CurrentAsync(cancellationToken);
        return new ProviderInfo
        {
            Current = current.ToString(),
            Available = _providerState.Available.Select(p => p.ToString()).ToList(),
            Active = BuildActiveDetail(current),
        };
    }

    private ProviderActiveDetail? BuildActiveDetail(LlmProvider provider)
    {
        string? model = null, effort = null, context = null;
        switch (provider)
        {
            case LlmProvider.ClaudeCli:
            {
                var o = _claudeCli.CurrentValue;
                model = NullIfBlank(o.Model);
                effort = NullIfBlank(o.Effort);
                context = CliModelCatalogs.ClaudeContextLabel(o.Model);
                break;
            }
            case LlmProvider.CopilotCli:
            {
                var o = _copilotCli.CurrentValue;
                model = NullIfBlank(o.Model);
                effort = NullIfBlank(o.Effort);
                context = CliModelCatalogs.CopilotContextLabel(o.Context);
                break;
            }
            case LlmProvider.CursorCli:
            {
                var o = _cursorCli.CurrentValue;
                var eff = NullIfBlank(o.Effort);
                model = CliModelCatalogs.NormalizeCursorModelId(NullIfBlank(o.Model) ?? "auto", ref eff);
                effort = eff;
                context = NullIfBlank(o.Context) is { } c
                    ? (c.Equals("1m", StringComparison.OrdinalIgnoreCase) ? "1M"
                        : c.Equals("200k", StringComparison.OrdinalIgnoreCase) ? "200k"
                        : c)
                    : null;
                break;
            }
            case LlmProvider.Anthropic:
                model = NullIfBlank(_anthropic.CurrentValue.Model);
                break;
            case LlmProvider.AzureOpenAI:
                model = NullIfBlank(_azure.CurrentValue.Deployment);
                break;
            case LlmProvider.OpenAICompatible:
                model = NullIfBlank(_openai.CurrentValue.Model);
                break;
            case LlmProvider.Ollama:
                model = NullIfBlank(_ollama.CurrentValue.Model);
                break;
        }

        if (model is null && effort is null && context is null)
            return null;
        return new ProviderActiveDetail { Model = model, Effort = effort, Context = context };
    }

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
