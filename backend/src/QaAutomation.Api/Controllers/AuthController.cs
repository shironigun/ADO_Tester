using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Web-UI login gate (cookie auth). <c>session</c> lets the SPA decide whether to show the login
/// screen; <c>login</c> issues the auth cookie; <c>logout</c> clears it. All actions are anonymous —
/// they must be reachable before the user is signed in (the global fallback policy protects the rest
/// of the API). Credentials come from <see cref="AuthOptions"/> (.env APP_LOGIN_*).
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly AuthOptions _auth;

    public AuthController(AuthOptions auth) => _auth = auth;

    /// <summary>Current auth state. Always 200 so the SPA can branch on the result.</summary>
    [HttpGet("session")]
    public ActionResult<AuthSession> Session()
    {
        if (!_auth.Enabled)
            return new AuthSession { AuthRequired = false, Authenticated = true, Username = null };

        var authenticated = User.Identity?.IsAuthenticated == true;
        return new AuthSession
        {
            AuthRequired = true,
            Authenticated = authenticated,
            Username = authenticated ? User.Identity!.Name : null,
        };
    }

    [HttpPost("login")]
    public ActionResult<AuthSession> Login([FromBody] LoginRequest request)
    {
        if (!_auth.Enabled)
            return new AuthSession { AuthRequired = false, Authenticated = true, Username = null };

        if (!_auth.ValidateCredentials(request.Username, request.Password))
            return Unauthorized(new { detail = "Invalid username or password." });

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_auth.TokenLifetimeMinutes);
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(_auth.GetSigningKeyBytes()), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: AuthOptions.Issuer,
            audience: AuthOptions.Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, _auth.Username),
                new Claim(ClaimTypes.Name, _auth.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            },
            notBefore: now,
            expires: expires,
            signingCredentials: creds);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        return new AuthSession
        {
            AuthRequired = true,
            Authenticated = true,
            Username = _auth.Username,
            Token = token,
            ExpiresAtUtc = expires,
        };
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        // Stateless JWT: the client discards its token. Endpoint kept for symmetry / future revocation.
        return NoContent();
    }
}
