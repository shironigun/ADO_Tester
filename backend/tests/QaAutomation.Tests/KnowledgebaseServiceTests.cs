using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

/// <summary>
/// Verifies the B6 Knowledgebase contract: the ~1000-char greedy chunker, PostgreSQL tsquery
/// builder, and an upload → search → delete round-trip against a real PostgreSQL FTS index.
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
        Assert.Equal("bb | dd", KnowledgebaseService.MatchExpression("a bb c dd"));
    }

    [Fact]
    public void Match_dedupes_preserving_order()
    {
        Assert.Equal("test | foo", KnowledgebaseService.MatchExpression("test test foo test"));
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
        Assert.Equal(20, expr.Split(" | ").Length);
    }

    // ── round-trip ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_search_delete_round_trip()
    {
        using var pg = PostgresTestDatabase.Create("knowledgebase");
        var dataDir = Path.Combine(Path.GetTempPath(), $"qa_kb_data_{Guid.NewGuid():N}");
        try
        {
            pg.InitializeSchema();

            using var db = pg.CreateContext();
            var svc = new KnowledgebaseService(
                db,
                new PostgresFtsSearch(),
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

            // ts_rank search finds matching chunks, maps the file name, and returns ascending scores.
            var hits = await svc.SearchAsync(1, "fox inventory");
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal("base.md", h.FileName));
            Assert.True(hits.Select(h => h.Score).SequenceEqual(hits.Select(h => h.Score).OrderBy(s => s)),
                "hits must be ordered by ascending ts_rank score");

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
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeleteFolder_removes_exact_path_and_descendants()
    {
        using var pg = PostgresTestDatabase.Create("kb_folder_del");
        var dataDir = Path.Combine(Path.GetTempPath(), $"qa_kb_folder_{Guid.NewGuid():N}");
        try
        {
            pg.InitializeSchema();
            using var db = pg.CreateContext();
            var svc = new KnowledgebaseService(
                db,
                new PostgresFtsSearch(),
                Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }),
                NullLogger<KnowledgebaseService>.Instance);

            await svc.UploadAsync(1, "keep.md", "root keep", folderPath: "");
            await svc.UploadAsync(1, "a.md", "parent file", folderPath: "modules");
            await svc.UploadAsync(1, "b.md", "child file", folderPath: "modules/atlas");
            await svc.UploadAsync(1, "c.md", "other tree", folderPath: "other");

            var deleted = await svc.DeleteFolderAsync(1, "modules");
            Assert.Equal(2, deleted);

            var remaining = await svc.ListAsync(1);
            Assert.Equal(2, remaining.Count);
            Assert.Contains(remaining, d => d.FileName == "keep.md");
            Assert.Contains(remaining, d => d.FileName == "c.md");
            Assert.DoesNotContain(remaining, d => d.FileName == "a.md");
            Assert.DoesNotContain(remaining, d => d.FileName == "b.md");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
