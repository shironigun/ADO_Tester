using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Builds a bounded CodeScanBundle from the ADO repository for the QA Loop KB-grounded pipeline.
/// Uses FRS atlas glob seeds as the primary source; falls back to IModuleSourceDiscoveryService.
/// Scoped — resolved from background DI scopes in QaLoopOrchestrator.
/// </summary>
public interface IQaLoopCodeScanService
{
    Task<CodeScanBundle> ScanAsync(
        string module,
        string repository,
        string branch,
        FrsModuleContext? frsContext,
        string sessionKey,
        CancellationToken ct = default);
}
