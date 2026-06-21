using Microsoft.AspNetCore.Mvc;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Frontend log ingestion (free, DB-only). <c>POST /api/logs</c> accepts a batch of client log entries
/// and stores each as an <c>ErrorLog</c> (Source = "frontend"). There is intentionally no GET / viewer —
/// debug by querying the SQLite <c>ErrorLogs</c> table directly.
/// </summary>
[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    private const int MaxBatch = 100;
    private const int MaxMessageLen = 8000;

    private readonly QaDbContext _db;

    public LogsController(QaDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Ingest([FromBody] List<ClientLogEntry>? entries, CancellationToken cancellationToken)
    {
        if (entries is null || entries.Count == 0)
            return Ok(new { stored = 0 });

        var now = DateTime.UtcNow;
        var rows = entries
            .Take(MaxBatch)
            .Where(e => !string.IsNullOrWhiteSpace(e.Message))
            .Select(e => new ErrorLog
            {
                CreatedAt = now,
                Source = "frontend",
                Level = NormalizeLevel(e.Level),
                Category = e.Source,
                Message = Truncate(e.Message, MaxMessageLen),
                Exception = e.Stack,
                Path = e.Url,
                Context = e.Context ?? (string.IsNullOrEmpty(e.UserAgent) ? null : e.UserAgent),
            })
            .ToList();

        if (rows.Count == 0)
            return Ok(new { stored = 0 });

        _db.ErrorLogs.AddRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { stored = rows.Count });
    }

    private static string NormalizeLevel(string? level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "warn" or "warning" => "Warning",
            "critical" or "fatal" => "Critical",
            _ => "Error",
        };

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
