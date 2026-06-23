using System.Collections.Concurrent;

namespace QaAutomation.Infrastructure.Settings;

/// <summary>
/// Holds the DB-persisted overrides for the option-backed settings keys (ado.*, anthropic.*, etc.),
/// so a UI settings edit wins over the .env bootstrap (DB-over-env, spec B9). A second PostConfigure
/// (registered after the env bridge) overlays these onto the typed options; <see cref="Apply"/> evicts
/// the <c>IOptionsMonitorCache</c> so the next <c>CurrentValue</c> read rebuilds with the new values —
/// and the ADO/LLM clients (which read <c>CurrentValue</c> per use) pick them up with no restart.
/// Singleton.
/// </summary>
public sealed class RuntimeSettings
{
    private readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.Ordinal);

    /// <summary>Replace all overrides from the dotted-key → value snapshot (empty values are dropped).</summary>
    public void Replace(IReadOnlyDictionary<string, string> values)
    {
        _overrides.Clear();
        foreach (var (k, v) in values)
            if (!string.IsNullOrEmpty(v))
                _overrides[k] = v;
    }

    /// <summary>The override for a dotted key, or null when unset (caller keeps the env/appsettings value).</summary>
    public string? Get(string dottedKey) => _overrides.TryGetValue(dottedKey, out var v) ? v : null;
}
