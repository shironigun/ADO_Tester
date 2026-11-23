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
/// Author-Tests endpoints (spec B9) — flat under <c>/api/author-tests</c>. SSE live console, ADO
/// search/plans/suites, session lifecycle (start/run-tester/cancel/push), session list+detail, and
/// the analysis/draft PATCH + approve-all editing surface. Frozen: 200 on start/cancel/approve-all,
/// 202 on run-tester/push; {total,items} paging; {detail} errors (ADO→502, DB/orchestrator→500).
/// </summary>
[ApiController]
[Route("api/author-tests")]
public sealed class AuthorTestsController : ControllerBase
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAzureDevOpsClient _ado;
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly IExistingTestsIndexService _dedupIndex;
    private readonly ILogger<AuthorTestsController> _log;

    public AuthorTestsController(
        IAgentOrchestrator orchestrator, IAzureDevOpsClient ado, IEventBus bus, QaDbContext db,
        IExistingTestsIndexService dedupIndex, ILogger<AuthorTestsController> log)
    {
        _orchestrator = orchestrator;
        _ado = ado;
        _bus = bus;
        _db = db;
        _dedupIndex = dedupIndex;
        _log = log;
    }

    [HttpGet("sessions/{sessionId:int}/stream")]
    public async Task Stream(int sessionId, [FromQuery] string? lastEventId, CancellationToken cancellationToken)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"session:{sessionId}", afterSeq, cancellationToken);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<AdoWorkItem>());
        try
        {
            return Ok(await _ado.SearchWorkItemsAsync(q, 20, cancellationToken));
        }
        catch (Exception e)
        {
            return StatusCode(502, new { detail = e.Message });
        }
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken cancellationToken)
    {
        try { return Ok(await _ado.GetTestPlansAsync(cancellationToken)); }
        catch (Exception e) { return StatusCode(502, new { detail = e.Message }); }
    }

    [HttpGet("plans/{planId:int}/suites")]
    public async Task<IActionResult> Suites(int planId, CancellationToken cancellationToken)
    {
        try { return Ok(await _ado.GetTestSuitesAsync(planId, cancellationToken)); }
        catch (Exception e) { return StatusCode(502, new { detail = e.Message }); }
    }

    /// <summary>Force-rescan a reference test plan (+ optional suite scope) and rebuild the dedup index.</summary>
    [HttpPost("plans/{planId:int}/rescan")]
    public async Task<IActionResult> RescanPlan(int planId, [FromQuery] int? suiteId, CancellationToken cancellationToken)
    {
        try
        {
            var scan = await _dedupIndex.EnsureIndexAsync(planId, suiteId, forceRescan: true, cancellationToken);
            return Ok(new RescanPlanResponse { ScanId = scan.Id, CaseCount = scan.CaseCount, ScannedAt = scan.ScannedAt });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Rescan failed for plan {PlanId}", planId);
            return StatusCode(500, new { detail = e.Message });
        }
    }

    /// <summary>Return the current scan status for a reference plan (null fields = never scanned).</summary>
    [HttpGet("plans/{planId:int}/scan-status")]
    public async Task<IActionResult> ScanStatus(int planId, [FromQuery] int? suiteId, CancellationToken cancellationToken)
    {
        try
        {
            var scan = await _dedupIndex.GetScanStatusAsync(planId, suiteId, cancellationToken);
            return Ok(new ScanStatusResponse
            {
                ScanId = scan?.Id,
                CaseCount = scan?.CaseCount,
                ScannedAt = scan?.ScannedAt,
            });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("{projectId:int}/start")]
    public async Task<IActionResult> Start(int projectId, [FromBody] StartRequest request, CancellationToken cancellationToken)
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
            var context = new AuthorRunContext(
                request.AdditionalContext, request.ReferenceTestPlanId, request.ReferenceTestSuiteId, request.PlanNavigationHint);
            var sessionId = await _orchestrator.StartAsync(
                projectId, request.WorkItemId, request.AutoHandoff, context, cancellationToken: cancellationToken);
            return Ok(new StartResponse { SessionId = sessionId });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Author-Tests start failed");
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("sessions/{sessionId:int}/run-tester")]
    public async Task<IActionResult> RunTester(int sessionId, [FromBody] RunTesterRequest? request, CancellationToken cancellationToken)
    {
        // Empty body → null request → pre-enhancement behavior (no extra context/reference plan).
        var context = request is null ? null : new AuthorRunContext(
            request.AdditionalContext, request.ReferenceTestPlanId, request.ReferenceTestSuiteId, request.PlanNavigationHint);
        await _orchestrator.RunTesterAsync(sessionId, context, cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpPost("sessions/{sessionId:int}/rerun-analyst")]
    public async Task<IActionResult> RerunAnalyst(int sessionId, [FromBody] RerunAnalystRequest request, CancellationToken cancellationToken)
    {
        await _orchestrator.RerunAnalystAsync(sessionId, request?.Feedback ?? "", cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpPost("sessions/{sessionId:int}/refine-tester")]
    public async Task<IActionResult> RefineTester(int sessionId, [FromBody] RefineTesterRequest request, CancellationToken cancellationToken)
    {
        await _orchestrator.RefineTesterAsync(sessionId, request?.OverallFeedback, request?.PerTest ?? [], cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpPost("sessions/{sessionId:int}/cancel")]
    public IActionResult Cancel(int sessionId)
        => Ok(new CancelResponse { Cancelled = _orchestrator.Cancel(sessionId) });

    [HttpGet("sessions/{projectId:int}")]
    public async Task<IActionResult> ListSessions(int projectId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);
        try
        {
            // Standalone sessions only — epic-run children (BatchId set) live under the Epic runs tab.
            var total = await _db.TestSessions.CountAsync(s => s.ProjectId == projectId && s.BatchId == null, cancellationToken);
            var items = await _db.TestSessions
                .Where(s => s.ProjectId == projectId && s.BatchId == null)
                .OrderByDescending(s => s.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync(cancellationToken);
            return Ok(new TestSessionList { Total = total, Items = items.Select(TestSessionSummary.From).ToList() });
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
            var session = await _db.TestSessions.Include(s => s.Drafts).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null)
                return StatusCode(404, new { detail = "Session not found." });
            return Ok(TestSessionDetail.From(session, session.Drafts));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPatch("sessions/{sessionId:int}/analysis")]
    public async Task<IActionResult> UpdateAnalysis(int sessionId, [FromBody] TestAnalysisUpdate request, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.TestSessions.FindAsync([sessionId], cancellationToken);
            if (session is null)
                return StatusCode(404, new { detail = "Session not found." });
            session.AnalysisJson = request.ToAnalystOutput().ToJson();   // stored as camelCase AnalystOutput JSON
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var drafts = await _db.TestCaseDrafts.Where(d => d.SessionId == sessionId).ToListAsync(cancellationToken);
            return Ok(TestSessionDetail.From(session, drafts));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPut("sessions/{sessionId:int}/code-behavior")]
    public async Task<IActionResult> UpdateCodeBehavior(int sessionId, [FromBody] UpdateCodeBehaviorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.TestSessions.FindAsync([sessionId], cancellationToken);
            if (session is null)
                return StatusCode(404, new { detail = "Session not found." });
            if (session.Status != SessionStatus.AwaitingTester)
                return StatusCode(409, new { detail = "Code-behavior notes can only be edited at the Analyst→Tester gate." });

            var arr = new JsonArray();
            var idx = 1;
            foreach (var it in request.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(it.Behavior)) continue;
                arr.Add(new JsonObject
                {
                    ["id"] = string.IsNullOrWhiteSpace(it.Id) ? $"b{idx}" : it.Id,
                    ["behavior"] = it.Behavior.Trim(),
                    ["comment"] = (it.Comment ?? "").Trim(),
                });
                idx++;
            }
            session.CodeBehaviorJson = arr.Count > 0 ? arr.ToJsonString() : null;
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            var drafts = await _db.TestCaseDrafts.Where(d => d.SessionId == sessionId).ToListAsync(cancellationToken);
            return Ok(TestSessionDetail.From(session, drafts));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPatch("drafts/{draftId:int}")]
    public async Task<IActionResult> UpdateDraft(int draftId, [FromBody] UpdateDraftRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await _db.TestCaseDrafts.FindAsync([draftId], cancellationToken);
            if (draft is null)
                return StatusCode(404, new { detail = "Draft not found." });
            if (request.Title is not null) draft.Title = request.Title;             // skip-null per field
            if (request.Preconditions is not null) draft.Preconditions = request.Preconditions;
            if (request.StepsJson is not null) draft.StepsJson = request.StepsJson;
            if (request.Status is not null) draft.Status = request.Status.Value;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(TestCaseDraftDto.From(draft));
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("sessions/{sessionId:int}/approve-all")]
    public async Task<IActionResult> ApproveAll(int sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var drafts = await _db.TestCaseDrafts
                .Where(d => d.SessionId == sessionId && d.Status == DraftStatus.Draft)
                .ToListAsync(cancellationToken);
            foreach (var d in drafts)
                d.Status = DraftStatus.Approved;
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(new ApproveAllResponse { ApprovedCount = drafts.Count });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }

    [HttpPost("sessions/{sessionId:int}/push")]
    public async Task<IActionResult> Push(int sessionId, [FromBody] PushRequest request, CancellationToken cancellationToken)
    {
        await _orchestrator.PushToAdoAsync(sessionId, request.TestPlanId, request.TestSuiteId, cancellationToken);
        return StatusCode(202, new AcceptedResponse { Started = true });
    }

    [HttpPost("drafts/{draftId:int}/push")]
    public async Task<IActionResult> PushDraft(int draftId, [FromBody] PushRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await _orchestrator.PushDraftAsync(draftId, request.TestPlanId, request.TestSuiteId, cancellationToken);
            return Ok(TestCaseDraftDto.From(draft));
        }
        catch (AdoApiError e)
        {
            return StatusCode(502, new { detail = e.Message });
        }
        catch (InvalidOperationException e)
        {
            return StatusCode(404, new { detail = e.Message });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { detail = e.Message });
        }
    }
}
