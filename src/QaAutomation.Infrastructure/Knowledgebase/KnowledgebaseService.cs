using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Knowledgebase;

/// <summary>
/// Stores uploaded .md files on disk, chunks them into a SQLite FTS5 table, and does
/// lexical (BM25) retrieval. Provider-agnostic — no embeddings needed.
/// </summary>
public sealed partial class KnowledgebaseService : IKnowledgebaseService
{
    private const int TargetChunkChars = 1000;

    private readonly AppDbContext _db;
    private readonly string _kbRoot;

    public KnowledgebaseService(AppDbContext db, IOptions<StorageOptions> storage)
    {
        _db = db;
        var opt = storage.Value;
        var dataDir = Path.GetFullPath(opt.DataDirectory);
        _kbRoot = Path.Combine(dataDir, opt.KnowledgebaseFolderName);
        Directory.CreateDirectory(_kbRoot);
    }

    public async Task<KnowledgebaseDocument> UploadAsync(
        int projectId, string fileName, string markdown, CancellationToken cancellationToken = default)
    {
        var projectDir = Path.Combine(_kbRoot, projectId.ToString());
        Directory.CreateDirectory(projectDir);

        var safeName = Path.GetFileName(fileName);
        var storedPath = Path.Combine(projectDir, $"{Guid.NewGuid():N}-{safeName}");
        await File.WriteAllTextAsync(storedPath, markdown, cancellationToken);

        var chunks = ChunkMarkdown(markdown);

        var doc = new KnowledgebaseDocument
        {
            ProjectId = projectId,
            FileName = safeName,
            StoredPath = storedPath,
            ChunkCount = chunks.Count,
            SizeBytes = Encoding.UTF8.GetByteCount(markdown),
            UploadedAt = DateTime.UtcNow,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(cancellationToken);

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await OpenAsync(conn, cancellationToken);
        foreach (var chunk in chunks)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO doc_chunks (content, project_id, doc_id) VALUES ($c, $p, $d)";
            cmd.Parameters.AddWithValue("$c", chunk);
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$d", doc.Id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return doc;
    }

    public async Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(
        int projectId, CancellationToken cancellationToken = default) =>
        await _db.Documents
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(cancellationToken);

    public async Task DeleteAsync(int documentId, CancellationToken cancellationToken = default)
    {
        var doc = await _db.Documents.FindAsync([documentId], cancellationToken);
        if (doc is null) return;

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await OpenAsync(conn, cancellationToken);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM doc_chunks WHERE doc_id = $d";
            cmd.Parameters.AddWithValue("$d", documentId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (File.Exists(doc.StoredPath)) File.Delete(doc.StoredPath);
        _db.Documents.Remove(doc);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(
        int projectId, string query, int topK = 8, CancellationToken cancellationToken = default)
    {
        var matchExpr = ToMatchExpression(query);
        if (matchExpr is null) return [];

        var fileNames = await _db.Documents
            .Where(d => d.ProjectId == projectId)
            .ToDictionaryAsync(d => d.Id, d => d.FileName, cancellationToken);

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await OpenAsync(conn, cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT doc_id, content, bm25(doc_chunks) AS score
            FROM doc_chunks
            WHERE doc_chunks MATCH $q AND project_id = $p
            ORDER BY score
            LIMIT $k
            """;
        cmd.Parameters.AddWithValue("$q", matchExpr);
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$k", topK);

        var hits = new List<KnowledgebaseHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var docId = reader.GetInt32(0);
            hits.Add(new KnowledgebaseHit(
                docId,
                fileNames.GetValueOrDefault(docId, "(unknown)"),
                reader.GetString(1),
                reader.GetDouble(2)));
        }
        return hits;
    }

    /// <summary>Creates the FTS5 virtual table if absent. Called at startup.</summary>
    public static void EnsureFtsTable(SqliteConnection conn)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks " +
            "USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)";
        cmd.ExecuteNonQuery();
    }

    private static async Task OpenAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
    }

    /// <summary>Builds a safe FTS5 MATCH expression: alnum tokens OR-ed together. Null if empty.</summary>
    private static string? ToMatchExpression(string query)
    {
        var tokens = TokenRegex().Matches(query)
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .Distinct()
            .Take(20)
            .Select(t => $"\"{t}\"")
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(" OR ", tokens);
    }

    private static List<string> ChunkMarkdown(string markdown)
    {
        var paragraphs = markdown.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (sb.Length > 0 && sb.Length + para.Length > TargetChunkChars)
            {
                chunks.Add(sb.ToString().Trim());
                sb.Clear();
            }
            sb.Append(para).Append("\n\n");
        }
        if (sb.Length > 0) chunks.Add(sb.ToString().Trim());
        return chunks.Count == 0 ? [markdown.Trim()] : chunks;
    }

    [GeneratedRegex(@"[A-Za-z0-9_]+")]
    private static partial Regex TokenRegex();
}
