using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Infrastructure.Knowledgebase;

/// <summary>
/// Per-project Markdown domain base (spec B6). Ports the Python <c>KnowledgebaseService</c> verbatim:
/// stores uploaded <c>.md</c> on disk, chunks (~1000-char paragraphs) into the FTS5 <c>doc_chunks</c>
/// virtual table, and runs lexical BM25 retrieval. Scoped: it shares the request/background
/// <see cref="QaDbContext"/> scope.
/// </summary>
public sealed class KnowledgebaseService : IKnowledgebaseService
{
    private const int TargetChunkChars = 1000;

    private readonly QaDbContext _db;
    private readonly ITextSearchProvider _textSearch;
    private readonly ILogger<KnowledgebaseService> _log;
    private readonly string _kbRoot;

    public KnowledgebaseService(
        QaDbContext db, ITextSearchProvider textSearch, IOptions<StorageOptions> storage, ILogger<KnowledgebaseService> log)
    {
        _db = db;
        _textSearch = textSearch;
        _log = log;
        var opts = storage.Value;
        _kbRoot = Path.GetFullPath(Path.Combine(opts.DataDirectory, opts.KnowledgebaseFolderName));
    }

    // ── chunking ──────────────────────────────────────────────────────────────

    /// <summary>Greedy ~1000-char chunker: accumulate blank-line-separated paragraphs, flushing when the
    /// next paragraph would overflow. An oversized single paragraph is NOT sub-split (spec B6).</summary>
    internal static List<string> ChunkMarkdown(string markdown)
    {
        var paragraphs = markdown.Replace("\r\n", "\n").Split("\n\n").Where(p => p.Length > 0).ToList();
        if (paragraphs.Count == 0)
            return [markdown.Trim()];

        var chunks = new List<string>();
        var buf = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (buf.Length > 0 && buf.Length + para.Length > TargetChunkChars)
            {
                chunks.Add(buf.ToString().Trim());
                buf.Clear();
            }
            buf.Append(para).Append("\n\n");
        }
        if (buf.Length > 0)
            chunks.Add(buf.ToString().Trim());

        return chunks.Count > 0 ? chunks : [markdown.Trim()];
    }

    // (FTS5 MATCH tokenizer moved to FtsMatch; the bm25 read moved to SqliteFts5Search.)

    /// <summary>Builds the FTS5 MATCH expression: <c>[A-Za-z0-9_]+</c> tokens of length &gt; 1, de-duped
    /// in order, capped at 20, quoted and OR-joined. Returns null when the query has no usable token.</summary>
    internal static string? MatchExpression(string query) => FtsMatch.SqliteMatchExpression(query);

    // ── ops ───────────────────────────────────────────────────────────────────

    public async Task<KnowledgebaseDocument> UploadAsync(
        int projectId, string fileName, string markdown, CancellationToken cancellationToken = default)
    {
        var projectDir = Path.Combine(_kbRoot, projectId.ToString());
        Directory.CreateDirectory(projectDir);

        var safeName = Path.GetFileName(fileName);
        var storedPath = Path.Combine(projectDir, $"{Guid.NewGuid():N}-{safeName}");
        await File.WriteAllTextAsync(storedPath, markdown, new UTF8Encoding(false), cancellationToken);

        var chunks = ChunkMarkdown(markdown);

        KnowledgebaseDocument? doc = null;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (doc is not null)
                _db.Entry(doc).State = EntityState.Detached;

            doc = new KnowledgebaseDocument
            {
                ProjectId = projectId,
                FileName = safeName,
                StoredPath = storedPath,
                ChunkCount = chunks.Count,
                SizeBytes = Encoding.UTF8.GetByteCount(markdown),
                UploadedAt = DateTime.UtcNow,
            };

            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            _db.Documents.Add(doc);
            await _db.SaveChangesAsync(cancellationToken);   // assigns doc.Id

            foreach (var chunk in chunks)
                await _textSearch.IndexDocChunkAsync(_db, projectId, doc.Id, chunk, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
        return doc!;
    }

    public async Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(
        int projectId, CancellationToken cancellationToken = default)
        => await _db.Documents
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(cancellationToken);

    public async Task DeleteAsync(int documentId, CancellationToken cancellationToken = default)
    {
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Detach any stale tracked state from a prior retry attempt.
            foreach (var e in _db.ChangeTracker.Entries<KnowledgebaseDocument>()
                         .Where(e => e.Entity.Id == documentId).ToList())
                e.State = EntityState.Detached;

            var doc = await _db.Documents.FindAsync([documentId], cancellationToken);
            if (doc is null)
                return;   // idempotent

            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await _textSearch.DeleteDocChunksAsync(_db, documentId, cancellationToken);

            try
            {
                if (!string.IsNullOrEmpty(doc.StoredPath) && File.Exists(doc.StoredPath))
                    File.Delete(doc.StoredPath);
            }
            catch (IOException) { /* file gone is fine — keep the DB delete idempotent */ }
            catch (UnauthorizedAccessException) { /* idem */ }

            _db.Documents.Remove(doc);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    public async Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(
        int projectId, string query, int topK = 8, CancellationToken cancellationToken = default)
    {
        // Fast out on a query with no usable token (the unit-tested seam), before touching the DB.
        if (MatchExpression(query) is null)
            return [];

        // doc id → file name for this project, fetched once (avoid N+1 on result mapping).
        var fileNames = await _db.Documents
            .Where(d => d.ProjectId == projectId)
            .Select(d => new { d.Id, d.FileName })
            .ToDictionaryAsync(x => x.Id, x => x.FileName, cancellationToken);

        // Provider-specific lexical retrieval (SQLite bm25 / PostgreSQL ts_rank), best match first.
        var hits = await _textSearch.SearchDocChunksAsync(_db, projectId, query, topK, cancellationToken);
        return hits
            .Select(h => new KnowledgebaseHit(h.DocId, fileNames.GetValueOrDefault(h.DocId, "(unknown)"), h.Content, h.Score))
            .ToList();
    }

}
