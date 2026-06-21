using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// Runs Analyst → Tester in the background. The pipeline takes long enough (LLM calls) that
/// we don't block the HTTP request — but background work needs its own DI scope because the
/// per-request scope (and its DbContext) dies as soon as the response is sent.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentOrchestrator> _logger;

    /// <summary>Cancellation tokens for in-flight pipelines, keyed by session ID.</summary>
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> _ctsRegistry = new();

    public AgentOrchestrator(IServiceScopeFactory scopeFactory, ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool Cancel(int sessionId)
    {
        if (_ctsRegistry.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public async Task<int> StartAsync(
        int projectId, int workItemId, bool autoHandoff = true, CancellationToken ct = default)
    {
        int sessionId;

        // Seed the session synchronously so the caller gets a real ID.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ado = scope.ServiceProvider.GetRequiredService<IAzureDevOpsClient>();

            var wi = await ado.GetWorkItemAsync(workItemId, ct);
            var session = new TestSession
            {
                ProjectId = projectId,
                WorkItemId = workItemId,
                WorkItemTitle = wi.Title,
                Status = SessionStatus.Analyzing,
                CurrentActivity = "Fetched work item from Azure DevOps",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.TestSessions.Add(session);
            await db.SaveChangesAsync(ct);
            sessionId = session.Id;
        }

        var cts = new CancellationTokenSource();
        _ctsRegistry[sessionId] = cts;

        // Fire and forget — fresh scope inside.
        _ = Task.Run(() => RunPipelineAsync(sessionId, projectId, workItemId, autoHandoff, cts.Token), cts.Token);

        return sessionId;
    }

    public Task RunTesterAsync(int sessionId, CancellationToken ct = default)
    {
        var cts = new CancellationTokenSource();
        _ctsRegistry[sessionId] = cts;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ado = scope.ServiceProvider.GetRequiredService<IAzureDevOpsClient>();
            var tester = scope.ServiceProvider.GetRequiredService<TesterAgent>();

            var session = await db.TestSessions.Include(s => s.Drafts)
                .FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (session is null) return;

            try
            {
                if (string.IsNullOrEmpty(session.AnalysisJson))
                    throw new InvalidOperationException("Analyst analysis missing — run Analyst first.");

                var analysis = JsonSerializer.Deserialize<AnalystOutput>(session.AnalysisJson)
                    ?? throw new InvalidOperationException("Invalid analysis JSON.");

                session.Status = SessionStatus.Drafting;
                session.CurrentActivity = "Tester — generating test cases in QA style";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);

                var wi = await ado.GetWorkItemAsync(session.WorkItemId, cts.Token);
                await RunTesterInternalAsync(db, tester, session, analysis, wi, cts.Token);
            }
            catch (OperationCanceledException)
            {
                session.Status = SessionStatus.Cancelled;
                session.CurrentActivity = "Cancelled by user";
                session.UpdatedAt = DateTime.UtcNow;
                try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual Tester failed for session {SessionId}", sessionId);
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                session.CurrentActivity = $"Failed: {ex.Message}";
                session.UpdatedAt = DateTime.UtcNow;
                try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
            }
            finally
            {
                _ctsRegistry.TryRemove(sessionId, out _);
            }
        }, cts.Token);

        return Task.CompletedTask;
    }

    public async Task PushToAdoAsync(
        int sessionId, int testPlanId, int testSuiteId, CancellationToken ct = default)
    {
        // Register a cancellation source linked to the HTTP request token so the UI's Cancel
        // endpoint can interrupt an in-flight push.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ctsRegistry[sessionId] = linkedCts;
        var pct = linkedCts.Token;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ado = scope.ServiceProvider.GetRequiredService<IAzureDevOpsClient>();

            var session = await db.TestSessions.Include(s => s.Drafts)
                .FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Session {sessionId} not found.");

            session.Status = SessionStatus.Pushing;
            session.CurrentActivity = "Push starting…";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            var approved = session.Drafts.Where(d => d.Status == DraftStatus.Approved).ToList();
            if (approved.Count == 0)
                throw new InvalidOperationException("No approved drafts to push.");

            try
            {
                for (int i = 0; i < approved.Count; i++)
                {
                    pct.ThrowIfCancellationRequested();
                    var draft = approved[i];

                    session.CurrentActivity = $"Pushing {i + 1}/{approved.Count}: creating Test Case";
                    session.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);

                    var steps = JsonSerializer.Deserialize<List<TestCaseStep>>(draft.StepsJson) ?? [];
                    var tcId = await ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, pct);

                    session.CurrentActivity = $"Pushing {i + 1}/{approved.Count}: adding TC #{tcId} to suite";
                    await db.SaveChangesAsync(CancellationToken.None);
                    await ado.AddTestCaseToSuiteAsync(testPlanId, testSuiteId, tcId, pct);

                    session.CurrentActivity = $"Pushing {i + 1}/{approved.Count}: linking TC #{tcId} to story";
                    await db.SaveChangesAsync(CancellationToken.None);
                    await ado.LinkTestedByAsync(session.WorkItemId, tcId, pct);

                    draft.AdoTestCaseId = tcId;
                    draft.Status = DraftStatus.Pushed;
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                session.Status = SessionStatus.Done;
                session.CurrentActivity = $"Pushed {approved.Count} test case(s) to ADO";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                session.Status = SessionStatus.Cancelled;
                session.CurrentActivity = $"Push cancelled — {approved.Count(d => d.Status == DraftStatus.Pushed)} of {approved.Count} pushed";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push failed for session {SessionId}", sessionId);
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                session.CurrentActivity = $"Push failed: {ex.Message}";
                session.UpdatedAt = DateTime.UtcNow;
                try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
            }
        }
        finally
        {
            _ctsRegistry.TryRemove(sessionId, out _);
        }
    }

    // ── Background pipeline ───────────────────────────────────────────────────

    private async Task RunPipelineAsync(
        int sessionId, int projectId, int workItemId, bool autoHandoff, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ado = scope.ServiceProvider.GetRequiredService<IAzureDevOpsClient>();
        var kb = scope.ServiceProvider.GetRequiredService<IKnowledgebaseService>();
        var analyst = scope.ServiceProvider.GetRequiredService<AnalystAgent>();
        var tester = scope.ServiceProvider.GetRequiredService<TesterAgent>();

        var session = await db.TestSessions.FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
        if (session is null)
        {
            _logger.LogError("Session {SessionId} disappeared.", sessionId);
            return;
        }

        async Task UpdateActivity(string activity)
        {
            session.CurrentActivity = activity;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
            _logger.LogInformation("Session {SessionId} using LLM provider: {Provider}", sessionId, llm.ProviderName);

            ct.ThrowIfCancellationRequested();
            await UpdateActivity("Re-fetching work item from Azure DevOps");
            var wi = await ado.GetWorkItemAsync(workItemId, ct);

            ct.ThrowIfCancellationRequested();
            await UpdateActivity("Retrieving relevant chunks from the domain knowledgebase");
            var query = $"{wi.Title} {wi.AcceptanceCriteria}".Trim();
            var context = await kb.SearchAsync(projectId, query, topK: 8, ct);

            ct.ThrowIfCancellationRequested();
            await UpdateActivity($"Analyst — analyzing story (using {context.Count} KB chunks)");
            var analysis = await analyst.AnalyzeAsync(wi, context, ct);
            session.AnalysisJson = JsonSerializer.Serialize(analysis);
            session.Status = autoHandoff ? SessionStatus.Drafting : SessionStatus.Drafted;
            session.CurrentActivity = autoHandoff
                ? "Analyst done — handing off to Tester"
                : "Analyst done — awaiting manual Tester trigger";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            if (autoHandoff)
            {
                ct.ThrowIfCancellationRequested();
                await UpdateActivity("Tester — generating test cases in QA style");
                await RunTesterInternalAsync(db, tester, session, analysis, wi, ct);
                await UpdateActivity($"Drafted {session.Drafts.Count} test case(s) — awaiting review");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pipeline cancelled for session {SessionId}", sessionId);
            session.Status = SessionStatus.Cancelled;
            session.CurrentActivity = "Cancelled by user";
            session.UpdatedAt = DateTime.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for session {SessionId}", sessionId);
            session.Status = SessionStatus.Failed;
            session.ErrorMessage = ex.Message;
            session.CurrentActivity = $"Failed: {ex.Message}";
            session.UpdatedAt = DateTime.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
        }
        finally
        {
            _ctsRegistry.TryRemove(sessionId, out _);
        }
    }

    private static async Task RunTesterInternalAsync(
        AppDbContext db, TesterAgent tester, TestSession session,
        AnalystOutput analysis, AdoWorkItem wi, CancellationToken ct = default)
    {
        var generated = await tester.GenerateAsync(analysis, wi, ct);

        // Re-load drafts to clear (in case this is a re-run)
        var existing = await db.TestCaseDrafts.Where(d => d.SessionId == session.Id).ToListAsync(ct);
        db.TestCaseDrafts.RemoveRange(existing);

        for (int i = 0; i < generated.Count; i++)
        {
            var g = generated[i];
            db.TestCaseDrafts.Add(new TestCaseDraft
            {
                SessionId = session.Id,
                Title = g.Title,
                Preconditions = g.Preconditions,
                StepsJson = JsonSerializer.Serialize(g.Steps),
                SortOrder = i,
            });
        }
        session.Status = SessionStatus.Drafted;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
