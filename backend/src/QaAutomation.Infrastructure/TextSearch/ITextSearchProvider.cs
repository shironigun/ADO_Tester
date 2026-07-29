using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.TextSearch;

/// <summary>One ranked KB chunk hit (doc id + chunk text + provider-native relevance score).</summary>
public sealed record DocChunkHit(int DocId, string Content, double Score);

/// <summary>
/// Provider-specific full-text search — the one capability that differs between SQLite (FTS5 / bm25)
/// and PostgreSQL (tsvector / ts_rank). Both the <c>doc_chunks</c> (KB retrieval) and
/// <c>test_case_index</c> (existing-test dedup) stores live OUTSIDE EF and are created/queried here:
/// on SQLite they are fts5 virtual tables; on PostgreSQL they are real tables with a generated
/// <c>tsvector</c> column + GIN index. Callers pass their own <see cref="QaDbContext"/> (the KB service
/// is scoped; the dedup index service resolves one per op), so the provider itself is stateless.
/// </summary>
public interface ITextSearchProvider
{
    /// <summary>Create the full-text stores if absent (idempotent). Called once at startup.</summary>
    void EnsureSchema(QaDbContext db);

    // ── KB doc_chunks ───────────────────────────────────────────────────────────
    Task IndexDocChunkAsync(QaDbContext db, int projectId, int docId, string content, CancellationToken ct);
    Task DeleteDocChunksAsync(QaDbContext db, int docId, CancellationToken ct);
    /// <summary>Ranked lexical search over a project's chunks. <paramref name="query"/> is the raw text;
    /// each provider builds its own match expression. Best match first. Empty when nothing matches.</summary>
    Task<IReadOnlyList<DocChunkHit>> SearchDocChunksAsync(
        QaDbContext db, int projectId, string query, int topK, CancellationToken ct);

    // ── dedup test_case_index ─────────────────────────────────────────────────────
    Task IndexTestCaseAsync(QaDbContext db, int scanId, int entryId, string title, string stepsSummary, CancellationToken ct);
    Task DeleteTestCasesForScanAsync(QaDbContext db, int scanId, CancellationToken ct);
    /// <summary>Ranked entry ids for the most feature-relevant existing cases in a scan. Best first.</summary>
    Task<IReadOnlyList<int>> SearchTestCaseEntryIdsAsync(
        QaDbContext db, int scanId, string query, int top, CancellationToken ct);
}
