using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Find-Bugs pipeline (spec B5 — port of the Python <c>BugOrchestrator</c>). Singleton. Analyst →
/// park at AwaitingActual → anchored code-verify (pass-1 + optional per-step pass-2) → Bug Writer →
/// push. Same background/own-scope/cancellation pattern as <see cref="AgentOrchestrator"/>; the
/// agents are called directly (no progress pump) with the EventBus emit callback.
/// </summary>
public sealed class BugOrchestrator : IBugOrchestrator
{
    private const string KVerifyDetail = "find_bugs.verify_detail";        // case | step | step_full
    private const string KVerifyStepScope = "find_bugs.verify_step_scope"; // fails_only | all
    private const string KBugFromCaseMode = "find_bugs.bug_from_case_mode"; // instant | agent

    private const int MaxFiles = 20;
    private const int MaxBytesPerFile = 30_000;
    private const int MaxTotalBytes = 250_000;

    private static readonly IReadOnlySet<BugSessionStatus> TerminalStatuses =
        new HashSet<BugSessionStatus> { BugSessionStatus.Done, BugSessionStatus.Drafted, BugSessionStatus.Failed, BugSessionStatus.Cancelled };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly PipelineConcurrency _concurrency;
    private readonly CancellationRegistry _cancellations;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly ILogger<BugOrchestrator> _log;

    public BugOrchestrator(
        IServiceScopeFactory scopeFactory, IEventBus bus, PipelineConcurrency concurrency,
        CancellationRegistry cancellations, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        ILogger<BugOrchestrator> log)
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

    // ── public entry points ───────────────────────────────────────────────────

    public async Task<int> StartAsync(int projectId, int workItemId, bool auto = false, int? batchId = null, CancellationToken cancellationToken = default)
    {
        var wi = await _ado.GetWorkItemAsync(workItemId, ct: cancellationToken);
        int sessionId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var now = DateTime.UtcNow;
            var session = new BugSession
            {
                ProjectId = projectId,
                WorkItemId = workItemId,
                WorkItemTitle = wi.Title,
                Status = BugSessionStatus.Analyzing,
                CurrentActivity = "Fetched ticket from Azure DevOps",
                BatchId = batchId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BugSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            sessionId = session.Id;
        }
        Launch(sessionId, token => RunAnalystAsync(sessionId, projectId, workItemId, auto, token));
        return sessionId;
    }

    public Task RunBugWriterAsync(
        int sessionId, string actualBehavior, string? codeContext, bool verifyCode = false,
        IReadOnlyList<string>? commitIds = null, IReadOnlyList<string>? prIds = null,
        IReadOnlyList<string>? selectedTestCases = null, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => RunBugWriterInnerAsync(
            sessionId, actualBehavior, codeContext, commitIds ?? [], prIds ?? [], selectedTestCases ?? [], token));
        return Task.CompletedTask;
    }

    public Task PushToAdoAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, token => PushAsync(sessionId, token));
        return Task.CompletedTask;
    }

    private void Launch(int sessionId, Func<CancellationToken, Task> body)
    {
        var token = _cancellations.Register(sessionId).Token;
        _ = Task.Run(async () =>
        {
            try { await body(token); }
            finally { _cancellations.Remove(sessionId); }
        });
    }

    // ── analyst ─────────────────────────────────────────────────────────────

    private async Task RunAnalystAsync(int sessionId, int projectId, int workItemId, bool auto, CancellationToken ct)
    {
        try
        {
            await MarkQueuedAsync(sessionId);
            await _concurrency.Gate.WaitAsync(ct);
            try
            {
                await RunAnalystInnerAsync(sessionId, projectId, workItemId, auto, ct);
            }
            finally
            {
                _concurrency.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkCancelledAsync(sessionId);
        }
    }

    private async Task RunAnalystInnerAsync(int sessionId, int projectId, int workItemId, bool auto, CancellationToken ct)
    {
        var key = Key(sessionId);
        var autoContinue = false;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var kb = scope.ServiceProvider.GetRequiredService<IKnowledgebaseService>();
            var session = await db.BugSessions.FindAsync([sessionId], ct);
            if (session is null)
                return;

            try
            {
                var llm = await _llmFactory.CreateAsync(ct);
                _log.LogInformation("Bug session {SessionId} using LLM provider: {Provider}", sessionId, llm.ProviderName);
                Pub(key, EventKinds.System, text: $"Pipeline started · provider {llm.ProviderName}");

                await UpdateAsync(db, session, "Re-fetching ticket from Azure DevOps", ct);
                var wi = await _ado.GetWorkItemAsync(workItemId, ct: ct);
                Pub(key, EventKinds.Phase, EventPhases.Fetch, $"Fetched ticket #{wi.Id} — {wi.Title}",
                    EventLevels.Success, new() { ["workItemId"] = wi.Id, ["type"] = wi.WorkItemType });

                await UpdateAsync(db, session, "Retrieving relevant KB chunks", ct);
                var query = $"{wi.Title} {wi.AcceptanceCriteria ?? ""}".Trim();
                var context = await kb.SearchAsync(projectId, query, 8, ct);
                Pub(key, EventKinds.Result, EventPhases.Retrieve, $"Retrieved {context.Count} knowledgebase chunk(s)",
                    EventLevels.Success, new() { ["chunks"] = context.Count, ["files"] = context.Select(h => h.FileName).ToList() });

                await UpdateAsync(db, session, $"Analyst — analyzing story (using {context.Count} KB chunks)", ct);
                Pub(key, EventKinds.Phase, EventPhases.Analyst, $"Analyst started ({context.Count} chunks)");
                var analysis = await new AnalystAgent(llm).AnalyzeAsync(
                    wi, context, progress: null, emit: EmitClosure(key, EventPhases.Analyst), cancellationToken: ct);
                Pub(key, EventKinds.Result, EventPhases.Analyst,
                    $"Analyst produced: {analysis.Module} / {analysis.Feature} · " +
                    $"{analysis.AcceptanceCriteria.Count} ACs · {analysis.EdgeCases.Count} edge", EventLevels.Success);

                session.AnalysisJson = analysis.ToJson();
                if (auto)
                {
                    session.CurrentActivity = "Analyst done — verifying against the code…";
                    session.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    autoContinue = true;
                }
                else
                {
                    session.Status = BugSessionStatus.AwaitingActual;
                    session.CurrentActivity = "Analyst done — paste observed actual behavior to continue";
                    Pub(key, EventKinds.System, EventPhases.Analyst, "Analyst done — awaiting observed actual behavior", data: Terminal());
                    PersistLog(session, key);
                    session.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await FailAsCancelledAsync(db, session, key);
                throw;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Analyst (bug pipeline) failed for session {SessionId}", sessionId);
                await FailAsync(db, session, key, e);
            }
        }

        // AUTO (Epic run): the analyst session is committed/closed; continue into the rest of the
        // pipeline in this same task/slot. RunBugWriterInner owns its own scope + fail/cancel handling.
        if (autoContinue)
            await RunBugWriterInnerAsync(sessionId, "", null, [], [], [], ct);
    }

    // ── bug writer (code-verify + draft) ───────────────────────────────────────

    private async Task RunBugWriterInnerAsync(
        int sessionId, string actualBehavior, string? codeContext,
        IReadOnlyList<string> commitIds, IReadOnlyList<string> prIds, IReadOnlyList<string> selectedTestCases, CancellationToken ct)
    {
        var key = Key(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.BugSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);
            if (string.IsNullOrEmpty(session.AnalysisJson))
                throw new InvalidOperationException("Analyst output missing — Analyst must complete first.");
            var analysis = AnalystOutput.FromJson(JsonNode.Parse(session.AnalysisJson));
            var wi = await _ado.GetWorkItemAsync(session.WorkItemId, ct: ct);

            session.ActualBehavior = actualBehavior;
            session.CodeContext = codeContext;

            // ── Code-analysis phase (always on) — anchored on the change ──
            session.Status = BugSessionStatus.Verifying;
            session.CurrentActivity = "Anchoring on the change (linked PRs/commits)…";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            Pub(key, EventKinds.Phase, EventPhases.Discover, "Anchoring on the change — linked PRs/commits, then only relevant code");
            var (diffSummary, repoCode) = await DiscoverAnchoredContextAsync(session.WorkItemId, wi, analysis, commitIds, prIds, llm, key, ct);
            var verifyContext = diffSummary.Length > 0
                ? $"## CHANGE UNDER TEST\n{diffSummary}\n\n## CODE (anchor + relevant dependencies/callers)\n{repoCode}"
                : repoCode;

            var sourced = await LoadTestCasesAsync(db, session.WorkItemId, ct);
            if (selectedTestCases.Count > 0)
            {
                var sel = selectedTestCases.ToHashSet();
                var chosen = sourced.Where(t => sel.Contains(t.Key)).ToList();
                if (chosen.Count > 0)
                {
                    if (chosen.Count < sourced.Count)
                        Pub(key, EventKinds.System, EventPhases.CodeVerify, $"Verifying {chosen.Count} of {sourced.Count} test case(s) you selected");
                    sourced = chosen;
                }
            }
            var testCases = sourced.Select(t => t.Input).ToList();
            var tcMeta = new Dictionary<string, (string Source, int? AdoId, IReadOnlyList<TestCaseStep> Steps)>();
            foreach (var t in sourced)
                tcMeta[t.Input.Title.Trim().ToLowerInvariant()] = (t.Source, t.AdoTestCaseId, t.Input.Steps);
            var nPlan = sourced.Count(t => t.Source == "plan");
            var nDraft = sourced.Count - nPlan;
            Pub(key, EventKinds.Phase, EventPhases.CodeVerify,
                $"Verifying {testCases.Count} test case(s) ({nPlan} from Test Plan, {nDraft} draft) against the change");

            var verdicts = await new CodeVerifierAgent(llm).VerifyAsync(
                wi, analysis, testCases, verifyContext,
                manualDescription: string.IsNullOrEmpty(actualBehavior) ? null : actualBehavior,
                emit: EmitClosure(key, EventPhases.CodeVerify), cancellationToken: ct);

            var verdictDicts = new List<Dictionary<string, object?>>();
            foreach (var v in verdicts)
            {
                var vd = new Dictionary<string, object?>
                {
                    ["testCase"] = v.TestCase,
                    ["status"] = v.Status,
                    ["evidence"] = v.Evidence,
                    ["reasoning"] = v.Reasoning,
                };
                if (tcMeta.TryGetValue(v.TestCase.Trim().ToLowerInvariant(), out var meta))
                {
                    vd["source"] = meta.Source;
                    if (meta.AdoId is { } id) vd["adoTestCaseId"] = id;
                    if (meta.Steps.Count > 0) vd["steps"] = StepsToDicts(meta.Steps);
                }
                verdictDicts.Add(vd);
            }
            session.CodeVerificationJson = JsonSerializer.Serialize(verdictDicts);
            foreach (var v in verdicts)
            {
                var lvl = v.Status == "fail" ? EventLevels.Error : v.Status == "pass" ? EventLevels.Success : EventLevels.Warn;
                Pub(key, EventKinds.Result, EventPhases.CodeVerify, $"{v.Status.ToUpperInvariant()}: {v.TestCase} — {v.Evidence}", lvl);
            }

            // ── Pass 2 (settings-driven): per-step verdicts ──
            var detail = await GetSettingAsync(db, KVerifyDetail, "step");
            var scopeSetting = await GetSettingAsync(db, KVerifyStepScope, "fails_only");
            if (detail is "step" or "step_full")
            {
                // Last-wins (NOT ToDictionary, which throws): plan + draft titles can normalize to the same key.
                var byTitle = new Dictionary<string, TestCaseInput>(StringComparer.Ordinal);
                foreach (var t in sourced)
                    byTitle[t.Input.Title.Trim().ToLowerInvariant()] = t.Input;
                var targets = new List<(int Index, TestCaseInput Case)>();
                for (var idx = 0; idx < verdictDicts.Count; idx++)
                {
                    var status = verdictDicts[idx].TryGetValue("status", out var s) ? s as string : null;
                    if (scopeSetting != "all" && status is not ("fail" or "inconclusive"))
                        continue;
                    var title = (verdictDicts[idx].TryGetValue("testCase", out var tcv) ? tcv as string : "") ?? "";
                    if (byTitle.TryGetValue(title.Trim().ToLowerInvariant(), out var c) && c.Steps.Count > 0)
                        targets.Add((idx, c));
                }
                if (targets.Count > 0)
                {
                    var full = detail == "step_full";
                    const int batch = 6;
                    var total = targets.Count;
                    var done = 0;
                    Pub(key, EventKinds.Phase, EventPhases.CodeVerify, $"Step-level verification of {total} case(s) ({detail}, {scopeSetting})");
                    for (var start = 0; start < total; start += batch)
                    {
                        var chunk = targets.Skip(start).Take(batch).ToList();
                        session.CurrentActivity = $"Verifying steps — {done}/{total} case(s)";
                        session.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        var stepLists = await new CodeVerifierAgent(llm).VerifyStepsAsync(
                            wi, analysis, chunk.Select(c => c.Case).ToList(), verifyContext, full,
                            emit: EmitClosure(key, EventPhases.CodeVerify), cancellationToken: ct);
                        for (var k = 0; k < chunk.Count; k++)
                        {
                            var svs = k < stepLists.Count ? stepLists[k] : [];
                            verdictDicts[chunk[k].Index]["steps"] = svs.Select(StepVerdictToDict).ToList();
                            var failIndex = svs.FindIndex(sv => sv.Status == "fail");
                            verdictDicts[chunk[k].Index]["failedStepIndex"] = failIndex >= 0 ? failIndex : null;
                            // Reconcile case-level status with the per-step verdicts.
                            if (svs.Any(sv => sv.Status == "fail")) verdictDicts[chunk[k].Index]["status"] = "fail";
                            else if (svs.Any(sv => sv.Status == "inconclusive")) verdictDicts[chunk[k].Index]["status"] = "inconclusive";
                            else if (svs.Count > 0) verdictDicts[chunk[k].Index]["status"] = "pass";
                        }
                        done += chunk.Count;
                        session.CodeVerificationJson = JsonSerializer.Serialize(verdictDicts);
                        session.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                    Pub(key, EventKinds.Result, EventPhases.CodeVerify, $"Step-level verification complete ({total} case(s))", EventLevels.Success);
                }
            }

            var fails = verdicts.Where(v => v.Status == "fail").ToList();
            var verifySummary = fails.Count > 0
                ? "Code-verification failures:\n" + string.Join("\n", fails.Select(v => $"- {v.TestCase}: {v.Evidence} ({v.Reasoning})"))
                : "";
            if (repoCode.Length > 0)
            {
                codeContext = Slice((codeContext ?? "") + "\n\n" + repoCode, 120_000);
                session.CodeContext = codeContext;
            }

            var combinedActual = string.Join("\n\n", new[] { (actualBehavior ?? "").Trim(), verifySummary }.Where(p => p.Length > 0)).Trim();
            if (combinedActual.Length == 0)
                combinedActual = "No discrepancies detected by static code analysis.";

            session.Status = BugSessionStatus.Drafting;
            session.CurrentActivity = "Bug Writer — comparing expected vs actual";
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, EventPhases.BugWriter, "Bug Writer started — comparing expected vs actual");

            var generated = await new BugWriterAgent(llm).GenerateAsync(
                analysis, wi, combinedActual, codeContext, emit: EmitClosure(key, EventPhases.BugWriter), cancellationToken: ct);

            var existingDrafts = await db.BugDrafts.Where(d => d.SessionId == sessionId).ToListAsync(ct);
            db.BugDrafts.RemoveRange(existingDrafts);
            for (var i = 0; i < generated.Count; i++)
            {
                var g = generated[i];
                db.BugDrafts.Add(new BugDraft
                {
                    SessionId = sessionId,
                    Title = g.Title,
                    Severity = BugHelpers.ParseSeverity(g.Severity),
                    Preconditions = g.Preconditions,
                    ReproStepsJson = JsonSerializer.Serialize(g.ReproSteps),
                    ExpectedBehavior = g.Expected,
                    ActualBehavior = g.Actual,
                    SuggestedFix = g.SuggestedFix,
                    SortOrder = i,
                });
                Pub(key, EventKinds.Result, EventPhases.BugWriter, $"Bug {i + 1}/{generated.Count} [{g.Severity}]: {g.Title}");
            }

            session.Status = BugSessionStatus.Drafted;
            session.CurrentActivity = $"Drafted {generated.Count} bug(s) — awaiting review";
            Pub(key, EventKinds.System, EventPhases.BugWriter, $"Drafted {generated.Count} bug(s) — awaiting review",
                EventLevels.Success, new() { ["terminal"] = true, ["count"] = generated.Count });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailAsCancelledAsync(db, session, key);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Bug Writer failed for session {SessionId}", sessionId);
            await FailAsync(db, session, key, e);
        }
    }

    // ── code-analysis helpers ───────────────────────────────────────────────

    private async Task<(string DiffSummary, string Code)> DiscoverAnchoredContextAsync(
        int workItemId, AdoWorkItem wi, AnalystOutput analysis,
        IReadOnlyList<string> commitIds, IReadOnlyList<string> prIds, ILlmClient llm, string key, CancellationToken ct)
    {
        string repoId;
        try { repoId = await _ado.ResolveRepositoryIdAsync(ct: ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"Repo resolve failed: {e.Message}", EventLevels.Warn); return ("", ""); }

        var changed = new List<ChangedFile>();
        bool Have(string p) => changed.Any(c => c.Path == p);
        void AddChanged(IEnumerable<ChangedFile> items) { foreach (var c in items) if (!Have(c.Path)) changed.Add(c); }

        // 1. Anchor on linked + manual PRs/commits.
        IReadOnlyList<string> autoPr = [], autoCommit = [];
        try { var refs = await _ado.GetWorkItemGitRefsAsync(workItemId, ct); autoPr = refs.PullRequestIds; autoCommit = refs.CommitShas; }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        foreach (var pr in autoPr.Concat(prIds).Distinct())
        {
            try { AddChanged(await _ado.GetPullRequestDiffAsync(pr, repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"PR #{pr}: {changed.Count} changed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"PR #{pr} skipped: {e.Message}", EventLevels.Warn); }
        }
        foreach (var sha in autoCommit.Concat(commitIds).Distinct())
        {
            try { AddChanged(await _ado.GetCommitDiffAsync(sha, repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"commit {Short(sha)}: {changed.Count} changed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"commit {Short(sha)} skipped: {e.Message}", EventLevels.Warn); }
        }

        // 2. No linked refs → search + recent commits.
        if (changed.Count == 0)
        {
            var terms = BugHelpers.SearchQuery(analysis.Feature, wi.Title, analysis.Module);
            Pub(key, EventKinds.Phase, EventPhases.Discover, $"No linked PRs/commits — searching for '{terms}'");
            IReadOnlyList<string> seeds = [];
            try { seeds = terms.Length > 0 ? await _ado.SearchCodeAsync(terms, 8, ct: ct) : []; Pub(key, EventKinds.Result, EventPhases.Discover, $"Code Search → {seeds.Count} seed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"Code Search unavailable: {e.Message}", EventLevels.Warn); }
            foreach (var seed in seeds.Take(2))
            {
                try
                {
                    var shas = await _ado.GetCommitsForPathAsync(seed, 1, repoId, ct);
                    if (shas.Count > 0) { AddChanged(await _ado.GetCommitDiffAsync(shas[0], repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"recent commit {Short(shas[0])} on {seed}"); }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            foreach (var sp in seeds)
                if (!Have(sp)) changed.Add(new ChangedFile(sp, "seed"));
        }

        if (changed.Count > MaxFiles)
            changed = changed.Take(MaxFiles).ToList();
        if (changed.Count == 0)
        {
            Pub(key, EventKinds.System, EventPhases.Discover, "No code anchor found — verifying against expected behavior only", EventLevels.Warn);
            return ("", "");
        }

        var diffSummary = "Files changed (anchor):\n" + string.Join("\n", changed.Select(c => $"- [{(string.IsNullOrEmpty(c.ChangeType) ? "edit" : c.ChangeType)}] {c.Path}"));

        // 3. Read changed files; collect dependency candidates from their imports.
        var contents = new Dictionary<string, string>();
        var importCands = new List<string>();
        foreach (var c in changed)
        {
            if (c.ChangeType == "delete") continue;
            string txt;
            try { txt = await _ado.GetFileContentAsync(c.Path, ct: ct); } catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            if (string.IsNullOrEmpty(txt)) continue;
            contents[c.Path] = Slice(txt, MaxBytesPerFile);
            foreach (var spec in BugHelpers.ExtractRelativeImports(txt))
                foreach (var cand in BugHelpers.ResolveImport(spec, c.Path))
                    if (!importCands.Contains(cand)) importCands.Add(cand);
        }

        // 4. Callers (regression surface): symbol search on each changed file's base name.
        var callerCands = new List<string>();
        foreach (var path in contents.Keys.Take(5).ToList())
        {
            var fname = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var symbol = fname.Contains('.') ? fname[..fname.LastIndexOf('.')] : fname;
            try
            {
                foreach (var h in await _ado.SearchCodeAsync(symbol, 6, ct: ct))
                    if (!contents.ContainsKey(h) && !callerCands.Contains(h)) callerCands.Add(h);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { break; }
        }

        // 5. Triage: one LLM pass picks the extra files actually needed.
        var candidateLines = importCands.Take(30).Select(p => $"[dependency] {p}")
            .Concat(callerCands.Take(30).Select(p => $"[caller] {p}")).ToList();
        var roleOf = new Dictionary<string, string>();
        foreach (var p in importCands) roleOf[p] = "dependency";
        foreach (var p in callerCands) roleOf[p] = "caller";
        var selectedExtra = candidateLines.Count > 0 ? await TriageFilesAsync(llm, diffSummary, analysis, candidateLines, key, ct) : [];

        // 6. Assemble role-tagged block within byte budget.
        var blocks = contents.Select(kv => $"=== [CHANGED] {kv.Key} ===\n{kv.Value}").ToList();
        var total = contents.Values.Sum(t => t.Length);
        var fetched = new HashSet<string>(contents.Keys);
        foreach (var path in selectedExtra)
        {
            if (total >= MaxTotalBytes || fetched.Contains(path)) continue;
            string txt;
            try { txt = await _ado.GetFileContentAsync(path, ct: ct); } catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            if (string.IsNullOrEmpty(txt)) continue;
            var snippet = Slice(txt, MaxBytesPerFile);
            blocks.Add($"=== [{(roleOf.TryGetValue(path, out var r) ? r : "related").ToUpperInvariant()}] {path} ===\n{snippet}");
            total += snippet.Length;
            fetched.Add(path);
        }

        Pub(key, EventKinds.Result, EventPhases.Discover,
            $"Anchored on {contents.Count} changed file(s); expanded to {fetched.Count} total · {total:N0} chars",
            EventLevels.Success, new() { ["files"] = fetched.OrderBy(x => x, StringComparer.Ordinal).ToList() });
        return (diffSummary, string.Join("\n\n", blocks));
    }

    private async Task<List<string>> TriageFilesAsync(
        ILlmClient llm, string diffSummary, AnalystOutput analysis, List<string> candidateLines, string key, CancellationToken ct)
    {
        const string system =
            "You are a senior engineer scoping a REGRESSION review. Given the change under test " +
            "and candidate files (dependencies the change imports, and callers that reference it), " +
            "pick ONLY the files needed to judge whether the change breaks the expected behavior. " +
            "Prefer callers and direct dependencies; ignore clearly unrelated files. " +
            "Return ONLY a JSON array of file path strings (max 10), no prose.";
        var user =
            $"## Change under test\n{diffSummary}\n\n" +
            $"## Feature\n{analysis.Module} / {analysis.Feature}\n\n" +
            "## Candidate files (role-tagged)\n" + string.Join("\n", candidateLines) +
            "\n\nReturn the JSON array of paths to read.";
        try
        {
            var raw = await llm.CompleteAsync(system, user, cancellationToken: ct);
            if (JsonNode.Parse(JsonUtils.StripFences(raw, '[')) is JsonArray arr)
            {
                var picked = arr.Where(x => x?.GetValueKind() == JsonValueKind.String).Select(x => x!.GetValue<string>()).Take(10).ToList();
                Pub(key, EventKinds.Result, EventPhases.Triage, $"Triage selected {picked.Count} extra file(s) to read");
                return picked;
            }
        }
        catch (Exception e)
        {
            Pub(key, EventKinds.System, EventPhases.Triage, $"Triage skipped ({e.Message}) — using deterministic picks", EventLevels.Warn);
        }
        return candidateLines.Select(ln => ln.Split("] ", 2)[^1]).Take(8).ToList();
    }

    private async Task<List<SourcedTestCase>> LoadTestCasesAsync(QaDbContext db, int workItemId, CancellationToken ct)
    {
        var sourced = new List<SourcedTestCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. ADO-linked Test Cases (preferred).
        IReadOnlyList<LinkedTestCase> linked;
        try { linked = await _ado.GetLinkedTestCasesAsync(workItemId, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { _log.LogInformation("Linked test cases unavailable for #{Id}: {Err}", workItemId, e.Message); linked = []; }
        foreach (var tc in linked)
        {
            sourced.Add(new SourcedTestCase(
                new TestCaseInput { Title = tc.Title, Preconditions = tc.Preconditions, Steps = tc.Steps.ToList() },
                "plan", tc.Id, $"plan#{tc.Id}"));
            seen.Add(tc.Title.Trim().ToLowerInvariant());
        }

        // 2. Latest Author-Tests session drafts not already linked in ADO.
        var ts = await db.TestSessions
            .Include(t => t.Drafts)
            .Where(t => t.WorkItemId == workItemId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (ts is not null)
        {
            foreach (var d in ts.Drafts.OrderBy(x => x.SortOrder))
            {
                if (seen.Contains((d.Title ?? "").Trim().ToLowerInvariant())) continue;
                sourced.Add(new SourcedTestCase(
                    new TestCaseInput { Title = d.Title ?? "", Preconditions = d.Preconditions ?? "", Steps = ParseSteps(d.StepsJson) },
                    "draft", d.AdoTestCaseId, $"draft#{d.Id}"));
            }
        }
        return sourced;
    }

    public async Task<IReadOnlyList<AttachedTestCase>> ListTestCasesAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.BugSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
            return [];
        var sourced = await LoadTestCasesAsync(db, session.WorkItemId, cancellationToken);
        return sourced.Select(t => new AttachedTestCase(
            t.Key, t.Input.Title, t.Input.Preconditions, t.Source, t.AdoTestCaseId, t.Input.Steps)).ToList();
    }

    public async Task<int> CreateBugFromVerdictAsync(int sessionId, JsonObject verdict, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var mode = VStr(verdict, "mode").ToLowerInvariant();
        if (mode is not ("instant" or "agent"))
            mode = await GetSettingAsync(db, KBugFromCaseMode, "instant");

        var title = VStr(verdict, "test_case").Trim();
        if (title.Length == 0) title = "Untitled test case";
        var steps = (verdict["steps"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        int? fidx = verdict["failed_step_index"]?.GetValueKind() == JsonValueKind.Number ? verdict["failed_step_index"]!.GetValue<int>() : null;
        if (fidx is null or < 0 || fidx >= steps.Count)
        {
            fidx = IndexOfStatus(steps, "fail");
            if (fidx is null) fidx = IndexOfStatus(steps, "inconclusive");
        }
        var evidence = VStr(verdict, "evidence");
        var reasoning = VStr(verdict, "reasoning");

        var session = await db.BugSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
            throw new ArgumentException("Session not found.");
        if (session.Status is BugSessionStatus.Analyzing or BugSessionStatus.Verifying or BugSessionStatus.Drafting or BugSessionStatus.Pushing)
            throw new InvalidOperationException("The pipeline is still running — wait for it to finish before adding a bug.");

        var cut = fidx is { } fi ? fi + 1 : steps.Count;
        var repro = steps.Take(cut)
            .Where(st => VStr(st, "action").Length > 0 || VStr(st, "expected").Length > 0)
            .Select(st => (VStr(st, "action").Length > 0 ? VStr(st, "action") : VStr(st, "expected")).Trim())
            .ToList();
        var failStep = fidx is { } f ? steps[f] : (steps.Count > 0 ? steps[^1] : null);
        var expected = failStep is null ? "" : VStr(failStep, "expected");
        var actualText = failStep is null ? "" : VStr(failStep, "actual");
        if (actualText.Length == 0) actualText = evidence.Length > 0 ? evidence : reasoning;

        var severity = BugHelpers.ParseSeverity(VStr(verdict, "severity") is { Length: > 0 } sv ? sv : "Medium");
        var preconditions = "";
        var reproJson = JsonSerializer.Serialize(repro);
        var suggestedFix = "";

        if (mode == "agent")
        {
            try
            {
                var llm = await _llmFactory.CreateAsync(cancellationToken);
                var wi = await _ado.GetWorkItemAsync(session.WorkItemId, ct: cancellationToken);
                var analysis = string.IsNullOrEmpty(session.AnalysisJson) ? new AnalystOutput() : AnalystOutput.FromJson(JsonNode.Parse(session.AnalysisJson));
                var summary = BugHelpers.VerdictActualSummary(title, repro, expected, actualText);
                var generated = await new BugWriterAgent(llm).GenerateAsync(analysis, wi, summary, session.CodeContext, cancellationToken: cancellationToken);
                if (generated.Count > 0)
                {
                    var g = generated[0];
                    title = g.Title.Length > 0 ? g.Title : title;
                    severity = BugHelpers.ParseSeverity(g.Severity);
                    preconditions = g.Preconditions;
                    reproJson = JsonSerializer.Serialize(g.ReproSteps);
                    expected = g.Expected.Length > 0 ? g.Expected : expected;
                    actualText = g.Actual.Length > 0 ? g.Actual : actualText;
                    suggestedFix = g.SuggestedFix;
                }
            }
            catch (Exception e)
            {
                _log.LogError(e, "Bug-from-verdict agent mode failed for {SessionId}; using instant pre-fill", sessionId);
            }
        }

        var existing = await db.BugDrafts.Where(d => d.SessionId == sessionId).Select(d => d.SortOrder).ToListAsync(cancellationToken);
        var nextOrder = existing.Count > 0 ? existing.Max() + 1 : 0;

        var draft = new BugDraft
        {
            SessionId = sessionId,
            SortOrder = nextOrder,
            Status = BugDraftStatus.Draft,
            Title = title,
            Severity = severity,
            Preconditions = preconditions,
            ReproStepsJson = reproJson,
            ExpectedBehavior = expected,
            ActualBehavior = actualText,
            SuggestedFix = suggestedFix,
        };
        db.BugDrafts.Add(draft);
        if (session.Status is BugSessionStatus.Drafted or BugSessionStatus.Done or BugSessionStatus.AwaitingActual or BugSessionStatus.Failed or BugSessionStatus.Cancelled)
        {
            session.Status = BugSessionStatus.Drafted;
            session.CurrentActivity = "Added a bug from a failed test case — awaiting review";
        }
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return draft.Id;
    }

    // ── push ─────────────────────────────────────────────────────────────────

    private async Task PushAsync(int sessionId, CancellationToken ct)
    {
        var key = Key(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.BugSessions.FindAsync([sessionId], ct);
        if (session is null)
            return;

        session.Status = BugSessionStatus.Pushing;
        session.CurrentActivity = "Push starting…";
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, EventPhases.Push, "Push to ADO started");

        var drafts = await db.BugDrafts
            .Where(d => d.SessionId == sessionId && d.Status == BugDraftStatus.Approved)
            .OrderBy(d => d.SortOrder)
            .ToListAsync(ct);

        if (drafts.Count == 0)
        {
            session.Status = BugSessionStatus.Failed;
            session.ErrorMessage = "No approved bug drafts to push.";
            session.CurrentActivity = "Push failed: no approved bug drafts";
            Pub(key, EventKinds.Error, EventPhases.Push, "Push failed: no approved bug drafts", EventLevels.Error, Terminal());
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
                session.CurrentActivity = $"Pushing {n}/{drafts.Count}: creating Bug";
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, "push", EventPhases.Push, $"{n}/{drafts.Count}: creating Bug — {draft.Title}");

                var reproSteps = ParseStringList(draft.ReproStepsJson);
                var bugId = await _ado.CreateBugAsync(
                    draft.Title, draft.Severity.ToString(), draft.Preconditions, reproSteps,
                    draft.ExpectedBehavior, draft.ActualBehavior, draft.SuggestedFix, ct: ct);

                session.CurrentActivity = $"Pushing {n}/{drafts.Count}: linking Bug #{bugId} to story";
                await db.SaveChangesAsync(ct);
                await _ado.LinkRelatedAsync(bugId, session.WorkItemId, ct);

                draft.AdoBugId = bugId;
                draft.Status = BugDraftStatus.Pushed;
                pushed++;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Result, EventPhases.Push, $"Bug #{bugId} pushed → linked to story", EventLevels.Success, new() { ["bugId"] = bugId });
            }

            session.Status = BugSessionStatus.Done;
            session.CurrentActivity = $"Pushed {pushed} bug(s) to ADO";
            Pub(key, EventKinds.System, EventPhases.Push, $"Pushed {pushed} bug(s) to ADO", EventLevels.Success, new() { ["terminal"] = true, ["count"] = pushed });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var pushedCount = drafts.Count(d => d.Status == BugDraftStatus.Pushed);
            session.Status = BugSessionStatus.Cancelled;
            session.CurrentActivity = $"Push cancelled — {pushedCount} of {drafts.Count} pushed";
            Pub(key, EventKinds.System, EventPhases.Push, session.CurrentActivity, EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Bug push failed for session {SessionId}", sessionId);
            session.Status = BugSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            session.CurrentActivity = $"Push failed: {e.Message}";
            Pub(key, EventKinds.Error, EventPhases.Push, $"Push failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    private sealed record SourcedTestCase(TestCaseInput Input, string Source, int? AdoTestCaseId, string Key);

    private async Task FailAsCancelledAsync(QaDbContext db, BugSession session, string key)
    {
        session.Status = BugSessionStatus.Cancelled;
        session.CurrentActivity = "Cancelled by user";
        Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
        PersistLog(session, key);
        session.UpdatedAt = DateTime.UtcNow;
        await SafeCommitAsync(db);
    }

    private async Task FailAsync(QaDbContext db, BugSession session, string key, Exception e)
    {
        session.Status = BugSessionStatus.Failed;
        session.ErrorMessage = e.Message;
        session.CurrentActivity = $"Failed: {e.Message}";
        Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
        PersistLog(session, key);
        session.UpdatedAt = DateTime.UtcNow;
        await SafeCommitAsync(db);
    }

    private async Task MarkQueuedAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.BugSessions.FindAsync(sessionId);
            if (row is not null) { row.CurrentActivity = PipelineConcurrency.QueuedActivity; row.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
        }
        catch { /* best-effort */ }
    }

    private async Task MarkCancelledAsync(int sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await db.BugSessions.FindAsync(sessionId);
            if (row is not null && !TerminalStatuses.Contains(row.Status))
            {
                row.Status = BugSessionStatus.Cancelled;
                row.CurrentActivity = "Cancelled by user";
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort */ }
    }

    private static async Task<string> GetSettingAsync(QaDbContext db, string key, string fallback)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        var value = row?.Value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static List<TestCaseStep> ParseSteps(string? stepsJson)
    {
        var steps = new List<TestCaseStep>();
        if (string.IsNullOrEmpty(stepsJson)) return steps;
        if (JsonNode.Parse(stepsJson) is JsonArray arr)
            foreach (var x in arr)
                if (x is JsonObject xo)
                    steps.Add(new TestCaseStep { Action = JsonUtils.StrOr(xo, "action", "Action"), Expected = JsonUtils.StrOr(xo, "expected", "Expected") });
        return steps;
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        if (JsonNode.Parse(json) is JsonArray arr)
            return arr.Select(x => x?.ToString() ?? "").ToList();
        return [];
    }

    private static List<Dictionary<string, object?>> StepsToDicts(IReadOnlyList<TestCaseStep> steps)
        => steps.Select(s => new Dictionary<string, object?> { ["action"] = s.Action, ["expected"] = s.Expected }).ToList();

    private static Dictionary<string, object?> StepVerdictToDict(StepVerdict s)
        => new() { ["action"] = s.Action, ["expected"] = s.Expected, ["status"] = s.Status, ["actual"] = s.Actual };

    private static int? IndexOfStatus(List<JsonObject> steps, string status)
    {
        for (var i = 0; i < steps.Count; i++)
            if (VStr(steps[i], "status").ToLowerInvariant() == status) return i;
        return null;
    }

    private static string VStr(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is not null && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static string Slice(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    private Action<LlmEmit> EmitClosure(string key, string phase)
        => emit => _bus.Publish(key, new ConsoleEvent { Kind = emit.Kind, Text = emit.Text, Phase = phase });

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private void PersistLog(BugSession session, string key)
    {
        try { session.EventLogJson = JsonSerializer.Serialize(_bus.Narrative(key)); }
        catch { /* never block on log persistence */ }
    }

    private static async Task UpdateAsync(QaDbContext db, BugSession session, string activity, CancellationToken ct)
    {
        session.CurrentActivity = activity;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task SafeCommitAsync(QaDbContext db)
    {
        try { await db.SaveChangesAsync(); } catch { /* best-effort teardown commit */ }
    }

    private static Dictionary<string, object?> Terminal() => new() { ["terminal"] = true };

    private static string Key(int sessionId) => $"bug:{sessionId}";
}
