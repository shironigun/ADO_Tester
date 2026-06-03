namespace QaAutomation.Core.Configuration;

/// <summary>
/// Azure OpenAI settings. Endpoint, key and deployment come from env vars
/// (AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT) via .env.
/// Also usable for OpenAI-compatible endpoints (e.g. GitHub Models) by pointing Endpoint at them.
/// </summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>e.g. https://{resource}.openai.azure.com</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Deployment name (the model). Used as the default model when none is passed.</summary>
    public string Deployment { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2024-08-01-preview";

    public int MaxTokens { get; set; } = 4096;
}
