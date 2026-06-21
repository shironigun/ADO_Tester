using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// Sets <c>PRAGMA busy_timeout</c> on every EF-opened SQLite connection so that the concurrent
/// background pipelines (each on its own connection) and the API's reads WAIT for a contended write
/// to commit instead of failing immediately with "SQLite Error 5: database is locked". busy_timeout
/// is per-connection, so it must be (re)applied on each open.
/// </summary>
public sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteBusyTimeoutInterceptor Instance = new();

    /// <summary>Generous wait — real writes are sub-millisecond, so this only ever absorbs contention.</summary>
    public const int BusyTimeoutMs = 10_000;

    private static readonly string Pragma = $"PRAGMA busy_timeout={BusyTimeoutMs};";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken);

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
