namespace QaAutomation.Core.Contracts;

/// <summary>Active provider + the available set (camelCase: current, available). Mirrors ProviderInfo.</summary>
public sealed class ProviderInfo
{
    public string Current { get; set; } = "";
    public IReadOnlyList<string> Available { get; set; } = [];
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
