using System.Security.Cryptography;
using System.Text;

namespace QaAutomation.Core.Configuration;

/// <summary>
/// Single-user web-UI login gate. Credentials come from the .env (APP_LOGIN_USERNAME /
/// APP_LOGIN_PASSWORD) — distinct from the QA Loop AUTH_* creds used to mint automation bearer
/// tokens. When both are set (or APP_LOGIN_ENABLED=true) the SPA shows a login screen and every
/// /api endpoint requires an authenticated cookie; otherwise the app runs open (unchanged behavior).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>JWT issuer/audience — fixed for this single-app token.</summary>
    public const string Issuer = "ado-tester";
    public const string Audience = "ado-tester";

    /// <summary>True when the login gate is active (see <see cref="FromEnvironment"/>).</summary>
    public bool Enabled { get; init; }

    public string Username { get; init; } = "";

    public string Password { get; init; } = "";

    /// <summary>Optional explicit HS256 secret (APP_LOGIN_JWT_SECRET). When blank, a random per-process
    /// key is used instead (tokens then invalidate on restart). Program.cs requires this to be set
    /// outside Development, so the random fallback only applies to local dev.</summary>
    public string JwtSecret { get; init; } = "";

    /// <summary>Random 32-byte signing key, generated once per process — the fallback when
    /// <see cref="JwtSecret"/> is blank. Never derived from the password (that would let a captured
    /// token be brute-forced offline back to the password).</summary>
    private readonly byte[] _randomKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Issued-token lifetime in minutes (APP_LOGIN_TOKEN_MINUTES). Default 2h.</summary>
    public int TokenLifetimeMinutes { get; init; } = 60 * 2;

    /// <summary>
    /// Resolve from process env. Auth is enabled when username AND password are non-empty, unless
    /// APP_LOGIN_ENABLED explicitly overrides (true/false). Values set via Docker/compose env or a
    /// .env file both land in process env, so this works identically in either.
    /// </summary>
    public static AuthOptions FromEnvironment()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        var username = (Env("APP_LOGIN_USERNAME") ?? "").Trim();
        var password = Env("APP_LOGIN_PASSWORD") ?? "";

        bool enabled;
        if (Env("APP_LOGIN_ENABLED") is { } flag && bool.TryParse(flag.Trim(), out var e))
            enabled = e;
        else
            enabled = username.Length > 0 && password.Length > 0;

        var minutes = 60 * 2;
        if (Env("APP_LOGIN_TOKEN_MINUTES") is { } m && int.TryParse(m.Trim(), out var mv) && mv > 0)
            minutes = mv;

        return new AuthOptions
        {
            Enabled = enabled,
            Username = username,
            Password = password,
            JwtSecret = (Env("APP_LOGIN_JWT_SECRET") ?? "").Trim(),
            TokenLifetimeMinutes = minutes,
        };
    }

    /// <summary>Constant-time credential check. Always true when auth is disabled.</summary>
    public bool ValidateCredentials(string? username, string? password)
    {
        if (!Enabled)
            return true;
        if (string.IsNullOrEmpty(username) || password is null)
            return false;

        // FixedTimeEquals returns false for length-mismatched inputs without leaking via early-exit timing.
        var userOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(Username));
        var passOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(Password));
        return userOk && passOk;
    }

    /// <summary>
    /// 32-byte HS256 signing key. SHA-256 of APP_LOGIN_JWT_SECRET when set; otherwise a random
    /// per-process key (never the password — see <see cref="_randomKey"/>).
    /// </summary>
    public byte[] GetSigningKeyBytes()
        => JwtSecret.Length > 0 ? SHA256.HashData(Encoding.UTF8.GetBytes(JwtSecret)) : _randomKey;
}
