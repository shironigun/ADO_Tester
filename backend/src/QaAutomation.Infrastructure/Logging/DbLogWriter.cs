using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Logging;

/// <summary>
/// Drains the <see cref="DbLogQueue"/> and persists entries to the ErrorLogs table in small batches.
/// Uses its own short-lived <see cref="QaDbContext"/> scope and swallows all errors so logging can
/// never crash or stall the app. Starts after the host builds — by then the DB has been migrated and
/// the ErrorLogs table exists.
/// </summary>
public sealed class DbLogWriter : BackgroundService
{
    private const int BatchMax = 100;

    private readonly DbLogQueue _queue;
    private readonly IServiceScopeFactory _scopes;

    public DbLogWriter(DbLogQueue queue, IServiceScopeFactory scopes)
    {
        _queue = queue;
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var first in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                    db.ErrorLogs.Add(first);
                    var n = 1;
                    while (n < BatchMax && _queue.TryRead(out var more))
                    {
                        db.ErrorLogs.Add(more);
                        n++;
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch
                {
                    // Never let logging break the app. A failed flush drops that batch.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }
}
