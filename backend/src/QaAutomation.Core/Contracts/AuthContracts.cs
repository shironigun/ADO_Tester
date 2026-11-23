namespace QaAutomation.Core.Contracts;

/// <summary>POST /api/auth/login body.</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>
/// Auth state for the SPA. <c>authRequired</c> = the server enforces login; <c>authenticated</c> =
/// the current cookie is valid. When <c>authRequired</c> is false the app runs open.
/// </summary>
public sealed class AuthSession
{
    public bool AuthRequired { get; set; }
    public bool Authenticated { get; set; }
    public string? Username { get; set; }

    /// <summary>Signed JWT — present on a successful login; null on session/logout responses.</summary>
    public string? Token { get; set; }

    /// <summary>Token expiry (UTC), present alongside <see cref="Token"/>.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
