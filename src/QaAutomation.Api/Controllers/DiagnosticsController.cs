using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClient _llm;

    public DiagnosticsController(IAzureDevOpsClient ado, ILlmClient llm)
    {
        _ado = ado;
        _llm = llm;
    }

    /// <summary>GET /api/diagnostics — env-var + active-provider status.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        activeProvider = _llm.ProviderName,
        adoPatPresent = Has("ADOACCESS_PAT"),
        anthropicKeyPresent = Has("ANTHROPIC_API_KEY"),
        azureOpenAiConfigured = Has("AZURE_OPENAI_ENDPOINT") && Has("AZURE_OPENAI_API_KEY") && Has("AZURE_OPENAI_DEPLOYMENT"),
    });

    /// <summary>GET /api/diagnostics/workitem/{id} — reads a work item from Your Project.</summary>
    [HttpGet("workitem/{id:int}")]
    public async Task<IActionResult> GetWorkItem(int id, CancellationToken ct)
        => Ok(await _ado.GetWorkItemAsync(id, ct));

    /// <summary>GET /api/diagnostics/llm — single-turn ping to the active LLM provider.</summary>
    [HttpGet("llm")]
    public async Task<IActionResult> PingLlm(CancellationToken ct)
    {
        var reply = await _llm.CompleteAsync("Reply with exactly: OK", "Say OK.", cancellationToken: ct);
        return Ok(new { provider = _llm.ProviderName, reply });
    }

    private static bool Has(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
