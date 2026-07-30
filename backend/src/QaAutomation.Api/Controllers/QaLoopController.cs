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
    private readonly IFrontendModuleService _frontendModules;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOptions;
    private readonly ILogger<QaLoopController> _log;

    public QaLoopController(
        IQaLoopOrchestrator orchestrator, IEventBus bus, QaDbContext db, IAzureDevOpsClient ado,
        IFrontendModuleService frontendModules, IOptions<QaLoopOptions> opts,
        IOptionsMonitor<AzureDevOpsOptions> adoOptions, ILogger<QaLoopController> log)
    {
        _orchestrator = orchestrator;
        _bus = bus;
        _db = db;
        _ado = ado;
        _frontendModules = frontendModules;
        _opts = opts;
        _adoOptions = adoOptions;
        _log = log;
    }

    /// <summary>Product frontend modules for the New Scan module dropdown. Tries the conventional route
    /// file (deterministic parser) then falls back to the active LLM to locate + extract the route map;
    /// the result is cached (one-time) per repo/branch. Pass <c>refresh=true</c> to rediscover.</summary>
    [HttpGet("frontend-modules")]
    public async Task<IActionResult> FrontendModules(
        [FromQuery] string? repository, [FromQuery] string? branch, [FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        try
        {
            var modules = await _frontendModules.DiscoverAsync(repository, branch, refresh, ct);
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

        var adoDefaults = _adoOptions.CurrentValue;
        var session = new ApiTestSession
        {
            ProjectId = projectId,
            Module = req.Module,
            // A scan-level selection wins, otherwise use the runtime ADO Settings values. This keeps
            // code-aware scans working when repository/branch are intentionally omitted from the form.
            Repository = string.IsNullOrWhiteSpace(req.Repository) ? adoDefaults.Repository : req.Repository,
            Branch = string.IsNullOrWhiteSpace(req.Branch) ? adoDefaults.DefaultBranch : req.Branch,
            WorkItemId = req.WorkItemId,
            TestPlanId = req.TestPlanId,
            TestSuiteId = req.TestSuiteId,
            SourceDirsJson = req.SourceDirs is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(req.SourceDirs) : null,
            FrontendModule = req.FrontendModule,
            FrontendPath = req.FrontendPath,
            HasUiPhase = req.HasUiPhase,
            AutoHandoff = req.AutoHandoff,
            ActivityEnabled = _opts.Value.ActivityEnabled,
            AdditionalContext = req.AdditionalContext,
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
    public async Task<IActionResult> GenerateDrafts(int sessionId, [FromBody] GenerateDraftsRequest? req, CancellationToken ct)
    {
        try
        {
            if (req?.GenerateContext is { Length: > 0 })
            {
                var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
                if (session is null) return StatusCode(404, new { detail = "Session not found." });
                session.GenerateContext = req.GenerateContext;
                session.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            await _orchestrator.GenerateDraftsAsync(sessionId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/rerun-analyze")]
    public async Task<IActionResult> RerunAnalyze(int sessionId, [FromBody] RerunAnalyzeRequest req, CancellationToken ct)
    {
        try
        {
            await _orchestrator.RerunAnalyzeAsync(sessionId, req.Feedback, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/refine-drafts")]
    public async Task<IActionResult> RefineDrafts(int sessionId, [FromBody] RefineDraftsRequest req, CancellationToken ct)
    {
        try
        {
            await _orchestrator.RefineDraftsAsync(sessionId, req.OverallFeedback, req.PerDraft, ct);
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

    [HttpPost("sessions/{sessionId:int}/author-ui-cases")]
    public async Task<IActionResult> AuthorUiCases(int sessionId, CancellationToken ct)
    {
        try
        {
            await _orchestrator.AuthorUiCasesAsync(sessionId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/generate-ui-scripts")]
    public async Task<IActionResult> GenerateUiScripts(int sessionId, CancellationToken ct)
    {
        try
        {
            await _orchestrator.GenerateUiScriptsAsync(sessionId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/heal-specs")]
    public async Task<IActionResult> HealSpecs(
        int sessionId, [FromBody] HealSpecsRequest req, CancellationToken ct)
    {
        if (req.DraftIds is not { Length: > 0 })
            return StatusCode(400, new { detail = "draftIds must be a non-empty array." });
        try
        {
            await _orchestrator.HealSpecsAsync(sessionId, req.DraftIds, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/cancel")]
    public IActionResult Cancel(int sessionId)
        => Ok(new CancelResponse { Cancelled = _orchestrator.Cancel(sessionId) });

    [HttpDelete("sessions/{sessionId:int}")]
    public async Task<IActionResult> DeleteSession(int sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            _orchestrator.Cancel(sessionId);
            var drafts = await _db.ApiTestDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            _db.ApiTestDrafts.RemoveRange(drafts);
            _db.ApiTestSessions.Remove(session);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

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
