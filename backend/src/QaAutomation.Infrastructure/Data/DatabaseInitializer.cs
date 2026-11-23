using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// Brings the PostgreSQL database up to date at startup. Runs EF migrations, bootstraps FTS
/// (tsvector tables via <see cref="ITextSearchProvider"/>), seeds Project id=1, and reconciles
/// orphaned TestRuns. Idempotent: safe to run on every boot.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly QaDbContext _db;
    private readonly ITextSearchProvider _textSearch;
    private readonly ILogger<DatabaseInitializer> _log;

    public DatabaseInitializer(QaDbContext db, ITextSearchProvider textSearch, ILogger<DatabaseInitializer> log)
    {
        _db = db;
        _textSearch = textSearch;
        _log = log;
    }

    public void Initialize()
    {
        _db.Database.Migrate();
        _textSearch.EnsureSchema(_db);
        SeedProject();
        ReconcileOrphanedRuns();
    }

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

    private void SeedProject()
    {
        if (_db.Projects.Any())
            return;   // fresh seed only — never overwrite an existing project row
        _db.Projects.Add(new Project
        {
            Name = "Default Project",
            AdoOrganization = "",
            AdoProject = "",
        });
        _db.SaveChanges();
        _log.LogInformation("Seeded default Project id=1.");
    }
}
