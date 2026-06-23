using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Logging;

/// <summary>
/// Bounded in-memory queue between the <see cref="DbLogger"/> (producer, on the logging hot path) and
/// the <see cref="DbLogWriter"/> background service (consumer). Drop-newest when full so logging never
/// blocks or fails the app. Registered as a singleton.
/// </summary>
public sealed class DbLogQueue
{
    private readonly Channel<ErrorLog> _channel =
        Channel.CreateBounded<ErrorLog>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public bool TryWrite(ErrorLog entry) => _channel.Writer.TryWrite(entry);
    public bool TryRead(out ErrorLog entry) => _channel.Reader.TryRead(out entry!);
    public IAsyncEnumerable<ErrorLog> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// <see cref="ILoggerProvider"/> that funnels Warning/Error/Critical entries into the
/// <see cref="DbLogQueue"/> for async persistence to the ErrorLogs table. EF Core's own categories and
/// this namespace are excluded to avoid recursion and SQL-log noise.
/// </summary>
public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly DbLogQueue _queue;
    public DbLoggerProvider(DbLogQueue queue) => _queue = queue;
    public ILogger CreateLogger(string categoryName) => new DbLogger(categoryName, _queue);
    public void Dispose() { }
}

internal sealed class DbLogger : ILogger
{
    private readonly string _category;
    private readonly DbLogQueue _queue;

    public DbLogger(string category, DbLogQueue queue)
    {
        _category = category;
        _queue = queue;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel >= LogLevel.Warning
           && !_category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
           && !_category.StartsWith("QaAutomation.Infrastructure.Logging", StringComparison.Ordinal);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        // Non-blocking; silently dropped if the queue is saturated. Logging must never throw.
        _queue.TryWrite(new ErrorLog
        {
            CreatedAt = DateTime.UtcNow,
            Source = "backend",
            Level = logLevel.ToString(),
            Category = _category,
            Message = message.Length > 8000 ? message[..8000] : message,
            Exception = exception?.ToString(),
        });
    }
}

public static class DbLoggingExtensions
{
    /// <summary>Registers the DB error-logger: the queue, the <see cref="ILoggerProvider"/> sink, and the
    /// background writer. The provider captures Warning+Error/Critical from anywhere in the app.</summary>
    public static IServiceCollection AddDbLogging(this IServiceCollection services)
    {
        services.AddSingleton<DbLogQueue>();
        services.AddSingleton<ILoggerProvider, DbLoggerProvider>();
        services.AddHostedService<DbLogWriter>();
        return services;
    }
}
