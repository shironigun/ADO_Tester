namespace QaAutomation.Core.Configuration;

/// <summary>
/// Settings for calling the Anthropic (Claude) API. The key is resolved from Key Vault in
/// production or user-secrets during local development.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Default model used when an agent does not override it.</summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    public string ApiVersion { get; set; } = "2023-06-01";

    public int MaxTokens { get; set; } = 4096;
}
