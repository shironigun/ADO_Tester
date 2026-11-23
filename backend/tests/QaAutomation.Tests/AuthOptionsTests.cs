using System.Security.Cryptography;
using System.Text;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Tests;

/// <summary>Signing-key provenance (security): the fallback key must be random, never derived from the
/// password — a password-derived HMAC key lets a captured token be brute-forced offline.</summary>
public sealed class AuthOptionsTests
{
    [Fact]
    public void Blank_secret_uses_a_random_key_not_the_password()
    {
        var a = new AuthOptions { Enabled = true, Username = "u", Password = "p" };
        var b = new AuthOptions { Enabled = true, Username = "u", Password = "p" };

        // Same creds, blank secret → different keys. A password-derived key would make these equal.
        Assert.False(a.GetSigningKeyBytes().SequenceEqual(b.GetSigningKeyBytes()));
        Assert.Equal(32, a.GetSigningKeyBytes().Length);
        // And the key is not SHA-256 of the old "user:pass:ado-tester-jwt" material.
        var legacy = SHA256.HashData(Encoding.UTF8.GetBytes("u:p:ado-tester-jwt"));
        Assert.False(a.GetSigningKeyBytes().SequenceEqual(legacy));
    }

    [Fact]
    public void Explicit_secret_is_deterministic_sha256()
    {
        var a = new AuthOptions { Enabled = true, JwtSecret = "s3cret" };
        var b = new AuthOptions { Enabled = true, JwtSecret = "s3cret" };
        Assert.True(a.GetSigningKeyBytes().SequenceEqual(b.GetSigningKeyBytes()));
        Assert.True(a.GetSigningKeyBytes().SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("s3cret"))));
    }
}
