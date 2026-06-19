using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Process singleton tracking the active LLM provider, persisted in the Settings table
/// (key <c>llm.provider</c>) so the choice survives restarts. Loads lazily on first read; a malformed
/// stored value falls back to the configured default (spec §5.6). Uses a scope factory because it is a
/// singleton but <see cref="QaDbContext"/> is scoped.
/// </summary>
public sealed class LlmProviderState : ILlmProviderState
{
    public const string SettingKey = "llm.provider";

    private readonly IServiceScopeFactory _scopes;
    private readonly LlmProvider _default;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private LlmProvider _current;

    public LlmProviderState(IServiceScopeFactory scopes, IOptions<LlmOptions> options)
    {
        _scopes = scopes;
        _default = options.Value.Provider;
        _current = _default;
    }

    public IReadOnlyList<LlmProvider> Available => Enum.GetValues<LlmProvider>();

    public async Task<LlmProvider> CurrentAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _current;
    }

    public async Task SetAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _current = provider;
            _loaded = true;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);
            if (setting is null)
                db.Settings.Add(new AppSetting { Key = SettingKey, Value = provider.ToString() });
            else
                setting.Value = provider.ToString();
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);
                // Exact value-name match only — mirrors Python's `LlmProvider(value)`: reject numeric
                // strings ("1"), surrounding whitespace (" Anthropic "), and unknown names → keep default.
                if (setting is not null && Enum.GetNames<LlmProvider>().Contains(setting.Value, StringComparer.Ordinal))
                    _current = Enum.Parse<LlmProvider>(setting.Value);
            }
            catch
            {
                // DB may not be ready during very early startup — stick with the default.
            }
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
