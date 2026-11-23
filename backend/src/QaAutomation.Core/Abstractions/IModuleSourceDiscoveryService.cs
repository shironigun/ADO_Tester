using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>Discovers bounded frontend and backend evidence for a QA Loop module on one repo branch.</summary>
public interface IModuleSourceDiscoveryService
{
    Task<ModuleSourceDiscovery?> DiscoverAsync(
        string repository,
        string branch,
        string module,
        string? frontendPath,
        string? frontendRoute,
        CancellationToken cancellationToken = default);
}
