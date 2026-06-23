using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Epic/Feature batch runs (spec B8): expand children, fan out one session per story under a Batch,
/// derive status from children at read time, and cancel a whole batch. The orchestration lives here;
/// the controller handles validation + HTTP status mapping.
/// </summary>
public interface IBatchService
{
    /// <summary>Children of an Epic/Feature for the pre-run picker. May throw <see cref="AdoApiError"/>.</summary>
    Task<ChildrenExpandResponse> ChildrenExpandAsync(int parentWorkItemId, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>Create the Batch and fan out a child session per (sanitized) work item id.
    /// <paramref name="module"/> is <c>author-tests</c> or <c>find-bugs</c>; ids are pre-sanitized non-empty.</summary>
    Task<BatchStartResponse> StartAsync(string module, int projectId, BatchStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Batch detail with per-child summaries + derived status. Null when the batch is missing.</summary>
    Task<BatchDetail?> DetailAsync(int batchId, CancellationToken cancellationToken = default);

    /// <summary>Cancel every child (signal in-flight, force-transition parked ones). Null when the batch is missing.</summary>
    Task<bool?> CancelAsync(int batchId, CancellationToken cancellationToken = default);

    /// <summary>Paged batch list with derived rollups for a project.</summary>
    Task<BatchList> ListAsync(int projectId, int skip, int take, CancellationToken cancellationToken = default);
}
