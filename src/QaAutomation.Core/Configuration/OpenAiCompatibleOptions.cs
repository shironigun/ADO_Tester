namespace QaAutomation.Core.Configuration;

/// <summary>
/// Settings for any OpenAI-compatible chat endpoint. Defaults target GitHub Models (free).
/// Env vars: OPENAI_BASE_URL / OPENAI_API_KEY / OPENAI_MODEL.
/// Examples:
///   GitHub Models : https://models.inference.ai.azure.com  (key = a GitHub PAT)
///   Groq          : https://api.groq.com/openai/v1
///   OpenAI        : https://api.openai.com/v1
///   Ollama (local): http://localhost:11434/v1
/// </summary>
public sealed class OpenAiCompatibleOptions
{
    public const string SectionName = "OpenAICompatible";

    public string BaseUrl { get; set; } = "https://models.inference.ai.azure.com";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    public int MaxTokens { get; set; } = 4096;
}
