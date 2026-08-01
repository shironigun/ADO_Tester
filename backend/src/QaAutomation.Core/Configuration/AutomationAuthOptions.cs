namespace QaAutomation.Core.Configuration;

/// <summary>
/// QA-run automation target + auth. The API base URL is the SAME regardless of DMS type (QA fronts
/// every DMS with one gateway); the bearer is minted once against the Web API using the shared
/// QA-admin credentials and reused across specs. Bridged from the flat <c>.env</c> in DependencyInjection.
/// </summary>
public sealed class AutomationAuthOptions
{
    public const string SectionName = "AutomationAuth";

    /// <summary>Shared target API host — set on the child process as <c>AUTOMATION_BASE_URL</c>.
    /// Same for all DMS types. From env <c>AUTOMATION_API_BASE_URL</c> (legacy: <c>LEAD_API_BASE_URL</c>).</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Web API base that mints the bearer token (direct, no APIM).
    /// e.g. https://your-web-api.example.net/api/v1</summary>
    public string? WebApiBaseUrl { get; set; }

    /// <summary>QA-admin dealer id used for the login call.</summary>
    public string? AuthDealerId { get; set; }

    /// <summary>QA-admin username used for the login call.</summary>
    public string? AuthUsername { get; set; }

    /// <summary>QA-admin password used for the login call.</summary>
    public string? AuthPassword { get; set; }

    /// <summary>True only when the mint host and all three credential fields are present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(WebApiBaseUrl)
        && !string.IsNullOrWhiteSpace(AuthDealerId)
        && !string.IsNullOrWhiteSpace(AuthUsername)
        && !string.IsNullOrWhiteSpace(AuthPassword);
}
