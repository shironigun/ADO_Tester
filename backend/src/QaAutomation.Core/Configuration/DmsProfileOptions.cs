namespace QaAutomation.Core.Configuration;

/// <summary>Registry of DMS profiles available to the Playwright runner.</summary>
public sealed class DmsProfileOptions
{
    // ponytail: read directly from process env at class-load, same Env(key) ?? default idiom used
    // throughout DependencyInjection.cs — a full IOptions<T>/RuntimeSettings round-trip is overkill
    // for a 4-row static registry with a single credentialed entry today. Add the Options ceremony
    // if/when a second profile needs a secret or this needs to be UI-editable.
    public static readonly IReadOnlyList<DmsProfileEntry> All =
    [
        new("ideal",    "ideal",    "Ideal",    Credentialed: false, BaseUrl: Env("IDEAL_BASE_URL")),
        new("infinity", "infinity", "Infinity", Credentialed: true,  BaseUrl: Env("INFINITY_BASE_URL"), BearerToken: Env("INFINITY_BEARER_TOKEN")),
        new("aspen",    "aspen",    "Aspen",    Credentialed: false, BaseUrl: Env("ASPEN_BASE_URL")),
        new("quantum",  "quantum",  "Quantum",  Credentialed: false, BaseUrl: Env("QUANTUM_BASE_URL")),
    ];

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}

/// <param name="Profile">C#-side identifier (matches DmsProfile enum wire value).</param>
/// <param name="Key">Lowercase key set on AUTOMATION_PROFILE env var.</param>
/// <param name="DisplayName">Human-readable label shown in the UI.</param>
/// <param name="Credentialed">Only Infinity has credentials in the MVP.</param>
/// <param name="BaseUrl">Target host set on AUTOMATION_BASE_URL env var for the child Playwright
/// process; null/unset until supplied via .env (see .env.example). No hardcoded guesses here.</param>
/// <param name="BearerToken">Set on AUTOMATION_BEARER_TOKEN env var when the profile is credentialed.</param>
public sealed record DmsProfileEntry(
    string Profile, string Key, string DisplayName, bool Credentialed,
    string? BaseUrl = null, string? BearerToken = null);
