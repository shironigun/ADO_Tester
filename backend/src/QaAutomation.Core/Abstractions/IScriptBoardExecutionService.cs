namespace QaAutomation.Core.Abstractions;

/// <summary>Selection filter for an ad-hoc Scripts Board execution (Slab 3, QLV2-303). When
/// <see cref="SpecFilePaths"/> is non-empty it wins outright — the other filters are ignored. Otherwise
/// every non-null filter narrows the live <see cref="IScriptInventoryService"/> inventory; all-null
/// means "run everything" (the "all" execution option from the design).</summary>
public sealed record ScriptBoardRunFilter(
    string? Module, string? Level, string? Type, string? Bucket, string? Tag,
    IReadOnlyList<string>? SpecFilePaths, bool Headed, int? Workers);

/// <summary>Resolves a <see cref="ScriptBoardRunFilter"/> to concrete spec paths, persists a
/// <c>ScriptBoardRun</c> row, and executes it via <c>IPlaywrightRunner</c>. Scoped — resolved in
/// background-task DI scopes (mirrors <c>RunExecutionService</c>).</summary>
public interface IScriptBoardExecutionService
{
    /// <summary>Resolves the selection, inserts a Queued row, and returns its id immediately. Throws
    /// <see cref="ArgumentException"/> when the selection resolves to zero specs.</summary>
    Task<int> StartAsync(ScriptBoardRunFilter filter, CancellationToken ct = default);

    /// <summary>Runs the previously-queued row to completion. Called from the caller's own background
    /// Task + DI scope (mirrors <c>QaRunsController.Start</c>) — never call inline on the request scope.</summary>
    Task ExecuteAsync(int runId, CancellationToken ct = default);
}
