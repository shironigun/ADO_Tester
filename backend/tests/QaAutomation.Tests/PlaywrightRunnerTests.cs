using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.QaRuns;

namespace QaAutomation.Tests;

/// <summary>
/// Child-process env mapping: shared AUTOMATION_BASE_URL + BASE_URL + bearer (minted upstream),
/// optional AUTOMATION_APP_* for UI runs. Profile is a label only — not a host/token source.
/// </summary>
public sealed class PlaywrightRunnerTests
{
    [Fact]
    public void ApplyAutomationEnv_sets_profile_baseUrl_bearer_and_BASE_URL()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: "infinity",
            baseUrl: "https://api.example.test", bearerToken: "tok-123");

        Assert.Equal("infinity", env["AUTOMATION_PROFILE"]);
        Assert.Equal("https://api.example.test", env["AUTOMATION_BASE_URL"]);
        Assert.Equal("https://api.example.test", env["BASE_URL"]);
        Assert.Equal("tok-123", env["AUTOMATION_BEARER_TOKEN"]);
    }

    [Fact]
    public void ApplyAutomationEnv_omits_baseUrl_and_bearerToken_when_null_or_empty()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: "ideal", baseUrl: null, bearerToken: "");

        Assert.Equal("ideal", env["AUTOMATION_PROFILE"]);
        Assert.False(env.ContainsKey("AUTOMATION_BASE_URL"));
        Assert.False(env.ContainsKey("BASE_URL"));
        Assert.False(env.ContainsKey("AUTOMATION_BEARER_TOKEN"));
    }

    [Fact]
    public void ApplyAutomationEnv_omits_profile_when_null_but_still_sets_baseUrl()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: null, baseUrl: "https://x.test", bearerToken: null);

        Assert.False(env.ContainsKey("AUTOMATION_PROFILE"));
        Assert.Equal("https://x.test", env["AUTOMATION_BASE_URL"]);
        Assert.Equal("https://x.test", env["BASE_URL"]);
    }

    [Fact]
    public void ApplyAutomationEnv_sets_APP_vars_for_UI_runs()
    {
        var env = new Dictionary<string, string?>();

        PlaywrightRunner.ApplyAutomationEnv(env, profile: "infinity",
            baseUrl: "https://app.example.test", bearerToken: null,
            appUrl: "https://app.example.test", appUsername: "qa", appPassword: "secret");

        Assert.Equal("https://app.example.test", env["BASE_URL"]);
        Assert.Equal("https://app.example.test", env["AUTOMATION_APP_URL"]);
        Assert.Equal("qa", env["AUTOMATION_APP_USERNAME"]);
        Assert.Equal("secret", env["AUTOMATION_APP_PASSWORD"]);
        Assert.False(env.ContainsKey("AUTOMATION_BEARER_TOKEN"));
    }

    [Fact]
    public void ResolveFixturesDir_finds_repo_scripts_fixtures()
    {
        var dir = PlaywrightRunner.ResolveFixturesDir(Directory.GetCurrentDirectory());
        Assert.NotNull(dir);
        Assert.True(File.Exists(Path.Combine(dir!, "auth.setup.ts")));
    }
}
