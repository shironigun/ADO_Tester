using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>
/// Pre-run auth: mints a QA bearer token by POSTing the shared QA-admin credentials to the Web API
/// login endpoint (<c>{WebApiBaseUrl}/Authentication/authentication</c>), mirroring the CRM
/// automation project's <c>mintBearerToken</c>. Singleton with a short-lived cache so a burst of
/// runs authenticates once; each run still refreshes on demand (forceRefresh) if a spec 401s.
/// </summary>
public sealed class AutomationAuthenticator : IAutomationAuthenticator
{
    // Login response shape: { "authToken": "..." }
    private sealed record LoginResponse(
        [property: JsonPropertyName("authToken")] string? AuthToken);

    // The Web API expects: { dealerId, userName, password, token }
    private sealed record LoginRequest(string dealerId, string userName, string password, string token);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<AutomationAuthOptions> _options;
    private readonly ILogger<AutomationAuthenticator> _log;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTime _cachedAt;

    public AutomationAuthenticator(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<AutomationAuthOptions> options,
        ILogger<AutomationAuthenticator> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
    }

    public string? ApiBaseUrl => _options.CurrentValue.ApiBaseUrl;

    public bool IsConfigured => _options.CurrentValue.IsComplete;

    public async Task<string?> GetBearerTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsComplete)
        {
            _log.LogWarning(
                "Automation auth not configured — set WEB_API_BASE_URL + AUTH_DEALER_ID/AUTH_USERNAME/AUTH_PASSWORD in .env. Running without a bearer token.");
            return null;
        }

        if (!forceRefresh && _cachedToken is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cachedToken is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cachedToken;

            var token = await MintAsync(opts, ct);
            if (token is not null)
            {
                _cachedToken = token;
                _cachedAt = DateTime.UtcNow;
            }
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> MintAsync(AutomationAuthOptions opts, CancellationToken ct)
    {
        var url = $"{opts.WebApiBaseUrl!.TrimEnd('/')}/Authentication/authentication";
        var body = new LoginRequest(
            dealerId: opts.AuthDealerId!, userName: opts.AuthUsername!, password: opts.AuthPassword!, token: "");
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var res = await client.PostAsJsonAsync(url, body, ct);
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                _log.LogError("Bearer token mint failed: {Status} {Body}", (int)res.StatusCode, text);
                return null;
            }
            var login = await res.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
            if (string.IsNullOrEmpty(login?.AuthToken))
            {
                _log.LogError("Login response missing authToken.");
                return null;
            }
            _log.LogInformation("Minted QA bearer token for automation run (Web API login OK).");
            return login.AuthToken;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bearer token mint threw for {Url}", url);
            return null;
        }
    }
}
