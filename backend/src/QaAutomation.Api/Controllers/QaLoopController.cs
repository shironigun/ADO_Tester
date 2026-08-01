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
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Api.Controllers;

[ApiController]
[Route("api/qaloop")]
public sealed class QaLoopController : ControllerBase
{
    // Terminal statuses — sessions in these states do not block a new scan start.
    // Note: ApiDone is NOT terminal (UI phase still running); Done/Failed/Cancelled are.
    private static readonly ApiSessionStatus[] TerminalStatuses =
        [ApiSessionStatus.Done, ApiSessionStatus.Failed, ApiSessionStatus.Cancelled];

    private readonly IQaLoopOrchestrator _orchestrator;
    private readonly IEventBus _bus;
    private readonly QaDbContext _db;
    private readonly IAzureDevOpsClient _ado;
    private readonly IFrontendModuleService _frontendModules;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IOptions<QaLoopOptions> _opts;
    private readonly IOptions<StorageOptions> _storage;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _adoOptions;
    private readonly ILogger<QaLoopController> _log;

    public QaLoopController(
        IQaLoopOrchestrator orchestrator, IEventBus bus, QaDbContext db, IAzureDevOpsClient ado,
        IFrontendModuleService frontendModules, ILlmClientFactory llmFactory,
        IOptions<QaLoopOptions> opts, IOptions<StorageOptions> storage,
        IOptionsMonitor<AzureDevOpsOptions> adoOptions, ILogger<QaLoopController> log)
    {
        _orchestrator = orchestrator;
        _bus = bus;
        _db = db;
        _ado = ado;
        _frontendModules = frontendModules;
        _llmFactory = llmFactory;
        _opts = opts;
        _storage = storage;
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

    [HttpPost("sessions/{sessionId:int}/rerun-ui-analysis")]
    public async Task<IActionResult> RerunUiAnalysis(
        int sessionId, [FromBody] RerunUiAnalysisRequest req, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (session.UiStatus != ApiUiStatus.AwaitingUiAnalysisReview)
                return StatusCode(409, new { detail = $"UiStatus must be AwaitingUiAnalysisReview (was {session.UiStatus})." });
            await _orchestrator.RerunUiAnalysisAsync(sessionId, req.Feedback ?? "", ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/author-ui-cases")]
    public async Task<IActionResult> AuthorUiCases(int sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            // KBG-111: null = old Slab 2 session (backward-compat); AwaitingUiAnalysisReview = new flow.
            if (session.UiStatus is not null && session.UiStatus != ApiUiStatus.AwaitingUiAnalysisReview)
                return StatusCode(409, new { detail = $"UiStatus must be null or AwaitingUiAnalysisReview (was {session.UiStatus})." });
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
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (session.UiStatus != ApiUiStatus.AwaitingReview)
                return StatusCode(409, new { detail = $"UiStatus must be AwaitingReview (was {session.UiStatus})." });
            await _orchestrator.GenerateUiScriptsAsync(sessionId, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/run-ui-scripts")]
    public async Task<IActionResult> RunUiScripts(int sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            if (string.IsNullOrWhiteSpace(_opts.Value.PlaywrightRoot))
                return StatusCode(400, new { detail = "PlaywrightRoot is not configured. Set AUTOMATION_PLAYWRIGHT_ROOT before running UI scripts." });
            if (session.UiStatus == ApiUiStatus.RunningUiScripts)
                return StatusCode(409, new { detail = "UI scripts are already running." });
            var approvedCount = await _db.ApiTestDrafts.CountAsync(d =>
                d.SessionId == sessionId
                && d.DraftKind == ApiDraftKind.UiScript
                && d.Status == ApiDraftStatus.Approved
                && d.SpecFilePath != null && d.SpecFilePath != "", ct);
            if (approvedCount == 0)
                return StatusCode(400, new { detail = "No approved UiScript drafts with SpecFilePath to run." });
            await _orchestrator.RunUiScriptsAsync(sessionId, ct);
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
            await _orchestrator.HealSpecsAsync(sessionId, req.DraftIds, req.UserContext, ct);
            return StatusCode(202, new AcceptedResponse { Started = true });
        }
        catch (InvalidOperationException e) { return StatusCode(404, new { detail = e.Message }); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPost("sessions/{sessionId:int}/explain-failure")]
    public async Task<IActionResult> ExplainFailure(
        int sessionId, [FromBody] ExplainFailureRequest req, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var linked = timeoutCts.Token;

            var draft = await _db.ApiTestDrafts.FindAsync([req.DraftId], linked);
            if (draft is null || draft.SessionId != sessionId)
                return StatusCode(404, new { detail = "Draft not found." });
            if (draft.DraftKind != ApiDraftKind.UiScript)
                return StatusCode(400, new { detail = "Draft must be a UiScript." });
            if (string.IsNullOrWhiteSpace(draft.ErrorMessage))
                return StatusCode(400, new { detail = "Draft has no ErrorMessage to explain." });

            var session = await _db.ApiTestSessions.FindAsync([sessionId], linked);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });

            string? screenshotDir = null;
            if (session.LastUiRunId is int runId)
            {
                var candidate = Path.Combine(
                    _storage.Value.DataDirectory,
                    _opts.Value.RunsFolderName,
                    runId.ToString());
                if (Directory.Exists(candidate))
                    screenshotDir = candidate;
            }

            var llm = await _llmFactory.CreateAsync(linked);
            var explanation = await new QaLoopFailureAnalystAgent(llm)
                .ExplainAsync(draft.ErrorMessage!, draft.Title, draft.StepsJson, screenshotDir, linked);

            return Ok(new ExplainFailureResponse(draft.Id, explanation));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(504, new { detail = "Explain-failure timed out after 30s." });
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

    // ── KBG-108: frs-modules + ui-suite ─────────────────────────────────────────

    [HttpGet("{projectId:int}/frs-modules")]
    public async Task<IActionResult> GetFrsModules(
        int projectId, [FromServices] IFrsKbService frsKb, CancellationToken ct)
    {
        try { return Ok(await frsKb.ListSlugsAsync(projectId, ct)); }
        catch (Exception e) { return StatusCode(500, new { detail = e.Message }); }
    }

    [HttpPatch("sessions/{sessionId:int}/ui-suite")]
    public async Task<IActionResult> PatchUiSuite(
        int sessionId, [FromBody] PatchUiSuiteRequest req, CancellationToken ct)
    {
        try
        {
            var session = await _db.ApiTestSessions.FindAsync([sessionId], ct);
            if (session is null) return StatusCode(404, new { detail = "Session not found." });
            session.UiTestSuiteId = req.SuiteId;
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok();
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