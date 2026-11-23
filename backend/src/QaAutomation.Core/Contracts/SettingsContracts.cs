namespace QaAutomation.Core.Contracts;

/// <summary>Active provider + the available set (camelCase: current, available). Mirrors ProviderInfo.</summary>
public sealed class ProviderInfo
{
    public string Current { get; set; } = "";
    public IReadOnlyList<string> Available { get; set; } = [];
    /// <summary>Display-only chips for the active provider (model / effort / context). Null when nothing to show.</summary>
    public ProviderActiveDetail? Active { get; set; }
}

/// <summary>Resolved model/effort/context labels for the AppBar chips (display only).</summary>
public sealed class ProviderActiveDetail
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    /// <summary>Human label such as <c>200k</c> / <c>1M</c>, not the raw CLI tier value.</summary>
    public string? Context { get; set; }
}

public sealed class SetProviderRequest
{
    public string Provider { get; set; } = "";
}

/// <summary>PUT /config body: a flat map of literal dotted keys → string values (extra keys allowed).</summary>
public sealed class UpdateAppSettingsRequest
{
    public Dictionary<string, string?> Values { get; set; } = new();
}
