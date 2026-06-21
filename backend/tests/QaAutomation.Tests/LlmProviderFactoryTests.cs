using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Tests;

/// <summary>
/// DI-integration tests (spec §5.6): the per-call factory returns the client for the active provider,
/// and provider state persists to the Settings table + reloads / falls back on a malformed value.
/// Runs against a throwaway SQLite file.
/// </summary>
public sealed class LlmProviderFactoryTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"qa_llm_{Guid.NewGuid():N}.db");

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().Build(), $"Data Source={_db}");
        var sp = services.BuildServiceProvider();
        sp.InitializeDatabase();
        return sp;
    }

    [Theory]
    [InlineData(LlmProvider.Anthropic, "Anthropic")]
    [InlineData(LlmProvider.AzureOpenAI, "AzureOpenAI")]
    [InlineData(LlmProvider.OpenAICompatible, "OpenAICompatible")]
    [InlineData(LlmProvider.ClaudeCli, "ClaudeCli")]
    public async Task Factory_builds_client_for_active_provider(LlmProvider provider, string expectedName)
    {
        using var sp = Build();
        await sp.GetRequiredService<ILlmProviderState>().SetAsync(provider);
        var client = await sp.GetRequiredService<ILlmClientFactory>().CreateAsync();
        Assert.Equal(expectedName, client.ProviderName);
    }

    [Fact]
    public async Task ProviderState_persists_and_reloads_across_instances()
    {
        using (var sp = Build())
            await sp.GetRequiredService<ILlmProviderState>().SetAsync(LlmProvider.ClaudeCli);

        using var sp2 = Build(); // fresh singletons, same DB file
        Assert.Equal(LlmProvider.ClaudeCli, await sp2.GetRequiredService<ILlmProviderState>().CurrentAsync());
    }

    [Theory]
    [InlineData("Bogus")]        // unknown name
    [InlineData("1")]            // numeric (Enum.TryParse would accept → AzureOpenAI); must be rejected
    [InlineData(" Anthropic ")]  // surrounding whitespace (Enum.TryParse trims); must be rejected
    [InlineData("anthropic")]    // wrong case
    public async Task ProviderState_invalid_stored_value_falls_back_to_default(string stored)
    {
        using (var sp = Build())
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            db.Settings.Add(new AppSetting { Key = LlmProviderState.SettingKey, Value = stored });
            await db.SaveChangesAsync();
        }
        using var sp2 = Build();
        Assert.Equal(LlmProvider.Anthropic, await sp2.GetRequiredService<ILlmProviderState>().CurrentAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_db + suffix); } catch { /* best effort */ }
        }
    }
}
