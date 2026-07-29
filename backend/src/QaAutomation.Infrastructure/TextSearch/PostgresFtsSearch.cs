using Microsoft.EntityFrameworkCore;
using Npgsql;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.TextSearch;

/// <summary>
/// PostgreSQL full-text implementation. <c>doc_chunks</c> and <c>test_case_index</c> are real tables
/// with a generated <c>tsvector</c> column + GIN index, ranked with <c>ts_rank</c>. The query is built
/// as an OR-joined <c>to_tsquery('english', …)</c> from <see cref="FtsMatch.PostgresOrTsQuery"/> so
/// retrieval is broad match-any — mirroring the SQLite fts5 OR behavior (using <c>websearch_to_tsquery</c>,
/// which ANDs every term, made the pipeline's long "title + acceptance criteria" query match zero chunks).
/// Like the SQLite tables they live OUTSIDE EF and are created by idempotent DDL in <see cref="EnsureSchema"/>.
/// Insert/delete use provider-agnostic <c>ExecuteSqlRaw</c> (the generated <c>tsv</c> column maintains
/// itself); only the ranked reads are Postgres-specific.
/// </summary>
public sealed class PostgresFtsSearch : ITextSearchProvider
{
    private const string DocChunksDdl = """
        CREATE TABLE IF NOT EXISTS doc_chunks (
            id         bigserial PRIMARY KEY,
            content    text    NOT NULL,
            project_id integer NOT NULL,
            doc_id     integer NOT NULL,
            tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
        );
        CREATE INDEX IF NOT EXISTS ix_doc_chunks_tsv ON doc_chunks USING GIN (tsv);
        CREATE INDEX IF NOT EXISTS ix_doc_chunks_project ON doc_chunks (project_id);
        """;

    private const string TestIndexDdl = """
        CREATE TABLE IF NOT EXISTS test_case_index (
            id            bigserial PRIMARY KEY,
            title         text    NOT NULL,
            steps_summary text    NOT NULL DEFAULT '',
            scan_id       integer NOT NULL,
            entry_id      integer NOT NULL,
            tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', title || ' ' || steps_summary)) STORED
        );
        CREATE INDEX IF NOT EXISTS ix_test_case_index_tsv ON test_case_index USING GIN (tsv);
        CREATE INDEX IF NOT EXISTS ix_test_case_index_scan ON test_case_index (scan_id);
        """;

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
        // OR-join the query tokens (broad match-any), mirroring SQLite. Null = no usable token.
        var tsQuery = FtsMatch.PostgresOrTsQuery(query);
        if (tsQuery is null)
            return [];

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var mustClose = conn.State != System.Data.ConnectionState.Open;
        if (mustClose)
            await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            // Tokens are [A-Za-z0-9_]+ OR-joined with ' | ', so to_tsquery never throws; higher ts_rank = better.
            cmd.CommandText =
                """
                SELECT doc_id, content, ts_rank(tsv, to_tsquery('english', @q)) AS score
                FROM doc_chunks
                WHERE tsv @@ to_tsquery('english', @q) AND project_id = @p
                ORDER BY score DESC
                LIMIT @k
                """;
            cmd.Parameters.AddWithValue("@q", tsQuery);
            cmd.Parameters.AddWithValue("@p", projectId);
            cmd.Parameters.AddWithValue("@k", topK);

            var hits = new List<DocChunkHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new DocChunkHit(
                    Convert.ToInt32(reader.GetValue(0)),
                    reader.GetString(1),
                    Convert.ToDouble(reader.GetValue(2))));
            return hits;
        }
        catch { return []; }
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
        var tsQuery = FtsMatch.PostgresOrTsQuery(query);
        if (tsQuery is null)
            return [];

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var mustClose = conn.State != System.Data.ConnectionState.Open;
        if (mustClose)
            await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT entry_id
                FROM test_case_index
                WHERE tsv @@ to_tsquery('english', @q) AND scan_id = @sid
                ORDER BY ts_rank(tsv, to_tsquery('english', @q)) DESC
                LIMIT @top
                """;
            cmd.Parameters.AddWithValue("@q", tsQuery);
            cmd.Parameters.AddWithValue("@sid", scanId);
            cmd.Parameters.AddWithValue("@top", top);

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
