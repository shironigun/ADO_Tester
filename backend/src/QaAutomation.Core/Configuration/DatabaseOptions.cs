namespace QaAutomation.Core.Configuration;

/// <summary>
/// Which database provider backs the app. Chosen once at startup from the <c>DATABASE_PROVIDER</c>
/// environment variable (default <see cref="Sqlite"/>); switching providers requires a restart because
/// EF binds the provider when <c>AddDbContext</c> runs. See
/// <c>docs/progress/RalphLoop/postgresql-migration/</c>.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>SQLite file database (the default; the app's original store).</summary>
    Sqlite,

    /// <summary>PostgreSQL via Npgsql — local Docker for dev, Azure Database for PostgreSQL in prod.</summary>
    Postgres,
}

/// <summary>
/// Bound database settings. <see cref="ConnectionString"/> is the SQLite <c>Data Source=…</c> or the
/// Npgsql connection string depending on <see cref="Provider"/>. Read at boot (not via the runtime
/// settings overlay, which cannot rebuild an already-registered <c>DbContext</c>).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "";
}
