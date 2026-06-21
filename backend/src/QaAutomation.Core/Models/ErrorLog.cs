namespace QaAutomation.Core.Models;

/// <summary>
/// A captured Warning/Error/Critical log entry, persisted for later debugging. Free and DB-only — no
/// external service, no UI viewer (query the SQLite <c>ErrorLogs</c> table directly when debugging).
/// Written by the backend <c>DbLogger</c> (<see cref="Source"/> = "backend") and by the frontend logger
/// via <c>POST /api/logs</c> (<see cref="Source"/> = "frontend"). Table: ErrorLogs.
/// </summary>
public sealed class ErrorLog
{
    public int Id { get; set; }

    /// <summary>UTC timestamp the entry was captured.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>"backend" | "frontend".</summary>
    public string Source { get; set; } = "backend";

    /// <summary>Warning | Error | Critical.</summary>
    public string Level { get; set; } = "Error";

    /// <summary>Logger category (backend) or component/area (frontend). Nullable.</summary>
    public string? Category { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Stack trace / exception details. Nullable.</summary>
    public string? Exception { get; set; }

    /// <summary>Request path (backend) or page URL (frontend). Nullable.</summary>
    public string? Path { get; set; }

    /// <summary>Optional JSON blob (user agent, extra fields). Nullable.</summary>
    public string? Context { get; set; }
}
