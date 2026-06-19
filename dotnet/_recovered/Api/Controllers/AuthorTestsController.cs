using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/author-tests")]
public class AuthorTestsController : ControllerBase
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAzureDevOpsClient _ado;
    private readonly AppDbContext _db;

    public AuthorTestsController(IAgentOrchestrator orchestrator, IAzureDevOpsClient ado, AppDbContext db)
    {
        _orchestrator = orchestrator; _ado = ado; _db = db;
    }

    // ── Work item search ───────────────────────────────────────────────────────

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
        => Ok(await _ado.SearchWorkItemsAsync(q, 20, ct));

    // ── Test plans + suites ────────────────────────────────────────────────────

    [HttpGet("plans")]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
        => Ok(await _ado.GetTestPlansAsync(ct));

    [HttpGet("plans/{planId:int}/suites")]
    public async Task<IActionResult> GetSuites(int planId, CancellationToken ct)
        => Ok(await _ado.GetTestSuitesAsync(planId, ct));

    // ── Pipeline ───────────────────────────────────────────────────────────────

    public sealed record StartRequest(int WorkItemId, bool AutoHandoff = true);

    /// <summary>POST /api/author-tests/{projectId}/start — kick off Analyst (+ Tester if auto).</summary>
    [HttpPost("{projectId:int}/start")]
    public async Task<IActionResult> Start(int projectId, [FromBody] StartRequest req, CancellationToken ct)
    {
        var sessionId = await _orchestrator.StartAsync(projectId, req.WorkItemId, req.AutoHandoff, ct);
        return Ok(new { sessionId });
    }

    /// <summary>POST /api/author-tests/sessions/{id}/run-tester — manual Tester trigger.</summary>
    [HttpPost("sessions/{sessionId:int}/run-tester")]
    public async Task<IActionResult> RunTester(int sessionId, CancellationToken ct)
    {
        await _orchestrator.RunTesterAsync(sessionId, ct);
        return Ok();
    }

    /// <summary>POST /api/author-tests/sessions/{id}/cancel — stop an in-flight pipeline.</summary>
    [HttpPost("sessions/{sessionId:int}/cancel")]
    public IActionResult Cancel(int sessionId)
    {
        var ok = _orchestrator.Cancel(sessionId);
        return Ok(new { cancelled = ok });
    }

    // ── Session queries ────────────────────────────────────────────────────────

    [HttpGet("sessions/{projectId:int}")]
    public async Task<IActionResult> ListSessions(int projectId, CancellationToken ct)
    {
        var sessions = await _db.TestSessions
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.WorkItemId, s.WorkItemTitle, s.Status, s.CreatedAt, s.ErrorMessage })
            .ToListAsync(ct);
        return Ok(sessions);
    }

    [HttpGet("sessions/detail/{sessionId:int}")]
    public async Task<IActionResult> GetSession(int sessionId, CancellationToken ct)
    {
        var session = await _db.TestSessions
            .Include(s => s.Drafts)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return NotFound();
        return Ok(session);
    }

    // ── Draft editing ──────────────────────────────────────────────────────────

    public sealed record UpdateDraftRequest(
        string? Title, string? Preconditions, string? StepsJson, DraftStatus? Status);

    [HttpPatch("drafts/{draftId:int}")]
    public async Task<IActionResult> UpdateDraft(
        int draftId, [FromBody] UpdateDraftRequest req, CancellationToken ct)
    {
        var draft = await _db.TestCaseDrafts.FindAsync([draftId], ct);
        if (draft is null) return NotFound();

        if (req.Title is not null) draft.Title = req.Title;
        if (req.Preconditions is not null) draft.Preconditions = req.Preconditions;
        if (req.StepsJson is not null) draft.StepsJson = req.StepsJson;
        if (req.Status is not null) draft.Status = req.Status.Value;

        await _db.SaveChangesAsync(ct);
        return Ok(draft);
    }

    /// <summary>Approve all Drafted items in a session at once.</summary>
    [HttpPost("sessions/{sessionId:int}/approve-all")]
    public async Task<IActionResult> ApproveAll(int sessionId, CancellationToken ct)
    {
        var drafts = await _db.TestCaseDrafts
            .Where(d => d.SessionId == sessionId && d.Status == DraftStatus.Draft)
            .ToListAsync(ct);
        foreach (var d in drafts) d.Status = DraftStatus.Approved;
        await _db.SaveChangesAsync(ct);
        return Ok(new { approvedCount = drafts.Count });
    }

    // ── Push to ADO ────────────────────────────────────────────────────────────

    public sealed record PushRequest(int TestPlanId, int TestSuiteId);

    [HttpPost("sessions/{sessionId:int}/push")]
    public IActionResult Push(int sessionId, [FromBody] PushRequest req)
    {
        // Fire and forget — orchestrator updates session.Status / CurrentActivity / ErrorMessage.
        // UI polls /sessions/detail/{id} and can hit /cancel mid-flight.
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.PushToAdoAsync(sessionId, req.TestPlanId, req.TestSuiteId);
            }
            catch { /* orchestrator records error/status; swallow so background task doesn't crash */ }
        });
        return Accepted(new { started = true });
    }
}
