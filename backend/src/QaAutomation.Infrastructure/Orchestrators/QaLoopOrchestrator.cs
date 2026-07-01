using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// QA Loop pipeline (scan → analyze → generate → push). Singleton.
/// <para><see cref="StartAsync"/> receives a pre-seeded <see cref="ApiTestSession"/> id and launches
/// the background analyze pipeline on its own DI scope (never the request scope). Live progress is
/// pumped to <see cref="IEventBus"/>; the durable narrative is persisted to EventLogJson.
/// Cancellation flows through the <see cref="CancellationRegistry"/>.</para>
/// </summary>
public sealed class QaLoopOrchestrator : IQaLoopOrchestrator
{
    private static readonly IReadOnlySet<ApiSessionStatus> TerminalStatuses =
        new HashSet<ApiSessionStatus>
        {
            ApiSessionStatus.Done, ApiSessionStatus.Drafted,
            ApiSessionStatus.Failed, ApiSessionStatus.Cancelled,
        };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly PipelineConcurrency _concurrency;
    private readonly CancellationRegistry _cancellations;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly string _scriptsDir;
    private readonly ILogger<QaLoopOrchestrator> _log;

    public QaLoopOrchestrator(
        IServiceScopeFactory scopeFactory, IEventBus bus, PipelineConcurrency concurrency,
        CancellationRegistry cancellations, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop,
        ILogger<QaLoopOrchestrator> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _concurrency = concurrency;
        _cancellations = cancellations;
        _ado = ado;
        _llmFactory = llmFactory;
        _scriptsDir = Path.GetFullPath(Path.Combine(
            storage.Value.DataDirectory, qaLoop.Value.ScriptsFolderName));
        _log = log;
    }

    public bool Cancel(int sessionId) => _cancellations.Cancel(sessionId);

    // ── QAL-021: Start / Launch / queue-gate ────────────────────────────────

    public Task<int> StartAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RunPipelineAsync(sessionId, ct));
        return Task.FromResult(sessionId);
    }

    // ── QAL-022: GenerateDrafts ──────────────────────────────────────────────

    public Task GenerateDraftsAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RunGenerateAsync(sessionId, ct));
        return Task.CompletedTask;
    }

    // ── QAL-023: Push / PushDraft ────────────────────────────────────────────

    public Task PushAllAsync(int sessionId, int testPlanId, int testSuiteId, int? storyId = null,
        CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RunPushAsync(sessionId, testPlanId, testSuiteId, storyId, ct));
        return Task.CompletedTask;
    }

    /// <summary>Synchronous per-row push. Idempotent: re-push returns if already Pushed.</summary>
    public async Task<ApiTestDraft> PushDraftAsync(int draftId, int testPlanId, int testSuiteId,
        int? storyId = null, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var draft = await db.ApiTestDrafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken)
            ?? throw new InvalidOperationException($"Draft {draftId} not found.");
        if (draft.Status == ApiDraftStatus.Pushed && draft.AdoTestCaseId is not null)
            return draft;

        var session = await db.ApiTestSessions.FindAsync([draft.SessionId], cancellationToken)
            ?? throw new InvalidOperationException($"Session {draft.SessionId} not found.");

        var steps = ParseSteps(draft.StepsJson);
        var tcId = await _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, cancellationToken);
        await _ado.AddTestCaseToSuiteAsync(testPlanId, testSuiteId, tcId, cancellationToken);

        var workItemId = storyId ?? session.WorkItemId;
        if (workItemId.HasValue)
            await _ado.LinkTestedByAsync(workItemId.Value, tcId, cancellationToken);

        draft.AdoTestCaseId = tcId;
        draft.Status = ApiDraftStatus.Pushed;
        draft.Title = RewriteTag(draft.Title, tcId, draft.CaseIndex);
        draft.UpdatedAt = DateTime.UtcNow;
        RewriteSpecOnDisk(draft, tcId);

        var remaining = await db.ApiTestDrafts.CountAsync(
            d => d.SessionId == session.Id && d.Id != draft.Id
                 && (d.Status == ApiDraftStatus.Draft || d.Status == ApiDraftStatus.Approved),
            cancellationToken);
        if (remaining == 0)
        {
            session.Status = ApiSessionStatus.Done;
        }
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    // ── Launch / queue gate ──────────────────────────────────────────────────

    private void Launch(int sessionId, Func<CancellationToken, Task> body)
    {
        var token = _cancellations.Register(sessionId).Token;
        _ = Task.Run(async () =>
        {
            try { await body(token); }
            finally { _cancellations.Remove(sessionId); }
        });
    }

    private async Task RunPipelineAsync(int sessionId, CancellationToken ct)
    {
        try
        {
            await MarkQueuedAsync(sessionId);
            await _concurrency.Gate.WaitAsync(ct);
            try { await RunPipelineInnerAsync(sessionId, ct); }
            finally { _concurrency.Gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(sessionId);
        }
    }

    // ── Analyze pipeline (QAL-021) ───────────────────────────────────────────

    private async Task RunPipelineInnerAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);
            Pub(key, EventKinds.System, text: $"QA Loop pipeline started · provider {llm.ProviderName}");

            // QAL-070: mirror AgentOrchestrator/BugOrchestrator — fetch the linked work item's title so
            // the session displays it. Best-effort: a failed fetch never fails the scan, leaves it null.
            if (session.WorkItemId.HasValue)
            {
                try
                {
                    var wi = await _ado.GetWorkItemAsync(session.WorkItemId.Value, ct: ct);
                    session.WorkItemTitle = wi.Title;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not fetch work item {Id} title", session.WorkItemId);
                }
            }

            // Fetch source from ADO repo
            Pub(key, EventKinds.Phase, EventPhases.Fetch, $"Fetching source for module '{session.Module}'");
            var source = await FetchSourceAsync(session, ct);
            Pub(key, EventKinds.Result, EventPhases.Fetch, $"Fetched source ({source.Length:N0} chars)", EventLevels.Success);

            // Existing test cases for reconciliation against the chosen suite
            IReadOnlyList<LinkedTestCase> existingCases = [];
            if (session.TestSuiteId.HasValue && session.TestPlanId.HasValue)
            {
                try
                {
                    existingCases = await _ado.GetSuiteTestCasesAsync(
                        session.TestPlanId.Value, session.TestSuiteId.Value, recurse: true, ct);
                    Pub(key, EventKinds.Result, EventPhases.Fetch,
                        $"Found {existingCases.Count} existing test case(s) in suite");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not load existing test cases for suite {Id}", session.TestSuiteId);
                }
            }

            // fetchFile delegate for the analyzer's one-shot refinement. Resolves each request as EITHER an
            // exact repo path OR (for frontend-driven scans) a search term - an endpoint route string or a
            // backend class name - via code search, so the analyzer can pull backend handlers it can't
            // path-guess from the frontend apis/. Resolved paths are recorded as the derived source dirs.
            var resolvedBackendPaths = new List<string>();
            Func<string, CancellationToken, Task<string?>>? fetchFile = null;
            if (!string.IsNullOrEmpty(session.Repository) && !string.IsNullOrEmpty(session.Branch))
            {
                var repo = session.Repository;
                var branch = session.Branch;
                fetchFile = async (pathOrTerm, fct) =>
                {
                    try
                    {
                        var direct = await _ado.GetFileContentAsync(pathOrTerm, @ref: branch, repoId: repo, ct: fct);
                        if (!string.IsNullOrEmpty(direct)) { resolvedBackendPaths.Add(pathOrTerm); return direct; }
                    }
                    catch { /* not a direct path — try search */ }
                    try
                    {
                        // ponytail: SearchCodeAsync searches the configured ado.repository (single product repo).
                        var hit = (await _ado.SearchCodeAsync(pathOrTerm, top: 1, ct: fct)).FirstOrDefault();
                        if (!string.IsNullOrEmpty(hit))
                        {
                            var content = await _ado.GetFileContentAsync(hit, @ref: branch, repoId: repo, ct: fct);
                            if (!string.IsNullOrEmpty(content)) { resolvedBackendPaths.Add(hit); return content; }
                        }
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "fetchFile search fallback failed for '{Term}'", pathOrTerm); }
                    return null;
                };
            }

            // Run the module analyzer
            Pub(key, EventKinds.Phase, EventPhases.Analyze, $"Module analyzer started for '{session.Module}'");
            session.Status = ApiSessionStatus.Analyzing;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var analysis = await new ModuleAnalyzerAgent(llm).AnalyzeInventoryAsync(
                session.Module, source, existingCases,
                fetchFile, emit: EmitClosure(key, EventPhases.Analyze, session.ActivityEnabled), ct: ct);

            // Pre-select: gap rows = selected; covered rows = not selected
            foreach (var row in analysis.Surface.Rows)
                row.Selected = row.Status == "gap";

            session.InventoryJson = JsonSerializer.Serialize(analysis.Inventory);
            session.AnalysisJson = JsonSerializer.Serialize(analysis.Surface);

            // Record the backend dirs the frontend-trace actually resolved, for display + reproducibility.
            if (resolvedBackendPaths.Count > 0)
            {
                var dirs = resolvedBackendPaths
                    .Select(p => p.TrimStart('/'))
                    .Select(p => p.LastIndexOf('/') is var i && i > 0 ? p[..i] : p)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                session.SourceDirsJson = JsonSerializer.Serialize(dirs);
                Pub(key, EventKinds.Result, EventPhases.Analyze,
                    $"Traced {dirs.Count} backend source dir(s) from the frontend module");
            }

            var selectedCount = analysis.Surface.Rows.Count(r => r.Selected);
            Pub(key, EventKinds.Result, EventPhases.Analyze,
                $"Analyzer: {analysis.Inventory.Routes.Count} route(s) · {analysis.Surface.Rows.Count} surface(s) · {selectedCount} selected",
                EventLevels.Success, new()
                {
                    ["routes"] = analysis.Inventory.Routes.Count,
                    ["surfaces"] = analysis.Surface.Rows.Count,
                    ["selected"] = selectedCount,
                });

            if (session.AutoHandoff)
            {
                session.Status = ApiSessionStatus.Drafting;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.System, text: "Analyzer done — handing off to generators");
                await GenerateDraftsInnerAsync(db, session, analysis, key, ct);
            }
            else
            {
                session.Status = ApiSessionStatus.AwaitingReview;
                Pub(key, EventKinds.System, EventPhases.Analyze,
                    "Analyzer done — awaiting surface review", data: Terminal());
                PersistLog(session, key);
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("QA Loop pipeline cancelled for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Cancelled;
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop pipeline failed for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── Generate pipeline (QAL-022) ──────────────────────────────────────────

    private async Task RunGenerateAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            var inventory = session.InventoryJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<InventoryDoc>(session.InventoryJson)
                  ?? throw new InvalidOperationException("InventoryJson deserialization returned null.")
                : throw new InvalidOperationException("InventoryJson missing — run Analyze first.");
            var surface = session.AnalysisJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<SurfaceMatrix>(session.AnalysisJson)
                  ?? throw new InvalidOperationException("AnalysisJson deserialization returned null.")
                : throw new InvalidOperationException("AnalysisJson missing — run Analyze first.");

            session.Status = ApiSessionStatus.Drafting;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await GenerateDraftsInnerAsync(db, session, new ModuleAnalysis(inventory, surface, []), key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Status = ApiSessionStatus.Cancelled;
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "GenerateDrafts failed for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    private async Task GenerateDraftsInnerAsync(
        QaDbContext db, ApiTestSession session, ModuleAnalysis analysis, string key, CancellationToken ct)
    {
        var llm = await _llmFactory.CreateAsync(ct);
        Pub(key, EventKinds.Phase, EventPhases.Generate, "Generators started (contract / endpoint / workflow)");

        var selected = analysis.Surface.Rows.Where(r => r.Selected).ToList();

        // Match surface rows back to inventory items by label substring
        var contractRoutes = MatchRoutes(selected.Where(r => r.Level == TestLevel.Contract), analysis.Inventory.Routes);
        var endpointRoutes = MatchRoutes(selected.Where(r => r.Level == TestLevel.Endpoint), analysis.Inventory.Routes);
        var workflowItems  = MatchWorkflows(selected.Where(r => r.Level == TestLevel.Workflow), analysis.Inventory.Workflows);

        // All generators run in parallel (Task.WhenAll across all three levels)
        var allTasks = new List<Task<List<GeneratedSpec>?>>();

        foreach (var r in contractRoutes)
        {
            var route = r;
            allTasks.Add(GenerateSafeAsync(async () =>
            {
                var spec = await new ContractGeneratorAgent(llm).GenerateAsync(
                    session.Module, route, emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct);
                return new List<GeneratedSpec> { spec };
            }, key));
        }

        foreach (var r in endpointRoutes)
        {
            var route = r;
            allTasks.Add(GenerateSafeAsync(async () =>
                (List<GeneratedSpec>?) await new EndpointGeneratorAgent(llm).GenerateAsync(
                    session.Module, route, emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct), key));
        }

        foreach (var w in workflowItems)
        {
            var workflow = w;
            allTasks.Add(GenerateSafeAsync(async () =>
            {
                var spec = await new WorkflowGeneratorAgent(llm).GenerateAsync(
                    session.Module, workflow, analysis.Inventory, emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct);
                return new List<GeneratedSpec> { spec };
            }, key));
        }

        var results = await Task.WhenAll(allTasks);

        // Persist all specs; per-draft disk errors are warned but don't fail the batch
        var now = DateTime.UtcNow;
        var sortOrder = 0;
        var draftCount = 0;
        foreach (var specList in results)
            foreach (var spec in specList ?? [])
            {
                PersistDraft(db, session, spec, sortOrder++, now, key);
                draftCount++;
            }

        await db.SaveChangesAsync(ct);

        session.Status = ApiSessionStatus.Drafted;
        Pub(key, EventKinds.System, EventPhases.Generate,
            $"Drafted {draftCount} test case(s) — awaiting review",
            EventLevels.Success, new() { ["terminal"] = true, ["count"] = draftCount });
        PersistLog(session, key);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── Push pipeline (QAL-023) ──────────────────────────────────────────────

    private async Task RunPushAsync(int sessionId, int testPlanId, int testSuiteId, int? storyId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) { _log.LogError("Push: session {Id} not found", sessionId); return; }

        session.Status = ApiSessionStatus.Pushing;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, EventPhases.Push, "Push to ADO started");

        var drafts = await db.ApiTestDrafts
            .Where(d => d.SessionId == sessionId && d.Status == ApiDraftStatus.Approved)
            .OrderBy(d => d.SortOrder)
            .ToListAsync(ct);

        if (drafts.Count == 0)
        {
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = "No approved drafts to push.";
            Pub(key, EventKinds.Error, EventPhases.Push,
                "Push failed: no approved drafts", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var pushed = 0;
            for (var i = 0; i < drafts.Count; i++)
            {
                var draft = drafts[i];
                var n = i + 1;
                Pub(key, "push", EventPhases.Push, $"{n}/{drafts.Count}: creating Test Case — {draft.Title}");

                var steps = ParseSteps(draft.StepsJson);
                var tcId = await _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, ct);
                await _ado.AddTestCaseToSuiteAsync(testPlanId, testSuiteId, tcId, ct);

                var workItemId = storyId ?? session.WorkItemId;
                if (workItemId.HasValue)
                    await _ado.LinkTestedByAsync(workItemId.Value, tcId, ct);

                // Rewrite [<tcId-pending>] → [id] on both the draft title and the spec file on disk
                draft.AdoTestCaseId = tcId;
                draft.Status = ApiDraftStatus.Pushed;
                draft.Title = RewriteTag(draft.Title, tcId, draft.CaseIndex);
                draft.UpdatedAt = DateTime.UtcNow;
                RewriteSpecOnDisk(draft, tcId);
                pushed++;
                await db.SaveChangesAsync(ct);

                Pub(key, EventKinds.Result, EventPhases.Push,
                    $"TC #{tcId} pushed → suite" + (workItemId.HasValue ? " + linked to story" : ""),
                    EventLevels.Success, new() { ["testCaseId"] = tcId });
            }

            session.Status = ApiSessionStatus.Done;
            Pub(key, EventKinds.System, EventPhases.Push,
                $"Pushed {pushed} test case(s) to ADO",
                EventLevels.Success, new() { ["terminal"] = true, ["count"] = pushed });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var pushedCount = drafts.Count(d => d.Status == ApiDraftStatus.Pushed);
            // Partial push: stay Drafted so the user can re-push the remaining approved drafts
            session.Status = ApiSessionStatus.Drafted;
            Pub(key, EventKinds.System, EventPhases.Push,
                $"Push cancelled — {pushedCount} of {drafts.Count} pushed", EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Push failed for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, EventPhases.Push,
                $"Push failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── Source fetch ─────────────────────────────────────────────────────────

    private async Task<string> FetchSourceAsync(ApiTestSession session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.Repository) || string.IsNullOrEmpty(session.Branch))
            return $"Module: {session.Module}";

        // Frontend-module-driven scan: seed with the module's frontend apis/ so the analyzer can read the
        // backend endpoints it calls and request the backend handlers via additionalFilesNeeded. Falls
        // back to the SourceDirs / module-name path below if the module has no apis/ folder.
        if (!string.IsNullOrEmpty(session.FrontendPath))
        {
            var seeded = await FetchFrontendApisAsync(session, ct);
            if (seeded is not null) return seeded;
        }

        var sourceDirs = ParseSourceDirs(session.SourceDirsJson);

        try
        {
            var paths = new List<string>();
            if (sourceDirs.Count > 0)
            {
                // Fetch each supplied directory — this is the only reliable way to find a module
                // that isn't a top-level repo folder (e.g. Lead/src/.../Deal nested several levels in).
                foreach (var dir in sourceDirs)
                {
                    try
                    {
                        paths.AddRange(await _ado.ListRepoFilePathsAsync(dir, branch: session.Branch, repoId: session.Repository, ct));
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Skipping unreadable source dir {Dir}", dir); }
                }
            }
            else
            {
                // Fallback: treat the module name itself as a top-level scopePath. Only correct when
                // the module happens to live at the repo root — supply SourceDirs for anything nested.
                paths.AddRange(await _ado.ListRepoFilePathsAsync(session.Module, branch: session.Branch, repoId: session.Repository, ct));
            }

            if (paths.Count == 0)
                return $"Module: {session.Module}";

            var sb = new StringBuilder();
            sb.Append($"# Module: {session.Module}\n# Repository: {session.Repository} Branch: {session.Branch}\n\n");
            foreach (var path in paths.Take(8))
            {
                try
                {
                    var content = await _ado.GetFileContentAsync(
                        path, @ref: session.Branch, repoId: session.Repository, ct: ct);
                    sb.Append($"## {path}\n```\n{content}\n```\n\n");
                }
                catch (Exception ex) { _log.LogDebug(ex, "Skipping unreadable file {Path}", path); }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Source fetch failed for module '{Module}'", session.Module);
            return $"Module: {session.Module}";
        }
    }

    private static List<string> ParseSourceDirs(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    /// <summary>Builds the analyzer seed from the selected frontend module's apis/ folder (the axios/fetch
    /// calls that name the backend endpoints). Returns null when there's no apis/ folder so the caller
    /// falls back to the SourceDirs/module-name path. The analyzer then requests the backend handlers via
    /// additionalFilesNeeded (resolved by the search-aware fetchFile in RunPipelineInnerAsync).</summary>
    private async Task<string?> FetchFrontendApisAsync(ApiTestSession session, CancellationToken ct)
    {
        try
        {
            var apisDir = $"{session.FrontendPath!.TrimEnd('/')}/apis";
            var paths = (await _ado.ListRepoFilePathsAsync(apisDir, branch: session.Branch, repoId: session.Repository, ct))
                .Where(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (paths.Count == 0) return null;   // no apis/ folder — fall back to legacy fetch

            var sb = new StringBuilder();
            sb.Append($"# Frontend module: {session.Module} (route {session.FrontendModule})\n");
            sb.Append($"# Repository: {session.Repository} Branch: {session.Branch}\n\n");
            sb.Append("These are the FRONTEND API-client files for this module - they call the backend REST\n");
            sb.Append("endpoints under test. Identify every backend endpoint (METHOD + path) they call, then\n");
            sb.Append("use additionalFilesNeeded (endpoint route strings and/or handler class names as search\n");
            sb.Append("terms) to fetch the backend implementation and build the full inventory.\n\n");
            foreach (var path in paths.Take(8))
            {
                try
                {
                    var content = await _ado.GetFileContentAsync(path, @ref: session.Branch, repoId: session.Repository, ct: ct);
                    sb.Append($"## {path}\n```\n{content}\n```\n\n");
                }
                catch (Exception ex) { _log.LogDebug(ex, "Skipping unreadable frontend file {Path}", path); }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Frontend apis fetch failed for module '{Module}'", session.Module);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<RouteInfo> MatchRoutes(IEnumerable<SurfaceRow> rows, List<RouteInfo> routes) =>
        rows.Select(r => routes.FirstOrDefault(x =>
                r.Label.Contains($"{x.Method} {x.Route}", StringComparison.OrdinalIgnoreCase)
                || r.Label.Contains(x.Route, StringComparison.OrdinalIgnoreCase)))
            .Where(r => r is not null).Select(r => r!)
            .Distinct().ToList();

    private static List<WorkflowInfo> MatchWorkflows(IEnumerable<SurfaceRow> rows, List<WorkflowInfo> workflows) =>
        rows.Select(r => workflows.FirstOrDefault(w =>
                r.Label.Contains(w.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(w => w is not null).Select(w => w!)
            .Distinct().ToList();

    private void PersistDraft(QaDbContext db, ApiTestSession session, GeneratedSpec spec,
        int sortOrder, DateTime now, string key)
    {
        var level = spec.Level == "contract" ? TestLevel.Contract
            : spec.Level == "workflow" ? TestLevel.Workflow
            : TestLevel.Endpoint;

        var draft = new ApiTestDraft
        {
            SessionId = session.Id,
            Level = level,
            Title = spec.TestCase?.Title ?? spec.Title,
            Preconditions = spec.TestCase?.Preconditions ?? "",
            StepsJson = spec.TestCase is not null
                ? JsonSerializer.Serialize(
                    spec.TestCase.Steps.Select(s => new { action = s.Action, expected = s.Expected }))
                : "[]",
            SchemaJson = spec.SchemaJson,
            SpecCode = spec.SpecCode,
            SourceRoutesJson = spec.SourceRoutes.Count > 0
                ? JsonSerializer.Serialize(spec.SourceRoutes)
                : null,
            CaseIndex = spec.CaseIndex,
            Status = ApiDraftStatus.Draft,
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (!string.IsNullOrEmpty(spec.SpecCode))
        {
            try
            {
                var levelDir = Path.Combine(_scriptsDir, session.Module, level.ToDirName());
                Directory.CreateDirectory(levelDir);
                // QAL-067: a multi-case spec (Endpoint's 200/401/400) is ONE shared physical file for
                // all its cases — slug it from something identical across those cases (the route), not
                // from the per-case title, or only case 0's write would ever "stick" as the group's file.
                var slug = spec.CaseIndex is not null && spec.SourceRoutes.Count > 0
                    ? Slug(spec.SourceRoutes[0])
                    : Slug(spec.TestCase?.Title ?? spec.Title);
                var specPath = Path.Combine(levelDir, $"{slug}.spec.ts");
                // Only case 0 actually needs to write the (identical) shared file to disk; cases 1..N
                // still get SpecFilePath pointed at it so their push-time rewrite isn't a silent no-op.
                if (spec.CaseIndex is null or 0)
                {
                    File.WriteAllText(specPath, spec.SpecCode);
                    Pub(SessionKey(session.Id), EventKinds.Result, EventPhases.Generate,
                        $"Spec written: {Path.GetFileName(specPath)}");
                }
                draft.SpecFilePath = specPath;

                // QAL-071: Contract specs import their Zod schema via a relative path the model invents
                // itself (unrelated to our own spec-filename slug) — write SchemaJson out under THAT
                // exact name, next to the spec, or the import can never resolve.
                TryWriteSchemaFile(levelDir, spec.SpecCode, spec.SchemaJson, session.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to write spec to disk for '{Title}'", spec.Title);
            }
        }

        db.ApiTestDrafts.Add(draft);
        Pub(SessionKey(session.Id), EventKinds.Result, EventPhases.Generate,
            $"Draft {sortOrder + 1}: {draft.Title}");
    }

    // ponytail: swallows non-cancel exceptions per surface; batch continues
    private async Task<List<GeneratedSpec>?> GenerateSafeAsync(Func<Task<List<GeneratedSpec>?>> factory, string key)
    {
        try { return await factory(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Generator failed for surface — skipping");
            Pub(key, EventKinds.Error, EventPhases.Generate,
                $"Generator error (surface skipped): {ex.Message}", EventLevels.Warn);
            return null;
        }
    }

    private static string RewriteTag(string title, int tcId, int? caseIndex) =>
        RewriteTagText(title, tcId, caseIndex, out var rewritten) ? rewritten : $"[{tcId}] {title}";

    /// <summary>QAL-067: replaces ONLY this draft's own placeholder ([<tcId-pending:N>] for a case-indexed
    /// draft, or the plain [<tcId-pending>] for Contract/Workflow). A global/unscoped replace would let
    /// case 0 pushing first stomp every other case's still-pending placeholder in a shared spec file.</summary>
    private static bool RewriteTagText(string text, int tcId, int? caseIndex, out string rewritten)
    {
        var tag = TitleTag.PendingFor(caseIndex);
        if (text.Contains(tag))
        {
            rewritten = text.Replace(tag, $"[{tcId}]");
            return true;
        }
        rewritten = text;
        return false;
    }

    private static void RewriteSpecOnDisk(ApiTestDraft draft, int tcId)
    {
        if (string.IsNullOrEmpty(draft.SpecFilePath) || !File.Exists(draft.SpecFilePath)) return;
        try
        {
            var text = File.ReadAllText(draft.SpecFilePath);
            if (RewriteTagText(text, tcId, draft.CaseIndex, out var updated) && updated != text)
                File.WriteAllText(draft.SpecFilePath, updated);
        }
        catch { /* best-effort disk rewrite */ }
    }

    /// <summary>QAL-071: matches the one local relative import a generated spec uses for its schema,
    /// e.g. <c>from './get-lead-deal-dealId.schema'</c> → captures "get-lead-deal-dealId.schema". Package
    /// imports (@playwright/test, allure-playwright, ...) are never relative so they never match.</summary>
    private static readonly System.Text.RegularExpressions.Regex SchemaImportPattern =
        new(@"from\s+['""]\.\/([\w.-]+)['""]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Best-effort, non-fatal: if the spec has a SchemaJson and a resolvable local relative
    /// import, writes the schema out under the model's OWN chosen import name (+.ts) next to the spec.
    /// No-op (silent) when there's no schema (Endpoint/Workflow) or no matching import.</summary>
    private void TryWriteSchemaFile(string levelDir, string specCode, string? schemaJson, int sessionId)
    {
        if (string.IsNullOrEmpty(schemaJson)) return;
        var name = ExtractSchemaImportName(specCode);
        if (name is null) return;

        try
        {
            var schemaPath = Path.Combine(levelDir, $"{name}.ts");
            File.WriteAllText(schemaPath, schemaJson);
            Pub(SessionKey(sessionId), EventKinds.Result, EventPhases.Generate,
                $"Schema written: {Path.GetFileName(schemaPath)}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write schema file for import '{Name}'", name);
        }
    }

    /// <summary>Extracts the relative-import module name a generated spec references (its schema file),
    /// e.g. "./get-lead-deal-dealId.schema" → "get-lead-deal-dealId.schema". Null if no local relative
    /// import is found (Endpoint/Workflow specs, or a Contract spec with no import at all).</summary>
    private static string? ExtractSchemaImportName(string specCode)
    {
        var m = SchemaImportPattern.Match(specCode);
        return m.Success ? m.Groups[1].Value : null;
    }

    private async Task MarkQueuedAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.ApiTestSessions.FindAsync(sessionId);
            if (row is not null)
            {
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort */ }
    }

    private async Task MarkCancelledAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.ApiTestSessions.FindAsync(sessionId);
            if (row is not null && !TerminalStatuses.Contains(row.Status))
            {
                row.Status = ApiSessionStatus.Cancelled;
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort */ }
    }

    private static List<TestCaseStep> ParseSteps(string? stepsJson)
    {
        var steps = new List<TestCaseStep>();
        if (string.IsNullOrEmpty(stepsJson)) return steps;
        if (JsonNode.Parse(stepsJson) is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonObject xo)
                    steps.Add(new TestCaseStep
                    {
                        Action = JsonUtils.StrOr(xo, "action"),
                        Expected = JsonUtils.StrOr(xo, "expected"),
                    });
        return steps;
    }

    private Action<LlmEmit> EmitClosure(string key, string phase, bool activityEnabled = true)
        => activityEnabled
            ? emit => _bus.Publish(key, new ConsoleEvent { Kind = emit.Kind, Text = emit.Text, Phase = phase })
            : _ => { };

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private void PersistLog(ApiTestSession session, string key)
    {
        if (!session.ActivityEnabled) return;
        try { session.EventLogJson = JsonSerializer.Serialize(_bus.Narrative(key)); }
        catch { /* never block the pipeline on log persistence */ }
    }

    private static async Task SafeCommitAsync(QaDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch { /* swallow — terminal status writes are best-effort during teardown */ }
    }

    private static Dictionary<string, object?> Terminal() => new() { ["terminal"] = true };
    private static string SessionKey(int sessionId) => $"qa-session:{sessionId}";
    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
