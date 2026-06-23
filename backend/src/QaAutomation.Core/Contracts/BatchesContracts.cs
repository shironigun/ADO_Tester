namespace QaAutomation.Core.Contracts;

// Batch (Epic/Feature run) DTOs (spec B8). camelCase wire. module/mode are wire strings
// (author-tests/find-bugs, auto/manual). derivedStatus is computed at read time (never stored).

public sealed class BatchChild
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string WorkItemType { get; set; } = "";
    public string? State { get; set; }
}

public sealed class ChildrenExpandResponse
{
    public int ParentWorkItemId { get; set; }
    public string ParentTitle { get; set; } = "";
    public IReadOnlyList<BatchChild> Children { get; set; } = [];
    public bool Truncated { get; set; }
}

public sealed class BatchStartRequest
{
    public int? ParentWorkItemId { get; set; }
    public List<int> WorkItemIds { get; set; } = [];
    public string? Mode { get; set; }   // 'auto' | 'manual' (find-bugs only)
}

public sealed class BatchStartResponse
{
    public int BatchId { get; set; }
    public IReadOnlyList<int> SessionIds { get; set; } = [];
    public IReadOnlyList<int> Skipped { get; set; } = [];
}

public sealed class BatchChildSummary
{
    public int SessionId { get; set; }
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public string Status { get; set; } = "";
    public string? CurrentActivity { get; set; }
    public string? ErrorMessage { get; set; }
    public int Count { get; set; }
}

public sealed class BatchDetail
{
    public int Id { get; set; }
    public string Module { get; set; } = "";
    public int? ParentWorkItemId { get; set; }
    public string Title { get; set; } = "";
    public string? Mode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string DerivedStatus { get; set; } = "";
    public IReadOnlyList<BatchChildSummary> Children { get; set; } = [];
}

public sealed class BatchSummary
{
    public int Id { get; set; }
    public string Module { get; set; } = "";
    public int? ParentWorkItemId { get; set; }
    public string Title { get; set; } = "";
    public string? Mode { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ChildCount { get; set; }
    public int DoneCount { get; set; }
    public int FailedCount { get; set; }
    public int RunningCount { get; set; }
    public string DerivedStatus { get; set; } = "";
}

public sealed class BatchList
{
    public int Total { get; set; }
    public IReadOnlyList<BatchSummary> Items { get; set; } = [];
}

/// <summary>Read-time rollup over a batch's child statuses (spec B8 <c>_classify</c>).
/// awaiting/cancelled counts are computed but intentionally NOT surfaced.</summary>
public readonly record struct BatchRollup(int ChildCount, int DoneCount, int FailedCount, int RunningCount, string DerivedStatus)
{
    private static readonly IReadOnlySet<string> Done = new HashSet<string> { "Done", "Drafted" };
    private static readonly IReadOnlySet<string> Awaiting = new HashSet<string> { "AwaitingActual", "AwaitingTester" };

    public static BatchRollup Classify(IReadOnlyCollection<string> statuses)
    {
        var total = statuses.Count;
        var done = statuses.Count(Done.Contains);
        var failed = statuses.Count(s => s == "Failed");
        var cancelled = statuses.Count(s => s == "Cancelled");
        var awaiting = statuses.Count(Awaiting.Contains);
        var running = total - done - failed - cancelled - awaiting;

        var derived =
            total == 0 ? "Empty"
            : running > 0 ? "Running"
            : awaiting > 0 ? "AwaitingInput"
            : failed > 0 ? "CompletedWithFailures"
            : done == 0 && cancelled > 0 ? "Cancelled"
            : "Completed";

        return new BatchRollup(total, done, failed, running, derived);
    }
}
