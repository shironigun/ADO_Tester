using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Diagnostics endpoints (spec §5.4 / B9) — flat under <c>/api/diagnostics</c>:
/// env + active-provider status, a PAT-backed work-item read, and a single-turn LLM ping.
/// Errors are returned as <c>{ detail }</c> (FastAPI parity), not ProblemDetails.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly ILlmProviderState _providerState;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IAzureDevOpsClient _ado;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOptions;
    private readonly IOptionsMonitor<AnthropicOptions> _anthropic;
    private readonly IOptionsMonitor<AzureOpenAiOptions> _azure;
    private readonly IOptionsMonitor<OllamaOptions> _ollama;
    private readonly IOptionsMonitor<ClaudeCliOptions> _claudeCli;
    private readonly IOptionsMonitor<CopilotCliOptions> _copilotCli;
    private readonly ILogger<DiagnosticsController> _log;

    public DiagnosticsController(
        ILlmProviderState providerState, ILlmClientFactory llmFactory, IAzureDevOpsClient ado,
        IOptionsMonitor<AzureDevOpsOptions> adoOptions, IOptionsMonitor<AnthropicOptions> anthropic,
        IOptionsMonitor<AzureOpenAiOptions> azure, IOptionsMonitor<OllamaOptions> ollama, IOptionsMonitor<ClaudeCliOptions> claudeCli,
        IOptionsMonitor<CopilotCliOptions> copilotCli,
        ILogger<DiagnosticsController> log)
    {
        _providerState = providerState;
        _llmFactory = llmFactory;
        _ado = ado;
        _adoOptions = adoOptions;
        _anthropic = anthropic;
        _azure = azure;
        _ollama = ollama;
        _claudeCli = claudeCli;
        _copilotCli = copilotCli;
        _log = log;
    }

    [HttpGet]
    public async Task<DiagnosticsStatus> Status(CancellationToken cancellationToken)
    {
        var provider = await _providerState.CurrentAsync(cancellationToken);
        // Probe both CLIs on a worker thread (synchronous subprocess), via the SAME resolvers the providers use.
        var (claudeAvailable, claudeVersion) = await Task.Run(() => ClaudeCli.Probe(_claudeCli.CurrentValue.Path), cancellationToken);
        var (copilotAvailable, copilotVersion) = await Task.Run(() => CopilotCli.Probe(_copilotCli.CurrentValue.Path), cancellationToken);

        var ado = _adoOptions.CurrentValue;
        var anthropic = _anthropic.CurrentValue;
        var azure = _azure.CurrentValue;
        var ollama = _ollama.CurrentValue;
        return new DiagnosticsStatus
        {
            ActiveProvider = provider.ToString(),
            AdoPatPresent = !string.IsNullOrEmpty(ado.PersonalAccessToken),
            AnthropicKeyPresent = !string.IsNullOrEmpty(anthropic.ApiKey),
            AzureOpenAiConfigured = !string.IsNullOrEmpty(azure.Endpoint)
                                    && !string.IsNullOrEmpty(azure.ApiKey)
                                    && !string.IsNullOrEmpty(azure.Deployment),
            OllamaConfigured = !string.IsNullOrEmpty(ollama.BaseUrl),
            ClaudeCliAvailable = claudeAvailable,
            ClaudeCliVersion = claudeVersion,
            CopilotCliAvailable = copilotAvailable,
            CopilotCliVersion = copilotVersion,
        };
    }

    [HttpGet("workitem/{workItemId:int}")]
    public async Task<IActionResult> GetWorkItem(int workItemId, CancellationToken cancellationToken)
    {
        try
        {
            var wi = await _ado.GetWorkItemAsync(workItemId, ct: cancellationToken);
            return Ok(wi);
        }
        catch (AdoApiError e)
        {
            _log.LogWarning("ADO read failed: {Error}", e.Message);
            return StatusCode(e.Status == 404 ? 404 : 502, new { detail = e.Message });
        }
        catch (Exception e)
        {
            _log.LogError(e, "ADO read failed unexpectedly");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("llm")]
    public async Task<IActionResult> PingLlm(CancellationToken cancellationToken)
    {
        var client = await _llmFactory.CreateAsync(cancellationToken);
        try
        {
            var reply = await client.CompleteAsync("Reply with exactly: OK", "Say OK.", cancellationToken: cancellationToken);
            return Ok(new LlmPingResponse { Provider = client.ProviderName, Reply = reply });
        }
        catch (Exception e)
        {
            return StatusCode(502, new { detail = e.Message });
        }
    }
}
