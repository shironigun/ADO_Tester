namespace QaAutomation.Core.Models;

/// <summary>Simple key/value setting store, persisted in PostgreSQL so choices survive restarts.</summary>
public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
