namespace QaAutomation.Core.Configuration;

/// <summary>
/// Bound database settings. Connection string is the Npgsql PostgreSQL connection string,
/// read at boot from <c>POSTGRES_CONNECTION_STRING</c> or <c>DATABASE_URL</c>.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = "";
}
