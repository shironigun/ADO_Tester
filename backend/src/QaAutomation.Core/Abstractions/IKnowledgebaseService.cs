using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Manages the per-project Markdown domain base: upload + store + chunk into an FTS index,
/// and lexical retrieval for the Analyst agent.
/// </summary>
public interface IKnowledgebaseService
{
    Task<KnowledgebaseDocument> UploadAsync(
        int projectId, string fileName, string markdown,
        string? folderPath = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(
        int projectId, CancellationToken cancellationToken = default);

    Task DeleteAsync(int documentId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every document under <paramref name="folderPath"/> (exact match and descendants
    /// via <c>folderPath/</c> prefix). Empty path deletes root-only docs (FolderPath null/empty).
    /// Returns the number of documents removed.</summary>
    Task<int> DeleteFolderAsync(int projectId, string folderPath, CancellationToken cancellationToken = default);

    /// <summary>PostgreSQL lexical retrieval of the most relevant chunks for a query.</summary>
    Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(
        int projectId, string query, int topK = 8, CancellationToken cancellationToken = default);

    /// <summary>All documents whose FolderPath equals <paramref name="folderPath"/> or starts with
    /// "folderPath/". Special case: empty string returns only root documents (FolderPath="" or NULL).</summary>
    Task<IReadOnlyList<KnowledgebaseDocument>> GetDocumentsByFolderAsync(
        int projectId, string folderPath, CancellationToken cancellationToken = default);

    /// <summary>Exact match on FolderPath + FileName. Returns the latest by UploadedAt when duplicates exist.
    /// Returns null when not found.</summary>
    Task<KnowledgebaseDocument?> GetDocumentByPathAsync(
        int projectId, string folderPath, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>Stubbed for an optional future semantic-retrieval upgrade. Not implemented in v1.</summary>
public interface IEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}
