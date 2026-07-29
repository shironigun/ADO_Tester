using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.TextSearch;

/// <summary>
/// SQLite FTS5 implementation. The <c>doc_chunks</c> and <c>test_case_index</c> virtual tables and the
/// <c>bm25()</c>/<c>rank</c> reads are moved here VERBATIM from KnowledgebaseService /
/// ExistingTestsIndexService so behavior (and the existing BM25 tests) are unchanged.
/// </summary>
public sealed class SqliteFts5Search : ITextSearchProvider
{
    private const string DocChunksDdl =
        "CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)";
    private const string TestIndexDdl =
        "CREATE VIRTUAL TABLE IF NOT EXISTS test_case_index USING fts5(title, steps_summary, scan_id UNINDEXED, entry_id UNINDEXED)";

    public void EnsureSchema(QaDbContext db)
    {
        db.Database.ExecuteSqlRaw(DocChunksDdl);
        db.Database.ExecuteSqlRaw(TestIndexDdl);
    }

    public Task IndexDocChunkAsync(QaDbContext db, int projectId, int docId, string content, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO doc_chunks (content, project_id, doc_id) VALUES ({0}, {1}, {2})",
            [content, projectId, docId], ct);

    public Task DeleteDocChunksAsync(QaDbContext db, int docId, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("DELETE FROM doc_chunks WHERE doc_id = {0}", [docId], ct);

    public async Task<IReadOnlyList<DocChunkHit>> SearchDocChunksAsync(
        QaDbContext db, int projectId, string query, int topK, CancellationToken ct)
    {
        var matchExpr = FtsMatch.SqliteMatchExpression(query);
        if (matchExpr is null)
            return [];

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var mustClose = conn.State != ConnectionState.Open;
        if (mustClose)
            await conn.OpenAsync(ct);
        try
        {
            // Raw open — the EF busy-timeout interceptor only covers EF-managed opens, so set it here too
            // (otherwise an FTS read during a pipeline write can fail with "database is locked").
            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA busy_timeout={SqliteBusyTimeoutInterceptor.BusyTimeoutMs};";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT doc_id, content, bm25(doc_chunks) AS score
                FROM doc_chunks
                WHERE doc_chunks MATCH $q AND project_id = $p
                ORDER BY score
                LIMIT $k
                """;
            cmd.Parameters.Add(new SqliteParameter("$q", matchExpr));
            cmd.Parameters.Add(new SqliteParameter("$p", projectId));
            cmd.Parameters.Add(new SqliteParameter("$k", topK));

            var hits = new List<DocChunkHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new DocChunkHit(
                    Convert.ToInt32(reader.GetValue(0)),
                    reader.GetString(1),
                    Convert.ToDouble(reader.GetValue(2))));
            return hits;
        }
        finally
        {
            if (mustClose)
                await conn.CloseAsync();
        }
    }

    public Task IndexTestCaseAsync(QaDbContext db, int scanId, int entryId, string title, string stepsSummary, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO test_case_index(title, steps_summary, scan_id, entry_id) VALUES ({0},{1},{2},{3})",
            [title, stepsSummary, scanId, entryId], ct);

    public Task DeleteTestCasesForScanAsync(QaDbContext db, int scanId, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("DELETE FROM test_case_index WHERE scan_id = {0}", [scanId], ct);

    public async Task<IReadOnlyList<int>> SearchTestCaseEntryIdsAsync(
        QaDbContext db, int scanId, string query, int top, CancellationToken ct)
    {
        var matchExpr = FtsMatch.SqliteMatchExpression(query);
        if (matchExpr is null)
            return [];

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var mustClose = conn.State != ConnectionState.Open;
        if (mustClose)
            await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT entry_id
                FROM test_case_index
                WHERE test_case_index MATCH $q AND scan_id = $sid
                ORDER BY rank
                LIMIT $top
                """;
            cmd.Parameters.Add(new SqliteParameter("$q", matchExpr));
            cmd.Parameters.Add(new SqliteParameter("$sid", scanId));
            cmd.Parameters.Add(new SqliteParameter("$top", top));

            var ids = new List<int>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(Convert.ToInt32(reader.GetValue(0)));
            return ids;
        }
        catch { return []; }
        finally
        {
            if (mustClose)
                await conn.CloseAsync();
        }
    }
}
