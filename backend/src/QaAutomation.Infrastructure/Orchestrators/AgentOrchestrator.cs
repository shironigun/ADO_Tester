using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Test-authoring pipeline (spec B5 — port of the Python <c>AgentOrchestrator</c>). Singleton.
/// <para><see cref="StartAsync"/> seeds a <see cref="TestSession"/> synchronously and returns its id;
/// the Analyst → Tester pipeline then runs on a background <see cref="Task"/> with its OWN DI scope
/// (never the request scope — that disposes when the HTTP handler returns). Live progress is pumped
/// to <c>CurrentActivity</c> and the <see cref="IEventBus"/>; the durable narrative is persisted to
/// <c>EventLogJson</c>. Cancellation flows through the <see cref="CancellationRegistry"/>.</para>
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private static readonly IReadOnlySet<SessionStatus> TerminalStatuses =
        new HashSet<SessionStatus> { SessionStatus.Done, SessionStatus.Drafted, SessionStatus.Failed, SessionStatus.Cancelled };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly PipelineConcurrency _concurrency;
    private readonly CancellationRegistry _cancellations;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly ICodeContextService _codeContext;
    private readonly IExistingTestsIndexService _dedupIndex;
    private readonly ILogger<AgentOrchestrator> _log;

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory, IEventBus bus, PipelineConcurrency concurrency,
        CancellationRegistry cancellations, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        ICodeContextService codeContext, IExistingTestsIndexService dedupIndex,
        ILogger<AgentOrchestrator> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _concurrency = concurrency;
        _cancellations = cancellations;
        _ado = ado;
        _llmFactory = llmFactory;
        _codeContext = codeContext;
        _dedupIndex = dedupIndex;
        _log = log;
    }

    public bool Cancel(int sessionId) => _cancellations.Cancel(sessionId);

    // ── start (Analyst, then Tester if auto) ─────────────────────────────────

    public async Task<int> StartAsync(
        int projectId, int workItemId, bool autoHandoff = true, AuthorRunContext? context = null,
        int? batchId = null, CancellationToken cancellationToken = default)
    {
        var wi = await _ado.GetWorkItemAsync(workItemId, ct: cancellationToken);

        int sessionId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var now = DateTime.UtcNow;
            var session = new TestSession
            {
                ProjectId = projectId,
                WorkItemId = workItemId,
                WorkItemTitle = wi.Title,
                Status = SessionStatus.Analyzing,
                CurrentActivity = "Fetched work item from Azure DevOps",
                BatchId = batchId,
                // Free-text guidance the Analyst uses; reference plan the Tester dedups against.
                AdditionalContext = context?.AdditionalContext,
                ReferenceTestPlanId = context?.ReferenceTestPlanId,
                ReferenceTestSuiteId = context?.ReferenceTestSuiteId,
                PlanNavigationHint = context?.PlanNavigationHint,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.TestSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            sessionId = session.Id;
        }

        Launch(sessionId, token => RunPipelineAsync(sessionId, projectId, workItemId, autoHandoff, token));
        return sessionId;
    }

    public async Task RunTesterAsync(int sessionId, AuthorRunContext? context = null, CancellationToken cancellationToken = default)
    {
        // Persist any gate-supplied context/reference plan first so the background rerun (own scope) reads it.
        if (context is not null)
            await ApplyRunContextAsync(sessionId, context, cancellationToken);
        Launch(sessionId, token => RerunTesterAsync(sessionId, token));
    }

    public Task RerunAnalystAsync(int sessionId, string feedback, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => RerunAnalystInnerAsync(sessionId, feedback ?? "", token));
        return Task.CompletedTask;
    }

    public Task RefineTesterAsync(
        int sessionId, string? overallFeedback, IReadOnlyList<TestFeedback> perTest, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => RefineTesterInnerAsync(sessionId, overallFeedback, perTest ?? [], token));
        return Task.CompletedTask;
    }

    /// <summary>Persist gate-supplied Tester context + reference plan onto the session (best-effort).</summary>
    private async Task ApplyRunContextAsync(int sessionId, AuthorRunContext context, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null) return;
        if (!string.IsNullOrWhiteSpace(context.AdditionalContext)) session.TesterContext = context.AdditionalContext;
        if (context.ReferenceTestPlanId is not null) session.ReferenceTestPlanId = context.ReferenceTestPlanId;
        if (context.ReferenceTestSuiteId is not null) session.ReferenceTestSuiteId = context.ReferenceTestSuiteId;
        if (!string.IsNullOrWhiteSpace(context.PlanNavigationHint)) session.PlanNavigationHint = context.PlanNavigationHint;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public Task PushToAdoAsync(
        int sessionId, int testPlanId, int testSuiteId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => PushAsync(sessionId, testPlanId, testSuiteId, token));
        return Task.CompletedTask;
    }

    /// <summary>Push one draft synchronously (per-row push). Unlike <see cref="PushToAdoAsync"/> this is
    /// awaited by the caller — a single test case is just three quick ADO calls, so the row gets an
    /// immediate result instead of an SSE-tracked background run. Idempotent if already Pushed.</summary>
    public async Task<TestCaseDraft> PushDraftAsync(
        int draftId, int testPlanId, int testSuiteId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var draft = await db.TestCaseDrafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken)
            ?? throw new InvalidOperationException($"Draft {draftId} not found.");
        if (draft.Status == DraftStatus.Pushed && draft.AdoTestCaseId is not null)
            return draft; // already in ADO — no duplicate Test Case

        var session = await db.TestSessions.FindAsync([draft.SessionId], cancellationToken)
            ?? throw new InvalidOperationException($"Session {draft.SessionId} not found.");

        var steps = ParseSteps(draft.StepsJson);
        var tcId = await _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, cancellationToken);
        await _ado.AddTestCaseToSuiteAsync(testPlanId, testSuiteId, tcId, cancellationToken);
        await _ado.LinkTestedByAsync(session.WorkItemId, tcId, cancellationToken);

        draft.AdoTestCaseId = tcId;
        draft.Status = DraftStatus.Pushed;

        // Mirror the bulk push: once nothing is left to push (no Draft/Approved drafts remain), the
        // session reads as Done — so a session fully pushed row-by-row isn't stuck mid-flight.
        var remaining = await db.TestCaseDrafts.CountAsync(
            d => d.SessionId == session.Id && d.Id != draft.Id
                 && (d.Status == DraftStatus.Draft || d.Status == DraftStatus.Approved), cancellationToken);
        if (remaining == 0)
        {
            session.Status = SessionStatus.Done;
            session.CurrentActivity = "All test cases pushed to ADO";
        }
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    /// <summary>Register a cancellation token for the session and run <paramref name="body"/> on a
    /// background task, clearing the registry entry when it finishes.</summary>
    private void Launch(int sessionId, Func<CancellationToken, Task> body)
    {
        var token = _cancellations.Register(sessionId).Token;
        _ = Task.Run(async () =>
        {
            try { await body(token); }
            finally { _cancellations.Remove(sessionId); }
        });
    }

    // ── pipelines ─────────────────────────────────────────────────────────────

    /// <summary>Queue behind the global concurrency cap, then run. A child cancelled while still queued
    /// (gate not yet acquired) is marked Cancelled here since its inner body never runs.</summary>
    private async Task RunPipelineAsync(int sessionId, int projectId, int workItemId, bool autoHandoff, CancellationToken ct)
    {
        try
        {
            await MarkQueuedAsync(sessionId);
            await _concurrency.Gate.WaitAsync(ct);
            try
            {
                await RunPipelineInnerAsync(sessionId, projectId, workItemId, autoHandoff, ct);
            }
            finally
            {
                _concurrency.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(sessionId);   // guarded against terminal — no-op if the inner body already set it
        }
    }

    private async Task RunPipelineInnerAsync(int sessionId, int projectId, int workItemId, bool autoHandoff, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var kb = scope.ServiceProvider.GetRequiredService<IKnowledgebaseService>();

        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        try
        {
            // Resolve the LLM client inside the try so a config error (e.g. provider not configured)
            // is recorded on the session rather than crashing the background task.
            var llm = await _llmFactory.CreateAsync(ct);
            _log.LogInformation("Session {SessionId} using LLM provider: {Provider}", sessionId, llm.ProviderName);
            Pub(key, EventKinds.System, text: $"Pipeline started · provider {llm.ProviderName}");

            await UpdateActivityAsync(db, session, "Re-fetching work item from Azure DevOps", ct);
            var wi = await _ado.GetWorkItemAsync(workItemId, ct: ct);
            Pub(key, EventKinds.Phase, EventPhases.Fetch, $"Fetched work item #{wi.Id} — {wi.Title}",
                EventLevels.Success, new() { ["workItemId"] = wi.Id, ["type"] = wi.WorkItemType });

            await UpdateActivityAsync(db, session, "Retrieving relevant chunks from the domain knowledgebase", ct);
            var query = $"{wi.Title} {wi.AcceptanceCriteria ?? ""}".Trim();
            var context = await kb.SearchAsync(projectId, query, 8, ct);
            Pub(key, EventKinds.Result, EventPhases.Retrieve, $"Retrieved {context.Count} knowledgebase chunk(s)",
                EventLevels.Success, new() { ["chunks"] = context.Count, ["files"] = context.Select(h => h.FileName).ToList() });

            // Phase 2 — Discover code context from the ADO repo.
            await UpdateActivityAsync(db, session, "Discovering code context from ADO repository", ct);
            Pub(key, EventKinds.Phase, EventPhases.Discover, "Discovering code context from repository");
            try
            {
                var (_, codeCtx) = await _codeContext.DiscoverAsync(
                    workItemId, wi, null, [], [], session.AdditionalContext, alwaysSearch: true, llm, key, ct);
                session.CodeContext = string.IsNullOrEmpty(codeCtx) ? null : codeCtx;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Result, EventPhases.Discover,
                    string.IsNullOrEmpty(codeCtx) ? "No code context found" : "Code context discovered",
                    EventLevels.Success, new() { ["hasCode"] = !string.IsNullOrEmpty(codeCtx) });

                // Distill the discovered code into plain-English behavior bullets for the reviewer to
                // annotate at the Analyst→Tester gate (fed to the Tester as extra guidance). Runs on the
                // already byte-budgeted code, so it's one cheap extra call; failures never block the run.
                if (!string.IsNullOrEmpty(codeCtx))
                {
                    try
                    {
                        var bullets = await RunStreamingAsync(db, session, "Analyzing code behavior",
                            (p, c) => new CodeBehaviorAgent(llm).AnalyzeAsync(wi, codeCtx!, p, EmitClosure(key, EventPhases.Discover), c),
                            key, EventPhases.Discover, ct);
                        session.CodeBehaviorJson = BuildCodeBehaviorJson(bullets);
                        session.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        Pub(key, EventKinds.Result, EventPhases.Discover,
                            $"Analyzed code behavior — {bullets.Count} point(s)",
                            EventLevels.Success, new() { ["points"] = bullets.Count });
                    }
                    catch (Exception behaviorEx) when (behaviorEx is not OperationCanceledException)
                    {
                        _log.LogWarning(behaviorEx, "Code behavior analysis failed for session {SessionId} — continuing", sessionId);
                        Pub(key, EventKinds.System, EventPhases.Discover, $"Code behavior analysis skipped: {behaviorEx.Message}", EventLevels.Warn);
                    }
                }
            }
            catch (Exception discoverEx) when (discoverEx is not OperationCanceledException)
            {
                _log.LogWarning(discoverEx, "Code discovery failed for session {SessionId} — continuing without code context", sessionId);
                Pub(key, EventKinds.System, EventPhases.Discover, $"Code discovery skipped: {discoverEx.Message}", EventLevels.Warn);
            }

            await UpdateActivityAsync(db, session, $"Analyst — analyzing story (using {context.Count} KB chunks)", ct);
            Pub(key, EventKinds.Phase, EventPhases.Analyst, $"Analyst started ({context.Count} chunks)");
            var analysis = await RunStreamingAsync(db, session, "Analyst — analyzing story",
                (p, c) => new AnalystAgent(llm).AnalyzeAsync(
                    wi, context, session.AdditionalContext, session.CodeContext, p, EmitClosure(key, EventPhases.Analyst), c),
                key, EventPhases.Analyst, ct);
            Pub(key, EventKinds.Result, EventPhases.Analyst,
                $"Analyst produced: {analysis.Module} / {analysis.Feature} · " +
                $"{analysis.AcceptanceCriteria.Count} ACs · {analysis.EdgeCases.Count} edge · " +
                $"{analysis.NegativeCases.Count} negative",
                EventLevels.Success, new()
                {
                    ["acceptanceCriteria"] = analysis.AcceptanceCriteria.Count,
                    ["edgeCases"] = analysis.EdgeCases.Count,
                    ["negativeCases"] = analysis.NegativeCases.Count,
                    ["businessRules"] = analysis.BusinessRules.Count,
                });

            session.AnalysisJson = analysis.ToJson();
            if (autoHandoff)
            {
                session.Status = SessionStatus.Drafting;
                session.CurrentActivity = "Analyst done — handing off to Tester";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.System, text: "Analyst done — handing off to Tester");
                await UpdateActivityAsync(db, session, "Tester — generating test cases in QA style", ct);
                await RunTesterInnerAsync(db, session, analysis, wi, key, ct);
            }
            else
            {
                session.Status = SessionStatus.AwaitingTester;
                session.CurrentActivity = "Analyst done — click Run Tester to continue";
                Pub(key, EventKinds.System, EventPhases.Analyst, "Analyst done — awaiting manual Run Tester", data: Terminal());
                PersistLog(session, key);
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Pipeline cancelled for session {SessionId}", sessionId);
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = "Cancelled by user";
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Pipeline failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Failed: {e.Message}";
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    private async Task RerunTesterAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        try
        {
            await _llmFactory.CreateAsync(ct);   // resolve so a missing provider lands on this session

            if (string.IsNullOrEmpty(session.AnalysisJson))
                throw new InvalidOperationException("Analyst analysis missing — run Analyst first.");
            var analysis = AnalystOutput.FromJson(JsonNode.Parse(session.AnalysisJson));

            session.Status = SessionStatus.Drafting;
            session.CurrentActivity = "Tester — generating test cases in QA style";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var wi = await _ado.GetWorkItemAsync(session.WorkItemId, ct: ct);
            await RunTesterInnerAsync(db, session, analysis, wi, key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = "Cancelled by user";
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Manual Tester failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Failed: {e.Message}";
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    private async Task RunTesterInnerAsync(
        QaDbContext db, TestSession session, AnalystOutput analysis, AdoWorkItem wi, string key, CancellationToken ct)
    {
        var llm = await _llmFactory.CreateAsync(ct);

        // Phase 3 — Build existing-tests dedup block from the reference plan index (if configured).
        string? existingTestsBlock = null;
        if (session.ReferenceTestPlanId is not null)
        {
            try
            {
                await _dedupIndex.EnsureIndexAsync(
                    session.ReferenceTestPlanId.Value, session.ReferenceTestSuiteId, forceRescan: false, ct);
                existingTestsBlock = await _dedupIndex.BuildTesterContextAsync(
                    session.ReferenceTestPlanId.Value, session.ReferenceTestSuiteId, analysis, 8192, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Dedup index build failed for session {SessionId} — proceeding without it", session.Id);
            }
        }

        Pub(key, EventKinds.Phase, EventPhases.Tester, "Tester started — generating test cases in QA style");
        var generated = await RunStreamingAsync(db, session, "Tester — generating test cases",
            (p, c) => new TesterAgent(llm).GenerateAsync(
                analysis, wi, EffectiveTesterContext(session), existingTestsBlock,
                p, EmitClosure(key, EventPhases.Tester), c),
            key, EventPhases.Tester, ct);

        // Post-gen dedup: similarity check against the full reference plan index.
        IReadOnlyList<DedupResult> dedupResults = [];
        if (session.ReferenceTestPlanId is not null && generated.Count > 0)
        {
            try
            {
                var toCheck = generated
                    .Select(g => new DedupTestCase(g.Title, string.Join(" | ", g.Steps.Select(s => s.Action))))
                    .ToList();
                dedupResults = await _dedupIndex.FindDuplicatesAsync(
                    session.ReferenceTestPlanId.Value, session.ReferenceTestSuiteId, toCheck, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-gen dedup check failed for session {SessionId} — proceeding without filtering", session.Id);
            }
        }

        var clearDupTitles = dedupResults.Where(d => d.IsClearDuplicate)
            .Select(d => d.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredGenerated = generated.Where(g => !clearDupTitles.Contains(g.Title)).ToList();

        if (dedupResults.Count > 0)
        {
            var skipped = dedupResults.Where(d => d.IsClearDuplicate).ToList();
            var flagged = dedupResults.Where(d => !d.IsClearDuplicate).ToList();
            Pub(key, EventKinds.Result, EventPhases.Dedup,
                $"Dedup: skipped {skipped.Count} duplicate(s), flagged {flagged.Count} borderline",
                EventLevels.Info,
                new()
                {
                    ["skipped"] = skipped.Select(d => new { d.Title, d.MatchedTitle, d.MatchedAdoId }).ToList<object>(),
                    ["flagged"] = flagged.Select(d => new { d.Title, d.MatchedTitle, d.MatchedAdoId }).ToList<object>(),
                });
        }

        // Clear any prior drafts (re-run) then insert the new ones — one transaction via SaveChanges.
        var existing = await db.TestCaseDrafts.Where(d => d.SessionId == session.Id).ToListAsync(ct);
        db.TestCaseDrafts.RemoveRange(existing);
        for (var i = 0; i < filteredGenerated.Count; i++)
        {
            var g = filteredGenerated[i];
            db.TestCaseDrafts.Add(new TestCaseDraft
            {
                SessionId = session.Id,
                Title = g.Title,
                Preconditions = g.Preconditions,
                StepsJson = JsonSerializer.Serialize(g.Steps.Select(st => new { action = st.Action, expected = st.Expected })),
                SortOrder = i,
            });
            Pub(key, EventKinds.Result, EventPhases.Tester, $"Test case {i + 1}/{filteredGenerated.Count}: {g.Title}");
        }

        session.Status = SessionStatus.Drafted;
        session.CurrentActivity = $"Drafted {filteredGenerated.Count} test case(s) — awaiting review";
        Pub(key, EventKinds.System, EventPhases.Tester, $"Drafted {filteredGenerated.Count} test case(s) — awaiting review",
            EventLevels.Success, new() { ["terminal"] = true, ["count"] = filteredGenerated.Count });
        PersistLog(session, key);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── refine loops (feedback-driven re-runs) ───────────────────────────────

    /// <summary>Re-run the Analyst against its own prior output + reviewer feedback, improving/filling
    /// gaps rather than starting over. Returns the session to AwaitingTester.</summary>
    private async Task RerunAnalystInnerAsync(int sessionId, string feedback, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var kb = scope.ServiceProvider.GetRequiredService<IKnowledgebaseService>();

        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);
            if (string.IsNullOrEmpty(session.AnalysisJson))
                throw new InvalidOperationException("Analyst analysis missing — run Analyst first.");
            var prior = AnalystOutput.FromJson(JsonNode.Parse(session.AnalysisJson));

            session.Status = SessionStatus.Analyzing;
            session.CurrentActivity = "Analyst — refining analysis with your feedback";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var wi = await _ado.GetWorkItemAsync(session.WorkItemId, ct: ct);
            var query = $"{wi.Title} {wi.AcceptanceCriteria ?? ""}".Trim();
            var context = await kb.SearchAsync(session.ProjectId, query, 8, ct);

            Pub(key, EventKinds.Phase, EventPhases.Analyst, "Analyst refine started — applying your feedback");
            var analysis = await RunStreamingAsync(db, session, "Analyst — refining analysis",
                (p, c) => new AnalystAgent(llm).RefineAsync(
                    prior, feedback, wi, context, session.AdditionalContext, session.CodeContext,
                    p, EmitClosure(key, EventPhases.Analyst), c),
                key, EventPhases.Analyst, ct);

            session.AnalysisJson = analysis.ToJson();
            session.Status = SessionStatus.AwaitingTester;
            session.CurrentActivity = "Analyst refined — review, then run the Tester";
            Pub(key, EventKinds.System, EventPhases.Analyst, "Analyst refined — awaiting manual Run Tester", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = "Cancelled by user";
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Analyst refine failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Failed: {e.Message}";
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    /// <summary>Feedback-driven Tester refine that MERGES into existing drafts (never a wholesale
    /// regenerate): update flagged, add new, remove only explicitly-flagged non-pushed drafts, and
    /// preserve everything else — pushed drafts are immutable.</summary>
    private async Task RefineTesterInnerAsync(
        int sessionId, string? overallFeedback, IReadOnlyList<TestFeedback> perTest, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);
            if (string.IsNullOrEmpty(session.AnalysisJson))
                throw new InvalidOperationException("Analyst analysis missing — run Analyst first.");
            var analysis = AnalystOutput.FromJson(JsonNode.Parse(session.AnalysisJson));

            session.Status = SessionStatus.Drafting;
            session.CurrentActivity = "Tester — refining test cases with your feedback";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var wi = await _ado.GetWorkItemAsync(session.WorkItemId, ct: ct);

            // Phase 3 — Build existing-tests dedup block from the reference plan index (if configured).
            string? existingTestsBlock = null;
            if (session.ReferenceTestPlanId is not null)
            {
                try
                {
                    existingTestsBlock = await _dedupIndex.BuildTesterContextAsync(
                        session.ReferenceTestPlanId.Value, session.ReferenceTestSuiteId, analysis, 8192, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Dedup index build failed for session {SessionId} refine — proceeding without it", session.Id);
                }
            }

            var drafts = await db.TestCaseDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            var feedbackByDraft = perTest.Where(f => !f.Remove)
                .GroupBy(f => f.DraftId).ToDictionary(g => g.Key, g => g.First().Feedback);
            var removeIds = perTest.Where(f => f.Remove).Select(f => f.DraftId).ToHashSet();

            // Only non-pushed drafts can be removed; pushed drafts are always kept.
            var toRemove = drafts.Where(d => removeIds.Contains(d.Id) && d.Status != DraftStatus.Pushed).ToList();
            var removedTitles = toRemove.Select(d => d.Title).ToList();

            var existing = drafts
                .Where(d => !toRemove.Contains(d))
                .Select(d => new ExistingDraftForRefine(
                    d.Id, d.Title, d.Preconditions, ParseSteps(d.StepsJson),
                    d.Status.ToString(), d.Status == DraftStatus.Pushed,
                    feedbackByDraft.TryGetValue(d.Id, out var fb) ? fb : null))
                .ToList();

            Pub(key, EventKinds.Phase, EventPhases.Tester, "Tester refine started — merging your feedback");
            var refined = await RunStreamingAsync(db, session, "Tester — refining test cases",
                (p, c) => new TesterAgent(llm).RefineAsync(
                    analysis, wi, existing, removedTitles, overallFeedback, EffectiveTesterContext(session),
                    existingTestsBlock, p, EmitClosure(key, EventPhases.Tester), c),
                key, EventPhases.Tester, ct);

            ReconcileDrafts(db, session, drafts, toRemove, refined, key);
            await db.SaveChangesAsync(ct);

            var total = await db.TestCaseDrafts.CountAsync(d => d.SessionId == sessionId, ct);
            session.Status = SessionStatus.Drafted;
            session.CurrentActivity = $"Refined — {total} test case(s) — awaiting review";
            Pub(key, EventKinds.System, EventPhases.Tester, $"Refined — {total} test case(s) — awaiting review",
                EventLevels.Success, new() { ["terminal"] = true, ["count"] = total });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = "Cancelled by user";
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Tester refine failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Failed: {e.Message}";
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    /// <summary>Merge the Tester's refine output into the draft set. Removes flagged non-pushed drafts,
    /// updates drafts whose <c>ref</c> matches (resetting Approved→Draft on a content change), and inserts
    /// ref:"new" items. Drafts neither returned nor flagged are preserved unchanged. Pushed drafts are
    /// never modified. Does not call SaveChanges (the caller commits).</summary>
    private void ReconcileDrafts(
        QaDbContext db, TestSession session, List<TestCaseDraft> current, List<TestCaseDraft> toRemove,
        List<RefinedTestCase> refined, string key)
    {
        var removedIds = toRemove.Select(d => d.Id).ToHashSet();
        if (toRemove.Count > 0)
        {
            db.TestCaseDrafts.RemoveRange(toRemove);
            Pub(key, EventKinds.Result, EventPhases.Tester, $"Removed {toRemove.Count} test case(s) per feedback");
        }

        var byId = current.Where(d => !removedIds.Contains(d.Id)).ToDictionary(d => d.Id);
        var maxSort = current.Count > 0 ? current.Max(d => d.SortOrder) : -1;
        int updated = 0, added = 0;

        foreach (var r in refined)
        {
            var stepsJson = JsonSerializer.Serialize(
                r.Steps.Select(st => new { action = st.Action, expected = st.Expected }));

            if (int.TryParse(r.Ref, out var refId))
            {
                if (removedIds.Contains(refId))
                    continue;   // never re-add a test the tester removed
                if (byId.TryGetValue(refId, out var draft))
                {
                    if (draft.Status == DraftStatus.Pushed)
                        continue;   // pushed drafts are immutable
                    var changed = draft.Title != r.Title || draft.StepsJson != stepsJson;
                    draft.Title = r.Title;
                    draft.StepsJson = stepsJson;
                    if (changed && draft.Status == DraftStatus.Approved)
                        draft.Status = DraftStatus.Draft;   // content changed → back to review
                    updated++;
                    continue;
                }
            }

            db.TestCaseDrafts.Add(new TestCaseDraft
            {
                SessionId = session.Id,
                Title = r.Title,
                Preconditions = r.Preconditions,
                StepsJson = stepsJson,
                Status = DraftStatus.Draft,
                SortOrder = ++maxSort,
            });
            added++;
        }
        Pub(key, EventKinds.Result, EventPhases.Tester, $"Refine merge: {updated} updated, {added} added");
    }

    private async Task PushAsync(int sessionId, int testPlanId, int testSuiteId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var session = await db.TestSessions.FindAsync([sessionId], ct);
        if (session is null)
        {
            _log.LogError("Push: session {SessionId} not found", sessionId);
            return;
        }

        session.Status = SessionStatus.Pushing;
        session.CurrentActivity = "Push starting…";
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, EventPhases.Push, "Push to ADO started");

        var drafts = await db.TestCaseDrafts
            .Where(d => d.SessionId == sessionId && d.Status == DraftStatus.Approved)
            .OrderBy(d => d.SortOrder)
            .ToListAsync(ct);

        if (drafts.Count == 0)
        {
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = "No approved drafts to push.";
            session.CurrentActivity = "Push failed: no approved drafts";
            Pub(key, EventKinds.Error, EventPhases.Push, "Push failed: no approved drafts", EventLevels.Error, Terminal());
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
                session.CurrentActivity = $"Pushing {n}/{drafts.Count}: creating Test Case";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                // NB: kind is the literal "push" here (verbatim from the Python — UI keys off it).
                Pub(key, "push", EventPhases.Push, $"{n}/{drafts.Count}: creating Test Case — {draft.Title}");

                var steps = ParseSteps(draft.StepsJson);
                var tcId = await _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, ct);

                session.CurrentActivity = $"Pushing {n}/{drafts.Count}: adding TC #{tcId} to suite";
                await db.SaveChangesAsync(ct);
                await _ado.AddTestCaseToSuiteAsync(testPlanId, testSuiteId, tcId, ct);

                session.CurrentActivity = $"Pushing {n}/{drafts.Count}: linking TC #{tcId} to story";
                await db.SaveChangesAsync(ct);
                await _ado.LinkTestedByAsync(session.WorkItemId, tcId, ct);

                draft.AdoTestCaseId = tcId;
                draft.Status = DraftStatus.Pushed;
                pushed++;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Result, EventPhases.Push, $"TC #{tcId} pushed → suite + linked to story",
                    EventLevels.Success, new() { ["testCaseId"] = tcId });
            }

            session.Status = SessionStatus.Done;
            session.CurrentActivity = $"Pushed {pushed} test case(s) to ADO";
            Pub(key, EventKinds.System, EventPhases.Push, $"Pushed {pushed} test case(s) to ADO",
                EventLevels.Success, new() { ["terminal"] = true, ["count"] = pushed });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var pushedCount = drafts.Count(d => d.Status == DraftStatus.Pushed);
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = $"Push cancelled — {pushedCount} of {drafts.Count} pushed";
            Pub(key, EventKinds.System, EventPhases.Push, session.CurrentActivity, EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Push failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Push failed: {e.Message}";
            Pub(key, EventKinds.Error, EventPhases.Push, $"Push failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Run a streaming agent call while pumping live progress into <c>CurrentActivity</c>
    /// (~every 1.5s) and publishing <c>stat</c> events, so the UI shows the agent is alive.</summary>
    private async Task<T> RunStreamingAsync<T>(
        QaDbContext db, TestSession session, string label,
        Func<StreamProgress, CancellationToken, Task<T>> factory, string key, string phase, CancellationToken ct)
    {
        var progress = new StreamProgress();
        var agentTask = factory(progress, ct);
        while (true)
        {
            var completed = await Task.WhenAny(agentTask, Task.Delay(1500, CancellationToken.None));
            var snap = progress.Snapshot();
            var elapsed = (int)snap.ElapsedSeconds;
            var chars = snap.Chars;
            session.CurrentActivity = chars > 0
                ? $"{label} — {chars:N0} chars · {elapsed}s"
                : $"{label} — working · {elapsed}s";
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            _bus.Publish(key, new ConsoleEvent
            {
                Kind = EventKinds.Stat,
                Phase = phase,
                Data = new() { ["chars"] = chars, ["elapsed"] = elapsed },
            });
            if (completed == agentTask)
                break;
            ct.ThrowIfCancellationRequested();   // cancelled between ticks → propagate (agent observes ct too)
        }
        return await agentTask;
    }

    private async Task MarkQueuedAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.TestSessions.FindAsync(sessionId);
            if (row is not null)
            {
                row.CurrentActivity = PipelineConcurrency.QueuedActivity;
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort cosmetic status */ }
    }

    /// <summary>Set a session to Cancelled — for a child cancelled while still queued (its body never
    /// ran). Guarded against terminal statuses so a late cancel can't clobber a finished run.</summary>
    private async Task MarkCancelledAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.TestSessions.FindAsync(sessionId);
            if (row is not null && !TerminalStatuses.Contains(row.Status))
            {
                row.Status = SessionStatus.Cancelled;
                row.CurrentActivity = "Cancelled by user";
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort */ }
    }

    private static List<TestCaseStep> ParseSteps(string? stepsJson)
    {
        var steps = new List<TestCaseStep>();
        if (string.IsNullOrEmpty(stepsJson))
            return steps;
        if (JsonNode.Parse(stepsJson) is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonObject xo)
                    steps.Add(new TestCaseStep
                    {
                        Action = JsonUtils.StrOr(xo, "action", "Action"),
                        Expected = JsonUtils.StrOr(xo, "expected", "Expected"),
                    });
        return steps;
    }

    private Action<LlmEmit> EmitClosure(string key, string phase)
        => emit => _bus.Publish(key, new ConsoleEvent { Kind = emit.Kind, Text = emit.Text, Phase = phase });

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private void PersistLog(TestSession session, string key)
    {
        try { session.EventLogJson = JsonSerializer.Serialize(_bus.Narrative(key)); }
        catch { /* never block the pipeline on log persistence */ }
    }

    /// <summary>Serialize behavior bullets into the stored JSON array <c>[{ id, behavior, comment }]</c>
    /// (empty comments). Returns null when there are no bullets.</summary>
    private static string? BuildCodeBehaviorJson(IReadOnlyList<string> bullets)
    {
        var arr = new JsonArray();
        var i = 1;
        foreach (var b in bullets)
        {
            if (string.IsNullOrWhiteSpace(b)) continue;
            arr.Add(new JsonObject { ["id"] = $"b{i++}", ["behavior"] = b.Trim(), ["comment"] = "" });
        }
        return arr.Count > 0 ? arr.ToJsonString() : null;
    }

    /// <summary>Combine the tester's free-text gate context with the annotated code-behavior bullets
    /// (behavior + reviewer notes) into one labeled block fed to the Tester. Null when both are empty.</summary>
    private static string? EffectiveTesterContext(TestSession session)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.TesterContext))
            parts.Add(session.TesterContext!.Trim());

        var behaviorBlock = BuildCodeBehaviorBlock(session.CodeBehaviorJson);
        if (behaviorBlock is not null)
            parts.Add(behaviorBlock);

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string? BuildCodeBehaviorBlock(string? codeBehaviorJson)
    {
        if (string.IsNullOrWhiteSpace(codeBehaviorJson))
            return null;
        try
        {
            if (JsonNode.Parse(codeBehaviorJson) is not JsonArray arr || arr.Count == 0)
                return null;
            var lines = new List<string>
            {
                "Code behavior analysis — what the current implementation actually does (derived from the " +
                "repo). Ground the test cases in this real behavior, and pay special attention to any " +
                "reviewer notes flagging suspected misbehavior (write cases that would catch them):",
            };
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var behavior = JsonUtils.StrOr(o, "behavior", "Behavior");
                if (string.IsNullOrWhiteSpace(behavior)) continue;
                var comment = JsonUtils.StrOr(o, "comment", "Comment");
                lines.Add(string.IsNullOrWhiteSpace(comment)
                    ? $"- {behavior.Trim()}"
                    : $"- {behavior.Trim()} [Reviewer note: {comment.Trim()}]");
            }
            return lines.Count > 1 ? string.Join("\n", lines) : null;
        }
        catch { return null; }
    }

    private static async Task UpdateActivityAsync(QaDbContext db, TestSession session, string message, CancellationToken ct)
    {
        session.CurrentActivity = message;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task SafeCommitAsync(QaDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch { /* swallow — terminal status writes are best-effort during teardown */ }
    }

    private static Dictionary<string, object?> Terminal() => new() { ["terminal"] = true };

    private static string SessionKey(int sessionId) => $"session:{sessionId}";
}
