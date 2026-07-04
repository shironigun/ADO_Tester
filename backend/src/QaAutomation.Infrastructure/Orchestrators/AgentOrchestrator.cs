using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
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
    private readonly ILogger<AgentOrchestrator> _log;

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory, IEventBus bus, PipelineConcurrency concurrency,
        CancellationRegistry cancellations, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        ILogger<AgentOrchestrator> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _concurrency = concurrency;
        _cancellations = cancellations;
        _ado = ado;
        _llmFactory = llmFactory;
        _log = log;
    }

    public bool Cancel(int sessionId) => _cancellations.Cancel(sessionId);

    // ── start (Analyst, then Tester if auto) ─────────────────────────────────

    public async Task<int> StartAsync(
        int projectId, int workItemId, bool autoHandoff = true, int? batchId = null, CancellationToken cancellationToken = default)
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

    public Task RunTesterAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => RerunTesterAsync(sessionId, token));
        return Task.CompletedTask;
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

            await UpdateActivityAsync(db, session, $"Analyst — analyzing story (using {context.Count} KB chunks)", ct);
            Pub(key, EventKinds.Phase, EventPhases.Analyst, $"Analyst started ({context.Count} chunks)");
            var analysis = await RunStreamingAsync(db, session, "Analyst — analyzing story",
                (p, c) => new AnalystAgent(llm).AnalyzeAsync(wi, context, p, EmitClosure(key, EventPhases.Analyst), c),
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
        Pub(key, EventKinds.Phase, EventPhases.Tester, "Tester started — generating test cases in QA style");
        var generated = await RunStreamingAsync(db, session, "Tester — generating test cases",
            (p, c) => new TesterAgent(llm).GenerateAsync(analysis, wi, p, EmitClosure(key, EventPhases.Tester), c),
            key, EventPhases.Tester, ct);

        // Clear any prior drafts (re-run) then insert the new ones — one transaction via SaveChanges.
        var existing = await db.TestCaseDrafts.Where(d => d.SessionId == session.Id).ToListAsync(ct);
        db.TestCaseDrafts.RemoveRange(existing);
        for (var i = 0; i < generated.Count; i++)
        {
            var g = generated[i];
            db.TestCaseDrafts.Add(new TestCaseDraft
            {
                SessionId = session.Id,
                Title = g.Title,
                Preconditions = g.Preconditions,
                StepsJson = JsonSerializer.Serialize(g.Steps.Select(st => new { action = st.Action, expected = st.Expected })),
                SortOrder = i,
            });
            Pub(key, EventKinds.Result, EventPhases.Tester, $"Test case {i + 1}/{generated.Count}: {g.Title}");
        }

        session.Status = SessionStatus.Drafted;
        session.CurrentActivity = $"Drafted {generated.Count} test case(s) — awaiting review";
        Pub(key, EventKinds.System, EventPhases.Tester, $"Drafted {generated.Count} test case(s) — awaiting review",
            EventLevels.Success, new() { ["terminal"] = true, ["count"] = generated.Count });
        PersistLog(session, key);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
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
