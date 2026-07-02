namespace QaAutomation.Core.Configuration;

/// <summary>
/// QA-run automation target + auth. The API base URL is the SAME regardless of DMS type (QA fronts
/// every DMS with one APIM gateway); the bearer is minted once per run session against the Web API
/// using the shared QA-admin credentials. Bridged from the flat <c>.env</c> in DependencyInjection.
/// </summary>
public sealed class AutomationAuthOptions
{
    public const string SectionName = "AutomationAuth";

    /// <summary>Deals/Lead API base — set on the child process as <c>AUTOMATION_BASE_URL</c>.
    /// Same for all DMS types (APIM gateway). e.g. https://target-crm-api-gateway-qa.azure-api.net/lead-api</summary>
    public string? LeadApiBaseUrl { get; set; }

    /// <summary>Web API base that mints the bearer token (direct, no APIM).
    /// e.g. https://target-crm-web-api-qa-...azurewebsites.net/api/v1</summary>
    public string? WebApiBaseUrl { get; set; }

    /// <summary>QA-admin dealer id used for the login call.</summary>
    public string? AuthDealerId { get; set; }

    /// <summary>QA-admin username used for the login call.</summary>
    public string? AuthUsername { get; set; }

    /// <summary>QA-admin password used for the login call.</summary>
    public string? AuthPassword { get; set; }

    /// <summary>True only when the base URL and all three credential fields are present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(WebApiBaseUrl)
        && !string.IsNullOrWhiteSpace(AuthDealerId)
        && !string.IsNullOrWhiteSpace(AuthUsername)
        && !string.IsNullOrWhiteSpace(AuthPassword);
}
