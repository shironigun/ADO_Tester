using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Tests;

/// <summary>
/// Verifies the two D6 bring-up paths of <see cref="DatabaseInitializer"/>:
/// adopting an existing Alembic-managed qa.db (no data loss) and creating a fresh one.
/// Always runs against a COPY/temp file — never the live database.
/// </summary>
public sealed class DatabaseInitializerTests
{
    private static readonly string[] Tables =
        ["Projects", "Settings", "Documents", "TestSessions", "TestCaseDrafts", "BugSessions", "BugDrafts", "Reports", "Batches"];

    private static QaDbContext NewContext(string dbPath)
        => new(new DbContextOptionsBuilder<QaDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static void Initialize(string dbPath)
    {
        using var ctx = NewContext(dbPath);
        new DatabaseInitializer(ctx, NullLogger<DatabaseInitializer>.Instance).Initialize();
    }

    [Fact]
    public void Adopts_existing_alembic_db_without_data_loss()
    {
        var live = FindLiveDb();
        Assert.True(live is not null, "Live backend/data/qa.db not found — cannot run the adoption test.");

        var copy = TempDbPath("adopt");
        File.Copy(live!, copy, overwrite: true);
        try
        {
            // Snapshot every table's row count BEFORE EF touches the file.
            var before = Tables.ToDictionary(t => t, t => RawCount(copy, t));
            Assert.True(before["TestSessions"] > 0, "Expected real data in the live DB to make the test meaningful.");

            Initialize(copy);

            using var ctx = NewContext(copy);

            // No data loss: EF-mapped counts equal the pre-adoption raw counts (also proves EF reads every table).
            Assert.Equal(before["Projects"], ctx.Projects.LongCount());
            Assert.Equal(before["Settings"], ctx.Settings.LongCount());
            Assert.Equal(before["Documents"], ctx.Documents.LongCount());
            Assert.Equal(before["TestSessions"], ctx.TestSessions.LongCount());
            Assert.Equal(before["TestCaseDrafts"], ctx.TestCaseDrafts.LongCount());
            Assert.Equal(before["BugSessions"], ctx.BugSessions.LongCount());
            Assert.Equal(before["BugDrafts"], ctx.BugDrafts.LongCount());
            Assert.Equal(before["Reports"], ctx.Reports.LongCount());
            Assert.Equal(before["Batches"], ctx.Batches.LongCount());

            // EF reads SQLAlchemy-written values: DateTime parses, string→enum conversions map.
            var session = ctx.TestSessions.OrderBy(s => s.Id).First();
            Assert.True(session.CreatedAt > new DateTime(2020, 1, 1));
            Assert.True(Enum.IsDefined(session.Status));
            Assert.True(Enum.IsDefined(ctx.Reports.First().Theme));
            Assert.True(Enum.IsDefined(ctx.Batches.First().Module));   // 'author-tests'/'find-bugs' via converter

            // Baseline recorded as applied; the original schema artifacts are untouched.
            Assert.Equal(1, RawScalar(copy, "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE \"MigrationId\" LIKE '%InitialBaseline'"));
            Assert.Equal(1, RawScalar(copy, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='doc_chunks'"));
            Assert.Equal(1, RawScalar(copy, "SELECT COUNT(*) FROM alembic_version"));  // Alembic bookkeeping preserved
        }
        finally
        {
            Cleanup(copy);
        }
    }

    [Fact]
    public void Builds_fresh_database_seeds_project_and_is_idempotent()
    {
        var fresh = TempDbPath("fresh");
        Cleanup(fresh);
        try
        {
            Initialize(fresh);

            using (var ctx = NewContext(fresh))
            {
                var project = Assert.Single(ctx.Projects);
                Assert.Equal(1, project.Id);
                Assert.Equal("Default Project", project.Name);
                Assert.Equal("", project.AdoOrganization);
                Assert.Equal("", project.AdoProject);
                Assert.Equal(0, ctx.TestSessions.Count());
            }

            // All frozen tables + the FTS virtual table exist.
            foreach (var t in Tables)
                Assert.Equal(1, RawScalar(fresh, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{t}'"));
            Assert.Equal(1, RawScalar(fresh, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='doc_chunks'"));

            // Idempotent: a second run neither re-seeds nor fails.
            Initialize(fresh);
            using (var ctx = NewContext(fresh))
                Assert.Single(ctx.Projects);
        }
        finally
        {
            Cleanup(fresh);
        }
    }

    [Fact]
    public void Enums_persist_as_their_exact_wire_strings()
    {
        // 🔴 EF must store enums as their exact string value, NOT as int and NOT camelCased. BatchModule
        // and BatchMode use non-PascalCase wire strings via a custom converter; if that regresses (e.g.
        // EF's default int mapping), the adopted Python DB silently breaks. Round-trip and read raw.
        var db = TempDbPath("enums");
        Cleanup(db);
        try
        {
            Initialize(db);
            using (var ctx = NewContext(db))
            {
                ctx.Batches.Add(new Batch
                {
                    ProjectId = 1,
                    Module = BatchModule.AuthorTests,
                    Mode = BatchMode.Auto,
                    Title = "t",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                ctx.SaveChanges();
            }

            // Custom-converter enums store kebab/lowercase wire strings, not the C# member names.
            Assert.Equal("author-tests", RawString(db, "SELECT Module FROM Batches LIMIT 1"));
            Assert.Equal("auto", RawString(db, "SELECT Mode FROM Batches LIMIT 1"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? FindLiveDb()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "data", "qa.db");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string TempDbPath(string tag) => Path.Combine(Path.GetTempPath(), $"qa_{tag}_{Guid.NewGuid():N}.db");

    private static long RawCount(string dbPath, string table) =>
        RawScalar(dbPath, $"SELECT COUNT(*) FROM \"{table}\"");

    private static long RawScalar(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string? RawString(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static void Cleanup(string dbPath)
    {
        SqliteConnection.ClearAllPools();   // release file handles so the temp file can be deleted
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
