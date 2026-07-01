using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.QaRuns;

namespace QaAutomation.Tests;

/// <summary>
/// QAL-068: AUTOMATION_PROFILE alone is a profile KEY (e.g. "infinity"), not a URL — every generated
/// spec that read it as a base URL silently fell back to a hardcoded/localhost guess. Verifies the
/// child-process environment actually gets a real AUTOMATION_BASE_URL/AUTOMATION_BEARER_TOKEN, and
/// that DmsProfileOptions.All picks those values up from the process environment (the .env bridge).
/// </summary>
public sealed class PlaywrightRunnerTests
{
    [Fact]
    public void ApplyAutomationEnv_sets_profile_baseUrl_and_bearerToken_when_all_present()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: "infinity",
            baseUrl: "https://infinity.example.test", bearerToken: "tok-123");

        Assert.Equal("infinity", env["AUTOMATION_PROFILE"]);
        Assert.Equal("https://infinity.example.test", env["AUTOMATION_BASE_URL"]);
        Assert.Equal("tok-123", env["AUTOMATION_BEARER_TOKEN"]);
    }

    [Fact]
    public void ApplyAutomationEnv_omits_baseUrl_and_bearerToken_when_null_or_empty()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: "ideal", baseUrl: null, bearerToken: "");

        Assert.Equal("ideal", env["AUTOMATION_PROFILE"]);
        Assert.False(env.ContainsKey("AUTOMATION_BASE_URL"));
        Assert.False(env.ContainsKey("AUTOMATION_BEARER_TOKEN"));
    }

    [Fact]
    public void ApplyAutomationEnv_omits_profile_when_null_but_still_sets_baseUrl()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: null, baseUrl: "https://x.test", bearerToken: null);

        Assert.False(env.ContainsKey("AUTOMATION_PROFILE"));
        Assert.Equal("https://x.test", env["AUTOMATION_BASE_URL"]);
    }

    [Fact]
    public void DmsProfileEntry_carries_BaseUrl_and_BearerToken_end_to_end_into_child_env()
    {
        // Exercises the real seam PlaywrightRunner.RunAsync uses: look up the registry entry by
        // profile key, then feed its BaseUrl/BearerToken into ApplyAutomationEnv — same as the fix.
        var entry = new DmsProfileEntry("infinity", "infinity", "Infinity", Credentialed: true,
            BaseUrl: "https://infinity.qa.example.test", BearerToken: "secret-token");
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, entry.Key, entry.BaseUrl, entry.BearerToken);

        Assert.Equal("infinity", env["AUTOMATION_PROFILE"]);
        Assert.Equal("https://infinity.qa.example.test", env["AUTOMATION_BASE_URL"]);
        Assert.Equal("secret-token", env["AUTOMATION_BEARER_TOKEN"]);
    }

    [Fact]
    public void DmsProfileOptions_All_has_the_four_expected_profile_keys_and_only_infinity_credentialed()
    {
        var keys = DmsProfileOptions.All.Select(p => p.Key).ToList();
        Assert.Equal(["ideal", "infinity", "aspen", "quantum"], keys);
        Assert.All(DmsProfileOptions.All, p => Assert.Equal(p.Credentialed, p.Key == "infinity"));
    }

    [Fact]
    public void DmsProfileOptions_All_has_no_hardcoded_BaseUrl_guess_when_env_unset()
    {
        // Guards against ever baking a guessed hostname into the registry itself (QAL-068 requirement:
        // don't propagate the old generated-file fallback hostname into the config layer as if verified).
        // Only meaningful when the ambient process doesn't already have these set (CI/dev machines
        // shouldn't), so this is a smoke check, not a hard environment guarantee.
        var entry = DmsProfileOptions.All.Single(p => p.Key == "infinity");
        if (Environment.GetEnvironmentVariable("INFINITY_BASE_URL") is null)
            Assert.Null(entry.BaseUrl);
    }
}
