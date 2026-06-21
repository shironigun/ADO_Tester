using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Api.Streaming;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Api.Controllers;

/// <summary>
/// Find-Bugs endpoints (spec B9) — flat under <c>/api/find-bugs</c>. SSE (key <c>bug:{id}</c>), start,
/// run (code-verify + Bug Writer; verifyCode is forced true), linked-refs, test-cases (bare array),
/// bug-from-verdict, cancel/push/approve-all, session list+detail, analysis/draft PATCH.
/// </summary>
[ApiController]
[Route("api/find-bugs")]
public sealed class FindBugsController : ControllerBase
{
    private readonly IBugOrchestrator _bugs;
    private readonly IAzureDevOpsClient _ado;
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly ILogger<FindBugsController> _log;

    public FindBugsController(
        IBugOrchestrator bugs, IAzureDevOpsClient ado, IEventBus bus, QaDbContext db, ILogger<FindBugsController> log)
    {
        _bugs = bugs;
        _ado = ado;
        _bus = bus;
        _db = db;
        _log = log;
    }

    [HttpGet("sessions/{sessionId:int}/stream")]
    public async Task Stream(int sessionId, [FromQuery] string? lastEventId, CancellationToken cancellationToken)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"bug:{sessionId}", afterSeq, cancellationToken);
    }

    [HttpPost("{projectId:int}/start")]
    public async Task<IActionResult> Start(int projectId, [FromBody] BugStartRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkItemId <= 0)
            return StatusCode(400, new { detail = "Invalid work item ID." });
        try
        {
            await _ado.GetWorkItemAsync(request.WorkItemId, ct: cancellationToken);
        }
        catch (AdoApiError e)
        {
            return e.Status == 404
                ? StatusCode(404, new { detail = $"Work item #{request.WorkItemId} not found." })
                : StatusCode(502, new { detail = e.Message });
        }
        try
        {
            var sessionId = await _bugs.StartAsync(projectId, request.WorkItemId, cancellationToken: cancellationToken);
            return Ok(new BugStartResponse { SessionId = sessionId });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Find-Bugs start failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("sessions/{sessionId:int}/run")]
    public async Task<IActionResult> Run(int sessionId, [FromBody] BugRunRequest request, CancellationToken cancellationToken)
    {
        // verifyCode is forced true (the code-analysis phase is always on — matches the Python router).
        await _bugs.RunBugWriterAsync(
            sessionId, request.ActualBehavior ?? "", request.CodeContext, verifyCode: true,
            request.CommitIds, request.PrIds, request.SelectedTestCases, cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpGet("sessions/{sessionId:int}/linked-refs")]
    public async Task<IActionResult> LinkedRefs(int sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.BugSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
            return StatusCode(404, new { detail = "Session not found." });

        var changes = new List<LinkedChange>();
        try { changes = (await _ado.GetLinkedChangesAsync(session.WorkItemId, cancellationToken)).ToList(); }
        catch (AdoApiError) { /* swallowed — return an empty BugLinkedRefs (NOT 502) */ }

        return Ok(new BugLinkedRefs
        {
            Changes = changes.Select(c => new BugLinkedChange { Kind = c.Kind, Id = c.Id, Title = c.Title, Url = c.Url }).ToList(),
            PullRequestIds = changes.Where(c => c.Kind == "pr").Select(c => c.Id).ToList(),
            CommitShas = changes.Where(c => c.Kind == "commit").Select(c => c.Id).ToList(),
        });
    }

    [HttpGet("sessions/{sessionId:int}/test-cases")]
    public async Task<IActionResult> TestCases(int sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.BugSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
            return StatusCode(404, new { detail = "Session not found." });
        try
        {
            return Ok(await _bugs.ListTestCasesAsync(sessionId, cancellationToken));   // bare array
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("sessions/{sessionId:int}/bug-from-verdict")]
    public async Task<IActionResult> BugFromVerdict(int sessionId, [FromBody] BugFromVerdictRequest request, CancellationToken cancellationToken)
    {
        var session = await _db.BugSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
            return StatusCode(404, new { detail = "Session not found." });

        // The orchestrator reads SNAKE_CASE keys (mirrors the Python model_dump() without by_alias).
        var verdict = new JsonObject
        {
            ["test_case"] = request.TestCase,
            ["source"] = request.Source,
            ["ado_test_case_id"] = request.AdoTestCaseId,
            ["severity"] = request.Severity,
            ["evidence"] = request.Evidence,
            ["reasoning"] = request.Reasoning,
            ["failed_step_index"] = request.FailedStepIndex,
            ["mode"] = request.Mode,
            ["steps"] = new JsonArray(request.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["action"] = s.Action, ["expected"] = s.Expected, ["status"] = s.Status, ["actual"] = s.Actual,
            }).ToArray()),
        };

        int draftId;
        try
        {
            draftId = await _bugs.CreateBugFromVerdictAsync(sessionId, verdict, cancellationToken);
        }
        catch (InvalidOperationException e)   // pipeline still running → ValueError → 400
        {
            return StatusCode(400, new { detail = e.Message });
        }
        catch (ArgumentException e)            // session vanished mid-call
        {
            return StatusCode(404, new { detail = e.Message });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }

        var draft = await _db.BugDrafts.FindAsync([draftId], cancellationToken);
        if (draft is null)
            return StatusCode(500, new { detail = "Draft was not created." });
        return Ok(BugDraftDto.From(draft));
    }

    [HttpPost("sessions/{sessionId:int}/cancel")]
    public IActionResult Cancel(int sessionId)
        => Ok(new CancelResponse { Cancelled = _bugs.Cancel(sessionId) });

    [HttpPost("sessions/{sessionId:int}/push")]
    public async Task<IActionResult> Push(int sessionId, CancellationToken cancellationToken)
    {
        await _bugs.PushToAdoAsync(sessionId, cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpPost("sessions/{sessionId:int}/approve-all")]
    public async Task<IActionResult> ApproveAll(int sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var drafts = await _db.BugDrafts
                .Where(d => d.SessionId == sessionId && d.Status == BugDraftStatus.Draft)
                .ToListAsync(cancellationToken);
            foreach (var d in drafts)
                d.Status = BugDraftStatus.Approved;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(new ApproveAllResponse { ApprovedCount = drafts.Count });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("sessions/{projectId:int}")]
    public async Task<IActionResult> ListSessions(int projectId, [FromQuery] int skip = 0, [FromQuery] int take = 30, CancellationToken cancellationToken = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);
        try
        {
            var total = await _db.BugSessions.CountAsync(s => s.ProjectId == projectId, cancellationToken);
            var items = await _db.BugSessions
                .Where(s => s.ProjectId == projectId)
                .OrderByDescending(s => s.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync(cancellationToken);
            return Ok(new BugSessionList { Total = total, Items = items.Select(BugSessionSummary.From).ToList() });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpGet("sessions/detail/{sessionId:int}")]
    public async Task<IActionResult> Detail(int sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.BugSessions.Include(s => s.Drafts).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null)
                return StatusCode(404, new { detail = "Session not found." });
            return Ok(BugSessionDetail.From(session, session.Drafts));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPatch("sessions/{sessionId:int}/analysis")]
    public async Task<IActionResult> UpdateAnalysis(int sessionId, [FromBody] BugAnalysisUpdate request, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.BugSessions.FindAsync([sessionId], cancellationToken);
            if (session is null)
                return StatusCode(404, new { detail = "Session not found." });
            session.AnalysisJson = request.ToAnalystOutput().ToJson();
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var drafts = await _db.BugDrafts.Where(d => d.SessionId == sessionId).ToListAsync(cancellationToken);
            return Ok(BugSessionDetail.From(session, drafts));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPatch("drafts/{draftId:int}")]
    public async Task<IActionResult> UpdateDraft(int draftId, [FromBody] UpdateBugDraftRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await _db.BugDrafts.FindAsync([draftId], cancellationToken);
            if (draft is null)
                return StatusCode(404, new { detail = "Draft not found." });
            if (request.Title is not null) draft.Title = request.Title;
            if (request.Severity is not null) draft.Severity = request.Severity.Value;
            if (request.Preconditions is not null) draft.Preconditions = request.Preconditions;
            if (request.ReproStepsJson is not null) draft.ReproStepsJson = request.ReproStepsJson;
            if (request.ExpectedBehavior is not null) draft.ExpectedBehavior = request.ExpectedBehavior;
            if (request.ActualBehavior is not null) draft.ActualBehavior = request.ActualBehavior;
            if (request.SuggestedFix is not null) draft.SuggestedFix = request.SuggestedFix;
            if (request.Status is not null) draft.Status = request.Status.Value;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(BugDraftDto.From(draft));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
