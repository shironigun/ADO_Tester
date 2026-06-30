namespace QaAutomation.Core.Configuration;

/// <summary>Registry of DMS profiles available to the Playwright runner.</summary>
public sealed class DmsProfileOptions
{
    public static readonly IReadOnlyList<DmsProfileEntry> All =
    [
        new("ideal",    "ideal",    "Ideal",    Credentialed: false),
        new("infinity", "infinity", "Infinity", Credentialed: true),
        new("aspen",    "aspen",    "Aspen",    Credentialed: false),
        new("quantum",  "quantum",  "Quantum",  Credentialed: false),
    ];
}

/// <param name="Profile">C#-side identifier (matches DmsProfile enum wire value).</param>
/// <param name="Key">Lowercase key set on AUTOMATION_PROFILE env var.</param>
/// <param name="DisplayName">Human-readable label shown in the UI.</param>
/// <param name="Credentialed">Only Infinity has credentials in the MVP.</param>
public sealed record DmsProfileEntry(string Profile, string Key, string DisplayName, bool Credentialed);
