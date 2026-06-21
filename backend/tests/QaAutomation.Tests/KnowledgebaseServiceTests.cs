using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Knowledgebase;

namespace QaAutomation.Tests;

/// <summary>
/// Verifies the B6 Knowledgebase contract: the ~1000-char greedy chunker, the FTS5 MATCH-expression
/// builder, and an upload → BM25 search → delete round-trip against a real SQLite FTS5 index.
/// </summary>
public sealed class KnowledgebaseServiceTests
{
    // ── chunker ────────────────────────────────────────────────────────────────

    [Fact]
    public void Chunk_keeps_small_paragraphs_together()
    {
        var chunks = KnowledgebaseService.ChunkMarkdown("A\n\nB");
        Assert.Equal(["A\n\nB"], chunks);
    }

    [Fact]
    public void Chunk_flushes_when_next_paragraph_would_overflow()
    {
        var a = new string('a', 600);
        var b = new string('b', 600);
        var chunks = KnowledgebaseService.ChunkMarkdown($"{a}\n\n{b}");
        Assert.Equal(2, chunks.Count);
        Assert.Equal(a, chunks[0]);
        Assert.Equal(b, chunks[1]);
    }

    [Fact]
    public void Chunk_does_not_subsplit_an_oversized_paragraph()
    {
        var huge = new string('c', 1500);
        var chunks = KnowledgebaseService.ChunkMarkdown(huge);
        Assert.Single(chunks);
        Assert.Equal(1500, chunks[0].Length);
    }

    // ── match expression ─────────────────────────────────────────────────────────

    [Fact]
    public void Match_quotes_and_ors_tokens_dropping_single_chars()
    {
        Assert.Equal("\"bb\" OR \"dd\"", KnowledgebaseService.MatchExpression("a bb c dd"));
    }

    [Fact]
    public void Match_dedupes_preserving_order()
    {
        Assert.Equal("\"test\" OR \"foo\"", KnowledgebaseService.MatchExpression("test test foo test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a ! ?")]
    public void Match_returns_null_when_no_usable_token(string query)
    {
        Assert.Null(KnowledgebaseService.MatchExpression(query));
    }

    [Fact]
    public void Match_caps_at_twenty_tokens()
    {
        var query = string.Join(' ', Enumerable.Range(0, 25).Select(i => $"tok{i:D2}"));
        var expr = KnowledgebaseService.MatchExpression(query)!;
        Assert.Equal(20, expr.Split(" OR ").Length);
    }

    // ── round-trip ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_search_delete_round_trip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qa_kb_{Guid.NewGuid():N}.db");
        var dataDir = Path.Combine(Path.GetTempPath(), $"qa_kb_data_{Guid.NewGuid():N}");
        try
        {
            using (var ctx = NewContext(dbPath))
                new DatabaseInitializer(ctx, NullLogger<DatabaseInitializer>.Instance).Initialize();

            using var db = NewContext(dbPath);
            var svc = new KnowledgebaseService(
                db,
                Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }),
                NullLogger<KnowledgebaseService>.Instance);

            const string markdown =
                "The quick brown fox jumps over the lazy dog.\n\n" +
                "Customers can search inventory by VIN and stock number.\n\n" +
                "The fox returns to the den at midnight.";

            var doc = await svc.UploadAsync(1, "domain/base.md", markdown);

            // Stored with a uuid prefix, basename-stripped, under kb/{projectId}/.
            Assert.True(doc.Id > 0);
            Assert.Equal("base.md", doc.FileName);
            Assert.True(File.Exists(doc.StoredPath));
            Assert.StartsWith(Path.Combine(dataDir, "kb", "1"), doc.StoredPath);
            Assert.Equal(KnowledgebaseService.ChunkMarkdown(markdown).Count, doc.ChunkCount);

            // BM25 search finds the matching chunk(s), maps the file name, and returns ascending scores.
            var hits = await svc.SearchAsync(1, "fox inventory");
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal("base.md", h.FileName));
            Assert.True(hits.Select(h => h.Score).SequenceEqual(hits.Select(h => h.Score).OrderBy(s => s)),
                "hits must be ordered by ascending bm25 score");

            // A valid token with no index match yields no hits.
            Assert.Empty(await svc.SearchAsync(1, "nonexistentzzz"));

            // Delete removes the row, the chunks, and the file (idempotent on re-delete).
            await svc.DeleteAsync(doc.Id);
            Assert.Empty(await svc.ListAsync(1));
            Assert.Empty(await svc.SearchAsync(1, "fox inventory"));
            Assert.False(File.Exists(doc.StoredPath));
            await svc.DeleteAsync(doc.Id);   // no throw
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + s); } catch { /* best effort */ }
            }
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static QaDbContext NewContext(string dbPath)
        => new(new DbContextOptionsBuilder<QaDbContext>().UseSqlite($"Data Source={dbPath}").Options);
}
