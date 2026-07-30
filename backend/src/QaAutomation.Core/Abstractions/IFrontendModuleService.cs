using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Discovers the product's frontend modules for the QA Loop "New Scan" dropdown. Tries the conventional
/// route file first (fast, deterministic parser); when that's missing or unparseable, falls back to the
/// active LLM to locate the route table in the repo and extract the modules. Results are cached (one-time)
/// so the AI pass runs only once per repo/branch until a caller forces a refresh.
/// </summary>
public interface IFrontendModuleService
{
    Task<IReadOnlyList<FrontendModule>> DiscoverAsync(
        string? repository = null, string? branch = null, bool refresh = false,
        CancellationToken cancellationToken = default);
}
