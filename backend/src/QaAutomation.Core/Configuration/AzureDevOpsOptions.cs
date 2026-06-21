namespace QaAutomation.Core.Configuration;

/// <summary>
/// Connection + house-style settings for Azure DevOps. Defaults mirror the live Python
/// <c>config.py</c> (spec §5.11). Secrets (the PAT) come from <c>.env</c>/User-Secrets locally and
/// Key Vault later; the UI-editable settings layer (B9) overrides these at runtime via IOptionsMonitor.
/// </summary>
public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public string Organization { get; set; } = "ConstellationDealer";
    public string Project { get; set; } = "Ideal Agile";

    /// <summary>Personal Access Token (Basic auth). Never commit a real value.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>Configurable REST api-version (default 7.1). Some endpoints hardcode other
    /// versions — those do NOT use this option (spec §5.5).</summary>
    public string ApiVersion { get; set; } = "7.1";

    /// <summary>Git repository used by the code-analysis (verify-against-code) phase.</summary>
    public string Repository { get; set; } = "CRM Project";
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Bug-filing house style (mirrors the targetcrm-bug-suite skill).</summary>
    public string BugAreaPath { get; set; } = "Ideal Agile\\CRM Team";
    public string QaOwner { get; set; } = "mahmood.ahmad@constellationdealer.com";

    /// <summary>Project-scoped base URL. Org/project names contain spaces, so the project is
    /// URL-encoded (spec §5.5). e.g. https://dev.azure.com/ConstellationDealer/Ideal%20Agile</summary>
    public string BaseUrl =>
        $"https://dev.azure.com/{Organization.Trim('/')}/{Uri.EscapeDataString(Project)}";
}
