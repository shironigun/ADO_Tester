using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Batches;

/// <summary>Epic/Feature batch runs (spec B8). Port of the Python batches router logic.</summary>
public sealed class BatchService : IBatchService
{
    private static readonly IReadOnlySet<string> Terminal =
        new HashSet<string> { "Done", "Drafted", "Failed", "Cancelled" };

    private readonly QaDbContext _db;
    private readonly IAgentOrchestrator _agent;
    private readonly IBugOrchestrator _bug;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILogger<BatchService> _log;

    public BatchService(
        QaDbContext db, IAgentOrchestrator agent, IBugOrchestrator bug, IAzureDevOpsClient ado, ILogger<BatchService> log)
    {
        _db = db;
        _agent = agent;
        _bug = bug;
        _ado = ado;
        _log = log;
    }

    public async Task<ChildrenExpandResponse> ChildrenExpandAsync(int parentWorkItemId, bool recursive, CancellationToken cancellationToken = default)
    {
        var parent = await _ado.GetWorkItemAsync(parentWorkItemId, ct: cancellationToken);
        var (items, truncated) = await _ado.GetChildWorkItemsAsync(parentWorkItemId, recursive, ct: cancellationToken);
        return new ChildrenExpandResponse
        {
            ParentWorkItemId = parentWorkItemId,
            ParentTitle = parent.Title,
            Children = items.Select(c => new BatchChild { Id = c.Id, Title = c.Title, WorkItemType = c.WorkItemType, State = c.State }).ToList(),
            Truncated = truncated,
        };
    }

    public async Task<BatchStartResponse> StartAsync(string module, int projectId, BatchStartRequest request, CancellationToken cancellationToken = default)
    {
        // Sanitize ids: drop <=0, de-dupe preserving first-seen order.
        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var i in request.WorkItemIds)
            if (i > 0 && seen.Add(i))
                ids.Add(i);

        var mode = (request.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "manual"))
            mode = "";
        // Mode is persisted only for find-bugs; author-tests always stores null.
        BatchMode? modeEnum = module == "find-bugs" && mode.Length > 0 ? BatchEnumExtensions.ToBatchMode(mode) : null;

        var title = "";
        if (request.ParentWorkItemId is { } pid && pid != 0)
        {
            try { title = (await _ado.GetWorkItemAsync(pid, ct: cancellationToken)).Title; }
            catch (AdoApiError) { title = $"Work item #{pid}"; }
        }
        if (string.IsNullOrEmpty(title))
            title = $"{ids.Count} stor{(ids.Count == 1 ? "y" : "ies")}";

        var now = DateTime.UtcNow;
        var batch = new Batch
        {
            ProjectId = projectId,
            Module = BatchEnumExtensions.ToBatchModule(module),
            ParentWorkItemId = request.ParentWorkItemId,
            Title = title,
            Mode = modeEnum,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Batches.Add(batch);
        await _db.SaveChangesAsync(cancellationToken);
        var batchId = batch.Id;

        // Fan out concurrently (return-exceptions semantics): failures land in `skipped`, not abort.
        var isAuthor = module == "author-tests";
        var auto = mode == "auto";
        var tasks = ids.Select(async wid =>
        {
            try
            {
                var sid = isAuthor
                    ? await _agent.StartAsync(projectId, wid, autoHandoff: true, batchId: batchId, cancellationToken: cancellationToken)
                    : await _bug.StartAsync(projectId, wid, auto: auto, batchId: batchId, cancellationToken: cancellationToken);
                return (wid, sid, ok: true);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Batch child {WorkItemId} failed to start", wid);
                return (wid, sid: 0, ok: false);
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);

        return new BatchStartResponse
        {
            BatchId = batchId,
            SessionIds = results.Where(r => r.ok).Select(r => r.sid).ToList(),
            Skipped = results.Where(r => !r.ok).Select(r => r.wid).ToList(),
        };
    }

    public async Task<BatchDetail?> DetailAsync(int batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _db.Batches.FindAsync([batchId], cancellationToken);
        if (batch is null)
            return null;

        List<BatchChildSummary> children;
        if (batch.Module == BatchModule.AuthorTests)
        {
            var sessions = await _db.TestSessions.Where(s => s.BatchId == batchId).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
            var sids = sessions.Select(s => s.Id).ToList();
            var counts = await _db.TestCaseDrafts.Where(d => sids.Contains(d.SessionId))
                .GroupBy(d => d.SessionId).Select(g => new { g.Key, C = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.C, cancellationToken);
            children = sessions.Select(s => new BatchChildSummary
            {
                SessionId = s.Id, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
                Status = s.Status.ToString(), CurrentActivity = s.CurrentActivity, ErrorMessage = s.ErrorMessage,
                Count = counts.GetValueOrDefault(s.Id),
            }).ToList();
        }
        else
        {
            var sessions = await _db.BugSessions.Where(s => s.BatchId == batchId).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
            var sids = sessions.Select(s => s.Id).ToList();
            var counts = await _db.BugDrafts.Where(d => sids.Contains(d.SessionId))
                .GroupBy(d => d.SessionId).Select(g => new { g.Key, C = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.C, cancellationToken);
            children = sessions.Select(s => new BatchChildSummary
            {
                SessionId = s.Id, WorkItemId = s.WorkItemId, WorkItemTitle = s.WorkItemTitle,
                Status = s.Status.ToString(), CurrentActivity = s.CurrentActivity, ErrorMessage = s.ErrorMessage,
                Count = counts.GetValueOrDefault(s.Id),
            }).ToList();
        }

        var derived = BatchRollup.Classify(children.Select(c => c.Status).ToList()).DerivedStatus;
        return new BatchDetail
        {
            Id = batch.Id, Module = batch.Module.ToWire(), ParentWorkItemId = batch.ParentWorkItemId,
            Title = batch.Title, Mode = batch.Mode?.ToWire(), CreatedAt = batch.CreatedAt, UpdatedAt = batch.UpdatedAt,
            DerivedStatus = derived, Children = children,
        };
    }

    public async Task<bool?> CancelAsync(int batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _db.Batches.FindAsync([batchId], cancellationToken);
        if (batch is null)
            return null;

        var now = DateTime.UtcNow;
        int signalled = 0, forced = 0;

        if (batch.Module == BatchModule.AuthorTests)
        {
            var rows = await _db.TestSessions.Where(s => s.BatchId == batchId).ToListAsync(cancellationToken);
            foreach (var x in rows)
            {
                if (_agent.Cancel(x.Id)) { signalled++; continue; }
                if (!Terminal.Contains(x.Status.ToString()))
                {
                    x.Status = SessionStatus.Cancelled;
                    x.CurrentActivity = "Cancelled (epic run)";
                    x.UpdatedAt = now;
                    forced++;
                }
            }
        }
        else
        {
            var rows = await _db.BugSessions.Where(s => s.BatchId == batchId).ToListAsync(cancellationToken);
            foreach (var x in rows)
            {
                if (_bug.Cancel(x.Id)) { signalled++; continue; }
                if (!Terminal.Contains(x.Status.ToString()))
                {
                    x.Status = BugSessionStatus.Cancelled;
                    x.CurrentActivity = "Cancelled (epic run)";
                    x.UpdatedAt = now;
                    forced++;
                }
            }
        }

        if (forced > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return (signalled + forced) > 0;
    }

    public async Task<BatchList> ListAsync(int projectId, int skip, int take, CancellationToken cancellationToken = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);

        var total = await _db.Batches.CountAsync(b => b.ProjectId == projectId, cancellationToken);
        var batches = await _db.Batches
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
        var ids = batches.Select(b => b.Id).ToList();

        // One status query per module (no N+1).
        var authorStatuses = await _db.TestSessions
            .Where(s => s.BatchId != null && ids.Contains(s.BatchId.Value))
            .Select(s => new { s.BatchId, s.Status })
            .ToListAsync(cancellationToken);
        var bugStatuses = await _db.BugSessions
            .Where(s => s.BatchId != null && ids.Contains(s.BatchId.Value))
            .Select(s => new { s.BatchId, s.Status })
            .ToListAsync(cancellationToken);

        var items = batches.Select(b =>
        {
            var statuses = b.Module == BatchModule.AuthorTests
                ? authorStatuses.Where(x => x.BatchId == b.Id).Select(x => x.Status.ToString()).ToList()
                : bugStatuses.Where(x => x.BatchId == b.Id).Select(x => x.Status.ToString()).ToList();
            var r = BatchRollup.Classify(statuses);
            return new BatchSummary
            {
                Id = b.Id, Module = b.Module.ToWire(), ParentWorkItemId = b.ParentWorkItemId, Title = b.Title,
                Mode = b.Mode?.ToWire(), CreatedAt = b.CreatedAt,
                ChildCount = r.ChildCount, DoneCount = r.DoneCount, FailedCount = r.FailedCount,
                RunningCount = r.RunningCount, DerivedStatus = r.DerivedStatus,
            };
        }).ToList();

        return new BatchList { Total = total, Items = items };
    }
}
