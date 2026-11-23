using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Core.Contracts;

// ── Inventory ────────────────────────────────────────────────────────────────

public sealed class ScriptInventoryItemDto
{
    public string SpecFilePath { get; set; } = "";
    public string Module { get; set; } = "";
    public string Type { get; set; } = "";
    public string Level { get; set; } = "";
    public string? Bucket { get; set; }
    public int? AdoTestCaseId { get; set; }
    public int? DraftId { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public string ReconcileState { get; set; } = "";
    public QaMeta? Meta { get; set; }

    public static ScriptInventoryItemDto From(ScriptInventoryItem i) => new()
    {
        SpecFilePath = i.SpecFilePath, Module = i.Module, Type = i.Type, Level = i.Level,
        Bucket = i.Bucket, AdoTestCaseId = i.AdoTestCaseId, DraftId = i.DraftId,
        Tags = i.Tags, ReconcileState = i.ReconcileState, Meta = i.Meta,
    };
}

public sealed class ScriptInventoryResponse
{
    public int Total { get; set; }
    public IReadOnlyList<ScriptInventoryItemDto> Items { get; set; } = [];
}

public sealed class ScriptSpecContentResponse
{
    public string SpecFilePath { get; set; } = "";
    public string Code { get; set; } = "";
    public int? DraftId { get; set; }
    public int? AdoTestCaseId { get; set; }
    public string ReconcileState { get; set; } = "";
    public QaMeta? Meta { get; set; }
}

// ── Runs ─────────────────────────────────────────────────────────────────────

public sealed class ScriptBoardRunStartRequest
{
    public string? Module { get; set; }
    public string? Level { get; set; }
    public string? Type { get; set; }
    public string? Bucket { get; set; }
    public string? Tag { get; set; }
    public List<string>? SpecFilePaths { get; set; }
    public bool Headed { get; set; }
    public int? Workers { get; set; }
    public string? Profile { get; set; }
}

public sealed class ImpactSimulateRequest
{
    public List<string>? ChangedFiles { get; set; }
}

public sealed class ScriptBoardRunStartResponse
{
    public int RunId { get; set; }
}

public sealed class ScriptBoardRunSummary
{
    public int Id { get; set; }
    public string SelectionSummary { get; set; } = "";
    public string? Module { get; set; }
    public bool Headed { get; set; }
    public int? Workers { get; set; }
    public string? Profile { get; set; }
    public TestRunStatus Status { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? ReportPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ScriptBoardRunSummary From(ScriptBoardRun r) => new()
    {
        Id = r.Id, SelectionSummary = r.SelectionSummary, Module = r.Module,
        Headed = r.Headed, Workers = r.Workers, Profile = r.Profile, Status = r.Status,
        Passed = r.Passed, Failed = r.Failed, Skipped = r.Skipped, DurationMs = r.DurationMs,
        ReportPath = r.ReportPath,
        ErrorMessage = r.ErrorMessage, StartedAt = r.StartedAt, FinishedAt = r.FinishedAt,
        CreatedAt = r.CreatedAt,
    };
}

public sealed class ScriptBoardRunList
{
    public int Total { get; set; }
    public IReadOnlyList<ScriptBoardRunSummary> Items { get; set; } = [];
}

public sealed class ScriptBoardRunDetail
{
    public int Id { get; set; }
    public string SelectionSummary { get; set; } = "";
    public string? Module { get; set; }
    public IReadOnlyList<string> SpecFilePaths { get; set; } = [];
    public bool Headed { get; set; }
    public int? Workers { get; set; }
    public string? Profile { get; set; }
    public TestRunStatus Status { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? JunitPath { get; set; }
    public string? ReportPath { get; set; }
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ScriptBoardRunDetail From(ScriptBoardRun r) => new()
    {
        Id = r.Id, SelectionSummary = r.SelectionSummary, Module = r.Module,
        SpecFilePaths = TryDeserialize(r.SpecFilePathsJson),
        Headed = r.Headed, Workers = r.Workers, Profile = r.Profile, Status = r.Status,
        Passed = r.Passed, Failed = r.Failed, Skipped = r.Skipped, DurationMs = r.DurationMs,
        JunitPath = r.JunitPath, ReportPath = r.ReportPath, EventLogJson = r.EventLogJson,
        ErrorMessage = r.ErrorMessage, StartedAt = r.StartedAt, FinishedAt = r.FinishedAt,
        CreatedAt = r.CreatedAt,
    };

    private static List<string> TryDeserialize(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
