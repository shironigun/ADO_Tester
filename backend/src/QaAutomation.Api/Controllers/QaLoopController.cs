using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QaAutomation.Api.Streaming;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/qaloop")]
public sealed class QaLoopController : ControllerBase
{
    // Terminal statuses — sessions in these states do not block a new scan start.
    private static readonly ApiSessionStatus[] TerminalStatuses =
        [ApiSessionStatus.Done, ApiSessionStatus.Failed, ApiSessionStatus.Cancelled];

    private readonly IQaLoopOrchestrator _orchestrator;
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly IAzureDevOpsClient _ado;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly ILogger<QaLoopController> _log;

    public QaLoopController(
        IQaLoopOrchestrator orchestrator, IEventBus bus, QaDbContext db, IAzureDevOpsClient ado,
        IOptions<QaLoopOptions> opts, ILogger<QaLoopController> log)
    {
        _orchestrator = orchestrator;
        _bus = bus;
        _db = db;
        _ado = ado;
        _opts = opts;
        _log = log;
    }

    /// <summary>Product frontend modules parsed from the SPA route file — the strict source for the
    /// New Scan module dropdown. Reuses GetFileContentAsync + the pure FrontendModuleParser.</summary>
    [HttpGet("frontend-modules")]
    public async Task<IActionResult> FrontendModules(
        [FromQuery] string? repository, [FromQuery] string? branch, CancellationToken ct)
    {
        var opts = _opts.Value;
        var repo = string.IsNullOrWhiteSpace(repository) ? opts.FrontendRepo : repository;
        var root = opts.FrontendRoot.Trim('/');
        try
        {
            var content = await _ado.GetFileContentAsync(
                $"{root}/routes/Routes.tsx", @ref: branch, repoId: repo, ct: ct);
            var modules = FrontendModuleParser.Parse(content, root, opts.FrontendBaseUrl);
            return Ok(modules);
        }
        catch (Exception e)
        {
            _log.LogWarning("Frontend module discovery failed: {Error}", e.Message);
            return StatusCode(502, new { detail = e.Message });
        }
    }

    // ── Session CRUD ──────────────────────────────────────────────────────────

    [HttpPost("{projectId:int}/scan/start")]
    public async Task<IActionResult> ScanStart(
        int projectId, [FromBody] ScanStartRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Module))
            return StatusCode(400, new { detail = "module is required." });

        int? existingSessionId = null;
        var existing = await _db.ApiTestSessions
            .Where(s => s.ProjectId == projectId
                        && s.Module == req.Module
                        && !TerminalStatuses.Contains(s.Status))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            existingSessionId = existing.Id;

        var session = new ApiTestSession
        {
            ProjectId = projectId,
            Module = req.Module,
            Repository = req.Repository,
            Branch = req.Branch,
            WorkItemId = req.WorkItemId,
            TestPlanId = req.TestPlanId,
            TestSuiteId = req.TestSuiteId,
            SourceDirsJson = req.SourceDirs is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(req.SourceDirs) : null,
            FrontendModule = req.FrontendModule,
            FrontendPath = req.FrontendPath,
            AutoHandoff = req.AutoHandoff,
            ActivityEnabled = _opts.Value.ActivityEnabled,
            Status = ApiSessionStatus.Analyzing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.ApiTestSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _orchestrator.StartAsync(session.Id, ct);
        }
        catch (Exception e)
        {
            _log.LogError(e, "QaLoop scan start failed for session {Id}", session.Id);
            return StatusCode(500, new { detail = e.Message });
        }

        return StatusCode(202, new ScanStartResponse { SessionId = session.Id, ExistingSessionId = existingSessionId });
    }

    [HttpGet("sessions/{sessionId:int}/stream")]
    public async Task Stream(int sessionId, [FromQuery] string? lastEventId, CancellationToken ct)
    {
        var afterSeq = SseWriter.ParseLastEventId(Request.Headers["Last-Event-ID"].FirstOrDefault(), lastEventId);
        await SseWriter.StreamAsync(Response, _bus, $"qa-session:{sessionId}", afterSeq, ct);
    }

    [HttpPatch("sessions/{sessionId:int}/analysis")]
    public async Task<IActionResult> UpdateAnalysis(
        int sessionId, [FromBody] ApiAnalysisUpdate req, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            session.AnalysisJson = System.Text.Json.JsonSerializer.Serialize(req.ToMatrix());
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            var drafts = await _db.ApiTestDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            return Ok(ApiSessionDetail.From(session, drafts));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/generate-drafts")]
    public async Task<IActionResult> GenerateDrafts(int sessionId, CancellationToken ct)
    {
        try
        {
            await _orchestrator.GenerateDraftsAsync(sessionId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPatch("drafts/{draftId:int}")]
    public async Task<IActionResult> UpdateDraft(
        int draftId, [FromBody] UpdateApiDraftRequest req, CancellationToken ct)
    {
        try
        {
            var draft = await _db.ApiTestDrafts.FindAsync([draftId], ct);
            if (draft is null) return StatusCode(404, new { detail = "Draft not found." });
            if (req.Status is not null) draft.Status = req.Status.Value;
            if (req.Title is not null) draft.Title = req.Title;
            if (req.Preconditions is not null) draft.Preconditions = req.Preconditions;
            if (req.StepsJson is not null) draft.StepsJson = req.StepsJson;
            if (req.SpecCode is not null) draft.SpecCode = req.SpecCode;
            draft.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(ApiTestDraftDto.From(draft));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/approve-all")]
    public async Task<IActionResult> ApproveAll(int sessionId, CancellationToken ct)
    {
        try
        {
            var drafts = await _db.ApiTestDrafts
                .Where(d => d.SessionId == sessionId && d.Status == ApiDraftStatus.Draft)
                .ToListAsync(ct);
            foreach (var d in drafts) d.Status = ApiDraftStatus.Approved;
            await _db.SaveChangesAsync(ct);
            return Ok(new ApproveAllResponse { ApprovedCount = drafts.Count });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPatch("sessions/{sessionId:int}/plan")]
    public async Task<IActionResult> UpdateSessionPlan(
        int sessionId, [FromBody] UpdateSessionPlanRequest req, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (req.TestPlanId is not null) session.TestPlanId = req.TestPlanId;
            if (req.TestSuiteId is not null) session.TestSuiteId = req.TestSuiteId;
            if (req.WorkItemId is not null) session.WorkItemId = req.WorkItemId;
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            var drafts = await _db.ApiTestDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            return Ok(ApiSessionDetail.From(session, drafts));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/push")]
    public async Task<IActionResult> PushAll(int sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (!session.TestPlanId.HasValue || !session.TestSuiteId.HasValue)
                return StatusCode(400, new { detail = "Session has no TestPlanId/TestSuiteId. Set them before pushing." });
            await _orchestrator.PushAllAsync(
                sessionId, session.TestPlanId.Value, session.TestSuiteId.Value, session.WorkItemId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("drafts/{draftId:int}/push")]
    public async Task<IActionResult> PushDraft(int draftId, CancellationToken ct)
    {
        try
        {
            var draft = await _db.ApiTestDrafts.FindAsync([draftId], ct);
            if (draft is null) return StatusCode(404, new { detail = "Draft not found." });
            var session = await _db.ApiTestSessions.FindAsync([draft.SessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (!session.TestPlanId.HasValue || !session.TestSuiteId.HasValue)
                return StatusCode(400, new { detail = "Session has no TestPlanId/TestSuiteId." });
            var pushed = await _orchestrator.PushDraftAsync(
                draftId, session.TestPlanId.Value, session.TestSuiteId.Value,
                session.WorkItemId, ct);
            return Ok(ApiTestDraftDto.From(pushed));
        }
        catch (AdoApiError e) { return StatusCode(502, new { detail = e.Message }); }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/cancel")]
    public IActionResult Cancel(int sessionId)
        => Ok(new CancelResponse { Cancelled = _orchestrator.Cancel(sessionId) });

    // ── Session list/detail ───────────────────────────────────────────────────

    [HttpGet("sessions/{projectId:int}")]
    public async Task<IActionResult> ListSessions(
        int projectId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        take = Math.Max(1, Math.Min(take, 100));
        skip = Math.Max(0, skip);
        try
        {
            var q = _db.ApiTestSessions.Where(s => s.ProjectId == projectId);
            var total = await q.CountAsync(ct);
            var sessions = await q.OrderByDescending(s => s.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
            var sessionIds = sessions.Select(s => s.Id).ToList();
            var draftCounts = await _db.ApiTestDrafts
                .Where(d => sessionIds.Contains(d.SessionId))
                .GroupBy(d => d.SessionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            return Ok(new ApiSessionList
            {
                Total = total,
                Items = sessions.Select(s => ApiSessionSummary.From(s, draftCounts.GetValueOrDefault(s.Id))).ToList(),
            });
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpGet("sessions/detail/{sessionId:int}")]
    public async Task<IActionResult> Detail(int sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            var drafts = await _db.ApiTestDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            return Ok(ApiSessionDetail.From(session, drafts));
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }
}
