namespace QaAutomation.Core.Contracts;

/// <summary>Response for <c>GET /api/diagnostics</c> (camelCase). Mirrors the Python
/// <c>DiagnosticsStatus</c> — the React client keys off these exact fields.</summary>
public sealed class DiagnosticsStatus
{
    public string ActiveProvider { get; init; } = "";
    public bool AdoPatPresent { get; init; }
    public bool AnthropicKeyPresent { get; init; }
    public bool AzureOpenAiConfigured { get; init; }
    public bool ClaudeCliAvailable { get; init; }
    public string? ClaudeCliVersion { get; init; }
    public bool CopilotCliAvailable { get; init; }
    public string? CopilotCliVersion { get; init; }
}

/// <summary>Response for <c>GET /api/diagnostics/llm</c> — the active provider name + its ping reply.</summary>
public sealed class LlmPingResponse
{
    public string Provider { get; init; } = "";
    public string Reply { get; init; } = "";
}
