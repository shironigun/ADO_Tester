using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Thread-safe singleton tracking the active provider, persisted to SQLite (key
/// <c>llm.provider</c>) so the choice survives API restarts. The initial value is loaded
/// from the DB on first access; falls back to <c>Llm:Provider</c> config if empty.
/// </summary>
public sealed class LlmProviderState : ILlmProviderState
{
    private const string SettingKey = "llm.provider";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();
    private LlmProvider _current;
    private bool _loaded;
    private readonly LlmProvider _fallback;

    public LlmProviderState(IServiceScopeFactory scopeFactory, LlmProvider configDefault)
    {
        _scopeFactory = scopeFactory;
        _fallback = configDefault;
        _current = configDefault;
    }

    public LlmProvider Current
    {
        get
        {
            EnsureLoaded();
            return _current;
        }
    }

    public void Set(LlmProvider provider)
    {
        lock (_lock)
        {
            _current = provider;
            _loaded = true;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = db.Settings.FirstOrDefault(s => s.Key == SettingKey);
            if (existing is null) db.Settings.Add(new AppSetting { Key = SettingKey, Value = provider.ToString() });
            else existing.Value = provider.ToString();
            db.SaveChanges();
        }
    }

    public IReadOnlyList<LlmProvider> Available { get; } = Enum.GetValues<LlmProvider>();

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var setting = db.Settings.FirstOrDefault(s => s.Key == SettingKey);
                if (setting is not null && Enum.TryParse<LlmProvider>(setting.Value, out var saved))
                    _current = saved;
                else
                    _current = _fallback;
            }
            catch
            {
                _current = _fallback;
            }
            _loaded = true;
        }
    }
}
