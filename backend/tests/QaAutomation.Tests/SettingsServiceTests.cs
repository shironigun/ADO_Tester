using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Tests;

/// <summary>
/// B9 settings contract (spec §6): secrets are masked with a <c>{key}.set</c> companion bool, non-secrets
/// return raw, an empty secret keeps the existing value, and unknown keys are silently ignored.
/// Runs the real <see cref="AppSettingsService"/> over a fresh PostgreSQL database.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private const string Mask = "••••••••"; // 8 bullets

    private readonly PostgresTestDatabase _pg;
    private readonly ServiceProvider _sp;
    private readonly IAppSettingsService _settings;

    public SettingsServiceTests()
    {
        _pg = PostgresTestDatabase.Create("settings");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().Build(), _pg.ConnectionString);
        _sp = services.BuildServiceProvider();
        using (var scope = _sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();
        _settings = _sp.GetRequiredService<IAppSettingsService>();
    }

    [Fact]
    public async Task Secrets_masked_nonsecrets_raw_unknown_ignored()
    {
        await _settings.UpdateAsync(new Dictionary<string, string?>
        {
            ["ado.pat"] = "secret-pat-123",
            ["reports.team_name"] = "QA Crew",
            ["bogus.key"] = "x",   // unknown → ignored
        });

        var ui = await _settings.GetAllForUiAsync();
        Assert.Equal(Mask, (string?)ui["ado.pat"]);          // secret masked, never echoed raw
        Assert.True((bool)ui["ado.pat.set"]!);               // companion flag set
        Assert.Equal("QA Crew", (string?)ui["reports.team_name"]); // non-secret returned raw
        Assert.False(ui.ContainsKey("bogus.key"));           // unknown key dropped
        Assert.Equal("", (string?)ui["anthropic.api_key"]);  // unset secret → empty
        Assert.False((bool)ui["anthropic.api_key.set"]!);
    }

    [Fact]
    public async Task Empty_secret_keeps_existing_value()
    {
        await _settings.UpdateAsync(new Dictionary<string, string?> { ["ado.pat"] = "keep-me" });
        await _settings.UpdateAsync(new Dictionary<string, string?> { ["ado.pat"] = "" }); // empty = unchanged
        Assert.Equal("keep-me", await _settings.GetAsync("ado.pat"));
    }

    public void Dispose()
    {
        _sp.Dispose();
        _pg.Dispose();
    }
}
