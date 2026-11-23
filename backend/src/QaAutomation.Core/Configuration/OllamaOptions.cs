namespace QaAutomation.Core.Configuration;

/// <summary>
/// Native Ollama API settings. The endpoint can target a local daemon or Ollama Cloud.
/// Env vars: OLLAMA_BASE_URL / OLLAMA_API_KEY / OLLAMA_MODEL.
/// </summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Native API root; callers may provide either a host root or a URL already ending in <c>/api</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/api";

    /// <summary>Optional for local Ollama; required by authenticated cloud endpoints.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "llama3.2";
}
