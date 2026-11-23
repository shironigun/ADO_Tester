using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

/// <summary>
/// Isolated PostgreSQL database for a test fixture. Creates a unique DB, applies EF migrations + FTS
/// schema on demand, and drops the DB on dispose. Requires a reachable Postgres (local Docker or CI).
/// </summary>
public sealed class PostgresTestDatabase : IDisposable
{
    private readonly string _adminConnectionString;
    private bool _dropped;

    public string ConnectionString { get; }
    public string DatabaseName { get; }

    private PostgresTestDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    /// <summary>Creates a fresh empty database. Call <see cref="InitializeSchema"/> before use.</summary>
    public static PostgresTestDatabase Create(string? label = null)
    {
        var baseCs = ResolveBaseConnectionString();
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseCs) { Database = "postgres" };
        var adminCs = adminBuilder.ConnectionString;

        var raw = $"qa_t_{(label ?? "x")}_{Guid.NewGuid():N}";
        var dbName = Regex.Replace(raw, @"[^a-zA-Z0-9_]", "_");
        if (dbName.Length > 63)
            dbName = dbName[..63];

        using (var conn = new NpgsqlConnection(adminCs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            cmd.ExecuteNonQuery();
        }

        var dbBuilder = new NpgsqlConnectionStringBuilder(adminCs) { Database = dbName };
        return new PostgresTestDatabase(adminCs, dbName, dbBuilder.ConnectionString);
    }

    public void Configure(DbContextOptionsBuilder options) =>
        options.UseNpgsql(ConnectionString, n => n.MigrationsAssembly("QaAutomation.Migrations.Npgsql"));

    public QaDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<QaDbContext>();
        Configure(opts);
        return new QaDbContext(opts.Options);
    }

    public void InitializeSchema()
    {
        using var db = CreateContext();
        new DatabaseInitializer(db, new PostgresFtsSearch(), NullLogger<DatabaseInitializer>.Instance).Initialize();
    }

    public void Dispose()
    {
        if (_dropped)
            return;
        _dropped = true;
        try
        {
            using var conn = new NpgsqlConnection(_adminConnectionString);
            conn.Open();
            using (var term = conn.CreateCommand())
            {
                term.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                    "WHERE datname = @db AND pid <> pg_backend_pid()";
                term.Parameters.AddWithValue("db", DatabaseName);
                term.ExecuteNonQuery();
            }
            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\"";
                drop.ExecuteNonQuery();
            }
        }
        catch
        {
            // best effort — leftover test DBs are harmless and can be wiped manually
        }
    }

    private static string ResolveBaseConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return "Host=localhost;Port=5432;Username=qa;Password=qa;Database=qa";
    }
}
