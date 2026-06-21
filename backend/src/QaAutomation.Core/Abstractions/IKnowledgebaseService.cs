using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Manages the per-project Markdown domain base: upload + store + chunk into an FTS index,
/// and lexical retrieval for the Analyst agent.
/// </summary>
public interface IKnowledgebaseService
{
    Task<KnowledgebaseDocument> UploadAsync(
        int projectId, string fileName, string markdown, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(
        int projectId, CancellationToken cancellationToken = default);

    Task DeleteAsync(int documentId, CancellationToken cancellationToken = default);

    /// <summary>Lexical (FTS5/BM25) retrieval of the most relevant chunks for a query.</summary>
    Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(
        int projectId, string query, int topK = 8, CancellationToken cancellationToken = default);
}

/// <summary>Stubbed for an optional future semantic-retrieval upgrade. Not implemented in v1.</summary>
public interface IEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}
