namespace QaAutomation.Core.Configuration;

/// <summary>
/// Connection settings for Azure DevOps. The PAT is resolved from Key Vault in production
/// (via configuration) or user-secrets / appsettings during local development.
/// </summary>
public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    /// <summary>e.g. "https://dev.azure.com/{organization}".</summary>
    public string Organization { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    /// <summary>Personal Access Token. Never commit a real value — use user-secrets / Key Vault.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Azure DevOps REST API version used for requests.</summary>
    public string ApiVersion { get; set; } = "7.1";

    public string BaseUrl =>
        $"https://dev.azure.com/{Organization.Trim('/')}/{Uri.EscapeDataString(Project)}";
}
