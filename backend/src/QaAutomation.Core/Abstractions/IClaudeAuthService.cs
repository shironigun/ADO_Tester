using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Drives the local <c>claude</c> CLI's subscription authentication (auth login/logout/status) so the
/// web UI can sign Claude in/out without shell/`docker exec` access to the container. Login is a
/// two-step OAuth flow: start (returns a URL to open in the browser) then submit the pasted code.
/// </summary>
public interface IClaudeAuthService
{
    Task<ClaudeAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Spawn <c>claude auth login</c> and return the authorization URL + a login id.</summary>
    Task<ClaudeLoginStart> StartLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>Feed the pasted authorization code to the pending login and report the outcome.</summary>
    Task<ClaudeLoginResult> SubmitCodeAsync(string loginId, string code, CancellationToken cancellationToken = default);

    /// <summary>Run <c>claude auth logout</c> and return the refreshed status.</summary>
    Task<ClaudeAuthStatus> LogoutAsync(CancellationToken cancellationToken = default);
}
