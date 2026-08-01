using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Reads the FRS Knowledge Base (uploaded via KB folder upload) and resolves a module name
/// to a <see cref="FrsModuleContext"/> by matching slugs, aliases, and keyword packs.
/// Scoped — resolved from the background DI scope inside QaLoopOrchestrator.
/// </summary>
public interface IFrsKbService
{
    /// <summary>Resolves a module name to a FrsModuleContext by reading atlas YAML + module markdown
    /// + RULES section directly from disk via KnowledgebaseDocument.StoredPath.
    /// Returns null when no FRS documents are uploaded or no slug matches.</summary>
    Task<FrsModuleContext?> ResolveModuleAsync(
        string moduleName, int projectId, CancellationToken ct = default);

    /// <summary>Returns available FRS slugs from filenames in the modules/ folder.
    /// No YAML parsing — fast for the scan-form dropdown.</summary>
    Task<IReadOnlyList<string>> ListSlugsAsync(
        int projectId, CancellationToken ct = default);
}
