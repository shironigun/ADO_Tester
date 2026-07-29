using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Extracts a module's knowledgebase (per-flow docs + <c>rules.md</c>) from product source code and
/// stores + indexes each produced document via <see cref="IKnowledgebaseService"/> so the Analyst and
/// Generator can retrieve it. Track A — the hackathon KB foundation that everything downstream depends on.
/// </summary>
public interface IKbExtractionService
{
    Task<KbExtractionResult> ExtractAsync(int projectId, KbExtractionRequest request, CancellationToken ct = default);
}
