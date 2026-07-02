namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Mints a QA bearer token for a Playwright run session (pre-run auth step). Implementations call
/// the Web API login endpoint with the shared QA-admin credentials and cache the token so a run
/// authenticates once and reuses the token across all its specs.
/// </summary>
public interface IAutomationAuthenticator
{
    /// <summary>The API base URL to target (AUTOMATION_BASE_URL) — same for every DMS. Null if unset.</summary>
    string? ApiBaseUrl { get; }

    /// <summary>Mint (or return a cached) bearer token. Returns null if auth is not configured or fails.</summary>
    Task<string?> GetBearerTokenAsync(bool forceRefresh = false, CancellationToken ct = default);
}
