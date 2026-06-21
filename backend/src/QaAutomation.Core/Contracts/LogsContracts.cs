namespace QaAutomation.Core.Contracts;

/// <summary>One client-side log entry posted by the frontend logger to <c>POST /api/logs</c>.
/// Stored as an <c>ErrorLog</c> with Source = "frontend".</summary>
public sealed class ClientLogEntry
{
    /// <summary>"warn" | "error" (mapped to Warning/Error). Anything else → Error.</summary>
    public string? Level { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>JS stack trace / component stack. Optional.</summary>
    public string? Stack { get; set; }

    /// <summary>Page URL the error happened on. Optional (defaults to request referer if absent).</summary>
    public string? Url { get; set; }

    /// <summary>Originating component/area (e.g. "axios", "react", "unhandledrejection"). Optional.</summary>
    public string? Source { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Optional JSON string with extra context (status, payload, …).</summary>
    public string? Context { get; set; }
}
