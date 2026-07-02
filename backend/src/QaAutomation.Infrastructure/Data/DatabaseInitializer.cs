using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// Brings the SQLite database up to date at startup (spec §6 B1 / D6). Two paths:
/// <list type="bullet">
/// <item><b>Adopt</b> — the live Alembic-built <c>qa.db</c> already has the app tables but no EF history.
/// Record the baseline migration as <i>already applied</i> (insert into <c>__EFMigrationsHistory</c>, no DDL)
/// so EF never tries to recreate existing tables. All data is preserved; <c>alembic_version</c> is left intact.</item>
/// <item><b>Fresh</b> — no app tables yet. <see cref="DatabaseFacade.Migrate"/> creates the full schema and
/// records the baseline.</item>
/// </list>
/// Then it bootstraps the FTS5 <c>doc_chunks</c> virtual table (EF cannot model virtual tables) and seeds
/// <c>Project</c> id=1. Idempotent: safe to run on every boot.
/// </summary>
public sealed class DatabaseInitializer
{
    private const string FtsBootstrapSql =
        "CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)";

    private static readonly string EfProductVersion =
        (typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "8.0.11").Split('+')[0];

    private readonly QaDbContext _db;
    private readonly ILogger<DatabaseInitializer> _log;

    public DatabaseInitializer(QaDbContext db, ILogger<DatabaseInitializer> log)
    {
        _db = db;
        _log = log;
    }

    public void Initialize()
    {
        if (ExistingSchemaNeedsAdoption())
            StampBaselineAsApplied();

        _db.Database.Migrate();   // fresh → create schema + history; adopted / up-to-date → no-op
        EnsureFtsTable();
        SeedProject();
        ReconcileOrphanedRuns();
    }

    /// <summary>
    /// A TestRun executes on an in-memory background task, so a backend restart orphans any run left
    /// in Queued/Running — it would otherwise display as "stuck" forever. Mark those Failed on boot.
    /// </summary>
    private void ReconcileOrphanedRuns()
    {
        var stuck = _db.TestRuns
            .Where(r => r.Status == TestRunStatus.Queued || r.Status == TestRunStatus.Running)
            .ToList();
        if (stuck.Count == 0)
            return;
        foreach (var r in stuck)
        {
            r.Status = TestRunStatus.Failed;
            r.ErrorMessage = "Orphaned by a backend restart while running.";
            r.FinishedAt = DateTime.UtcNow;
        }
        _db.SaveChanges();
        _log.LogInformation("Reconciled {Count} orphaned TestRun(s) as Failed after restart.", stuck.Count);
    }

    /// <summary>True when the app schema already exists (Alembic-built) but EF has not recorded its baseline.</summary>
    private bool ExistingSchemaNeedsAdoption()
    {
        if (!TableExists("TestSessions"))
            return false;   // fresh database — let Migrate() build everything
        if (TableExists("__EFMigrationsHistory") && BaselineRecorded())
            return false;   // already EF-managed
        return true;
    }

    private void StampBaselineAsApplied()
    {
        var baseline = _db.Database.GetMigrations().Single(m => m.EndsWith("InitialBaseline", StringComparison.Ordinal));
        _db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
            "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
            "\"ProductVersion\" TEXT NOT NULL)");
        _db.Database.ExecuteSqlRaw(
            "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})",
            baseline, EfProductVersion);
        _log.LogInformation(
            "Adopted existing Alembic database: recorded baseline {Baseline} as applied (no DDL ran).", baseline);
    }

    private void EnsureFtsTable() => _db.Database.ExecuteSqlRaw(FtsBootstrapSql);

    private void SeedProject()
    {
        if (_db.Projects.Any())
            return;   // fresh seed only — never overwrite an adopted project row
        _db.Projects.Add(new Project
        {
            Name = "Ideal Agile",
            AdoOrganization = "ConstellationDealer",
            AdoProject = "Ideal Agile",
        });
        _db.SaveChanges();
        _log.LogInformation("Seeded default Project id=1 'Ideal Agile'.");
    }

    private bool TableExists(string name) => Count(
        "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name = {0}", name) > 0;

    private bool BaselineRecorded() => Count(
        "SELECT COUNT(*) AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%InitialBaseline'") > 0;

    private long Count(string sql, params object[] parameters)
        => _db.Database.SqlQueryRaw<long>(sql, parameters).AsEnumerable().Single();
}
