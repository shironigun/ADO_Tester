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
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.QaLoop;

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

    /// <summary>
    /// camelCase + case-insensitive — UiAnalysisJson is rendered by the SPA (camelCase) and may
    /// also contain legacy PascalCase rows written before this options set was used.
    /// </summary>
    private static readonly JsonSerializerOptions UiAnalysisJsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _bus;
    private readonly PipelineConcurrency _concurrency;
    private readonly CancellationRegistry _cancellations;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILlmClientFactory _llmFactory;
    private readonly IPlaywrightRunner _playwrightRunner;
    private readonly IScriptPathAdapter _scriptPaths;
    private readonly string _scriptsDir;
    private readonly string _playwrightRoot;
    private readonly int _uiScriptGenTimeoutSeconds;
    private readonly ILogger<QaLoopOrchestrator> _log;

    public QaLoopOrchestrator(
        IServiceScopeFactory scopeFactory, IEventBus bus, PipelineConcurrency concurrency,
        CancellationRegistry cancellations, IAzureDevOpsClient ado, ILlmClientFactory llmFactory,
        IPlaywrightRunner playwrightRunner, IScriptPathAdapter scriptPaths,
        IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop,
        ILogger<QaLoopOrchestrator> log)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _concurrency = concurrency;
        _cancellations = cancellations;
        _ado = ado;
        _llmFactory = llmFactory;
        _playwrightRunner = playwrightRunner;
        _scriptPaths = scriptPaths;
        _scriptsDir = Path.GetFullPath(Path.Combine(
            storage.Value.DataDirectory, qaLoop.Value.ScriptsFolderName));
        _playwrightRoot = qaLoop.Value.PlaywrightRoot ?? "";
        _uiScriptGenTimeoutSeconds = Math.Max(1, qaLoop.Value.UiScriptGenerationTimeoutSeconds);
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

    // ── QLV2-106: RerunAnalyze ───────────────────────────────────────────────

    public Task RerunAnalyzeAsync(int sessionId, string feedback, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RerunAnalyzeInnerAsync(sessionId, feedback, ct));
        return Task.CompletedTask;
    }

    private async Task RerunAnalyzeInnerAsync(int sessionId, string feedback, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);

            var priorInventory = session.InventoryJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<InventoryDoc>(session.InventoryJson)
                  ?? throw new InvalidOperationException("InventoryJson deserialization returned null.")
                : throw new InvalidOperationException("InventoryJson missing — run Analyze first.");
            var priorSurface = session.AnalysisJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<SurfaceMatrix>(session.AnalysisJson)
                  ?? throw new InvalidOperationException("AnalysisJson deserialization returned null.")
                : throw new InvalidOperationException("AnalysisJson missing — run Analyze first.");

            session.Status = ApiSessionStatus.Analyzing;
            session.LastFeedback = feedback;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            Pub(key, EventKinds.Phase, EventPhases.Analyze, "Module analyzer refine started");

            var existingCases = await _ado.GetLinkedTestCasesAsync(session.WorkItemId ?? 0, ct);
            var analysis = await new ModuleAnalyzerAgent(llm).RefineInventoryAsync(
                session.Module, priorInventory, priorSurface, feedback, existingCases,
                additionalContext: FormatScanContext(session),
                emit: EmitClosure(key, EventPhases.Analyze, session.ActivityEnabled), ct: ct);

            // Re-apply gap-selected default
            foreach (var row in analysis.Surface.Rows)
                row.Selected = row.Status == "gap";

            session.InventoryJson = JsonSerializer.Serialize(analysis.Inventory);
            session.AnalysisJson = JsonSerializer.Serialize(analysis.Surface);
            session.Status = ApiSessionStatus.AwaitingReview;
            Pub(key, EventKinds.System, EventPhases.Analyze,
                "Analyzer refine done — awaiting surface review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
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
            _log.LogError(e, "QA Loop rerun-analyze failed for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── QLV2-203: AuthorUiCases ──────────────────────────────────────────────

    public Task AuthorUiCasesAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => AuthorUiCasesInnerAsync(sessionId, ct));
        return Task.CompletedTask;
    }

    private async Task AuthorUiCasesInnerAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);

            session.UiStatus = ApiUiStatus.Authoring;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, "ui-author", "UI test-case authoring started");

            // KBG-111: prefer FRS-grounded UI analysis; fall back to deprecated inventory path
            // for old Slab 2 sessions that never ran the UI Analyst.
            await db.Entry(session).Collection(s => s.Drafts).LoadAsync(ct);
            var existingSuite = DraftsToLinkedCases(session.Drafts);
            List<UiCaseDraft> cases;
            if (!string.IsNullOrEmpty(session.UiAnalysisJson))
            {
                var analysis = JsonSerializer.Deserialize<QaLoopUiAnalystOutput>(session.UiAnalysisJson, UiAnalysisJsonOpts)
                    ?? throw new InvalidOperationException("UiAnalysisJson deserialization returned null.");
                cases = await new QaLoopUiCaseAuthorAgent(llm).AuthorCasesAsync(
                    session.Module, analysis, FormatScanContext(session),
                    existingSuiteCases: existingSuite,
                    emit: EmitClosure(key, "ui-author", session.ActivityEnabled), ct: ct);
            }
            else
            {
                var inventory = session.InventoryJson is { Length: > 0 }
                    ? JsonSerializer.Deserialize<InventoryDoc>(session.InventoryJson)
                      ?? throw new InvalidOperationException("InventoryJson deserialization returned null.")
                    : throw new InvalidOperationException("InventoryJson missing — run Analyze first.");
                cases = await new UiCaseAuthorAgent(llm).AuthorCasesAsync(
                    session.Module, inventory, FormatScanContext(session),
                    emit: EmitClosure(key, "ui-author", session.ActivityEnabled), ct: ct);
            }

            // Dedup against Plan-imported titles (L1) — keep imported drafts; skip author duplicates.
            var existingTitles = session.Drafts
                .Select(d => PlanImportService.NormalizeTitle(d.Title))
                .Where(t => t.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var now = DateTime.UtcNow;
            var added = 0;
            var sortBase = session.Drafts.Count == 0 ? 0 : session.Drafts.Max(d => d.SortOrder) + 1;
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var norm = PlanImportService.NormalizeTitle(c.Title);
                if (norm.Length > 0 && existingTitles.Contains(norm))
                    continue;
                db.ApiTestDrafts.Add(new ApiTestDraft
                {
                    SessionId = sessionId,
                    Level = TestLevel.Ui,
                    DraftKind = ApiDraftKind.UiCase,
                    Bucket = c.Bucket,
                    Title = c.Title,
                    Preconditions = c.Preconditions,
                    StepsJson = c.StepsJson,
                    SpecCode = "",
                    Status = ApiDraftStatus.Draft,
                    SortOrder = sortBase + added,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                if (norm.Length > 0) existingTitles.Add(norm);
                added++;
            }

            session.UiStatus = ApiUiStatus.AwaitingReview;
            Pub(key, EventKinds.System, "ui-author",
                $"UI cases authored ({added} new, {existingSuite.Count} Plan-imported kept) — awaiting review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI authoring failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── Refine UI cases (Tester-style regenerate for UiCase drafts) ───────────

    public Task RefineUiCasesAsync(int sessionId, string? overallFeedback,
        IReadOnlyList<Core.Contracts.ApiDraftFeedback> perDraft, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RefineUiCasesInnerAsync(sessionId, overallFeedback, perDraft, ct));
        return Task.CompletedTask;
    }

    private async Task RefineUiCasesInnerAsync(int sessionId, string? overallFeedback,
        IReadOnlyList<Core.Contracts.ApiDraftFeedback> perDraft, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.Include(s => s.Drafts).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        try
        {
            if (session.UiStatus != ApiUiStatus.AwaitingReview)
                throw new InvalidOperationException(
                    $"UiStatus must be AwaitingReview to refine UI cases (was {session.UiStatus}).");
            if (string.IsNullOrEmpty(session.UiAnalysisJson))
                throw new InvalidOperationException("UiAnalysisJson missing — cannot regenerate UI cases.");

            var analysis = JsonSerializer.Deserialize<QaLoopUiAnalystOutput>(session.UiAnalysisJson, UiAnalysisJsonOpts)
                ?? throw new InvalidOperationException("UiAnalysisJson deserialization returned null.");

            var removeIds = perDraft.Where(f => f.Remove).Select(f => f.DraftId).ToHashSet();
            var feedbackBits = perDraft
                .Where(f => !string.IsNullOrWhiteSpace(f.Feedback) && !f.Remove)
                .Select(f =>
                {
                    var title = session.Drafts.FirstOrDefault(d => d.Id == f.DraftId)?.Title ?? $"#{f.DraftId}";
                    return $"- {title}: {f.Feedback!.Trim()}";
                })
                .ToList();

            // Drop pending UI cases (and any explicitly marked remove). Keep Approved/Pushed.
            var drop = session.Drafts
                .Where(d => d.DraftKind == ApiDraftKind.UiCase
                    && (d.Status is ApiDraftStatus.Draft or ApiDraftStatus.Rejected || removeIds.Contains(d.Id))
                    && d.Status != ApiDraftStatus.Approved
                    && d.Status != ApiDraftStatus.Pushed)
                .ToList();
            drop.AddRange(session.Drafts.Where(d =>
                d.DraftKind == ApiDraftKind.UiCase && removeIds.Contains(d.Id)
                && d.Status is ApiDraftStatus.Approved or ApiDraftStatus.Pushed));
            db.ApiTestDrafts.RemoveRange(drop.DistinctBy(d => d.Id));

            var keptApproved = session.Drafts
                .Where(d => d.DraftKind == ApiDraftKind.UiCase
                    && d.Status is ApiDraftStatus.Approved or ApiDraftStatus.Pushed
                    && !removeIds.Contains(d.Id))
                .Select(d => d.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            var fb = new StringBuilder();
            var scanCtx = FormatScanContext(session);
            if (!string.IsNullOrWhiteSpace(scanCtx))
                fb.AppendLine(scanCtx.Trim());
            if (!string.IsNullOrWhiteSpace(overallFeedback))
            {
                if (fb.Length > 0) fb.AppendLine();
                fb.AppendLine("Reviewer overall feedback:");
                fb.AppendLine(overallFeedback.Trim());
            }
            if (feedbackBits.Count > 0)
            {
                if (fb.Length > 0) fb.AppendLine();
                fb.AppendLine("Per-case feedback:");
                foreach (var line in feedbackBits) fb.AppendLine(line);
            }
            if (keptApproved.Count > 0)
            {
                if (fb.Length > 0) fb.AppendLine();
                fb.AppendLine("Already approved — do NOT recreate these titles (author only replacements / new cases):");
                foreach (var t in keptApproved) fb.AppendLine($"- {t}");
            }

            var llm = await _llmFactory.CreateAsync(ct);
            session.UiStatus = ApiUiStatus.Authoring;
            if (!string.IsNullOrWhiteSpace(overallFeedback)) session.LastFeedback = overallFeedback;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, "ui-author", "Regenerating UI cases with feedback");

            var cases = await new QaLoopUiCaseAuthorAgent(llm).AuthorCasesAsync(
                session.Module, analysis,
                additionalContext: fb.Length > 0 ? fb.ToString() : null,
                emit: EmitClosure(key, "ui-author", session.ActivityEnabled), ct: ct);

            var now = DateTime.UtcNow;
            var sortBase = session.Drafts.Where(d => d.DraftKind == ApiDraftKind.UiCase).Select(d => d.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                db.ApiTestDrafts.Add(new ApiTestDraft
                {
                    SessionId = sessionId,
                    Level = TestLevel.Ui,
                    DraftKind = ApiDraftKind.UiCase,
                    Bucket = c.Bucket,
                    Title = c.Title,
                    Preconditions = c.Preconditions,
                    StepsJson = c.StepsJson,
                    SpecCode = "",
                    Status = ApiDraftStatus.Draft,
                    SortOrder = sortBase + i,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            session.UiStatus = ApiUiStatus.AwaitingReview;
            Pub(key, EventKinds.System, "ui-author",
                $"UI cases regenerated ({cases.Count} new) — awaiting review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI case refine failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── QLV2-204: GenerateUiScripts ──────────────────────────────────────────

    public Task GenerateUiScriptsAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => GenerateUiScriptsInnerAsync(sessionId, ct));
        return Task.CompletedTask;
    }

    private async Task GenerateUiScriptsInnerAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var playwright = scope.ServiceProvider.GetRequiredService<IPlaywrightAgentService>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        // KBG-112: only start from AwaitingReview (controller also returns 409).
        if (session.UiStatus != ApiUiStatus.AwaitingReview)
        {
            Pub(key, EventKinds.Error, "ui-script",
                $"generate-ui-scripts requires UiStatus=AwaitingReview (was {session.UiStatus})",
                EventLevels.Warn, Terminal());
            PersistLog(session, key);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            session.UiStatus = ApiUiStatus.Drafting;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, "ui-script", "Playwright script generation started");

            var cases = await db.ApiTestDrafts
                .Where(d => d.SessionId == sessionId
                    && d.DraftKind == ApiDraftKind.UiCase
                    && d.Status == ApiDraftStatus.Approved)
                .OrderBy(d => d.SortOrder)
                .ToListAsync(ct);

            if (cases.Count == 0)
            {
                session.UiStatus = ApiUiStatus.Failed;
                session.ErrorMessage = "No approved UI cases to generate scripts for.";
                Pub(key, EventKinds.Error, "ui-script",
                    "No approved UI cases found", EventLevels.Error, Terminal());
                PersistLog(session, key);
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            string? moduleRoute = null;
            if (session.KbContextJson is { Length: > 0 })
            {
                try
                {
                    var frs = JsonSerializer.Deserialize<FrsModuleContext>(session.KbContextJson);
                    moduleRoute = ExtractFirstAtlasRoute(frs?.AtlasMarkdown);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "KbContextJson deserialize failed for session {Id} during UI script gen", sessionId);
                }
            }
            // Fallback: FrontendModule (legacy Slab 2 field) when atlas has no routes.
            moduleRoute ??= string.IsNullOrWhiteSpace(session.FrontendModule) ? null : session.FrontendModule;

            var metaModule = session.FrsSlug ?? session.Module;
            var now = DateTime.UtcNow;
            var sortBase = cases.Max(d => d.SortOrder) + 1;
            var timeout = TimeSpan.FromSeconds(_uiScriptGenTimeoutSeconds);
            using var gate = new SemaphoreSlim(2);

            // Generate in parallel (cap 2); persist sequentially afterward (DbContext is not thread-safe).
            var genResults = new (int Index, ApiTestDraft Case, string? SpecPath, string? SpecCode, string? Error)[cases.Count];
            await Task.WhenAll(cases.Select(async (uiCase, i) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    Pub(key, EventKinds.Result, "ui-script",
                        $"{i + 1}/{cases.Count}: generating script — {uiCase.Title}");

                    using var caseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    caseCts.CancelAfter(timeout);
                    try
                    {
                        var progress = new Progress<string>(msg =>
                            Pub(key, "stream", "ui-script", msg));

                        var specPath = await playwright.GenerateScriptAsync(
                            uiCase.Title, uiCase.StepsJson, uiCase.Bucket ?? "regression",
                            uiCase.Id, progress, caseCts.Token, moduleRoute);

                        var rawCode = File.Exists(specPath) ? await File.ReadAllTextAsync(specPath, caseCts.Token) : "";
                        var tags = QaMetaHeader.ExtractTags(rawCode);
                        var specCode = QaMetaHeader.Upsert(rawCode, new QaMeta(
                            metaModule, "ui", "ui", uiCase.Bucket, TcId: null, SourceDraftId: null,
                            DateTime.UtcNow, QaMetaHeader.ComputeHash(rawCode),
                            Tags: tags.Count > 0 ? tags : null));
                        if (!string.IsNullOrEmpty(specPath) && specCode != rawCode)
                            try { await File.WriteAllTextAsync(specPath, specCode, caseCts.Token); } catch { /* best-effort */ }

                        genResults[i] = (i, uiCase, specPath, specCode, null);
                    }
                    catch (OperationCanceledException) when (caseCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        _log.LogWarning("Script gen timed out for case '{Title}'", uiCase.Title);
                        Pub(key, EventKinds.Error, "ui-script",
                            $"Script gen timed out for '{uiCase.Title}'", EventLevels.Warn);
                        genResults[i] = (i, uiCase, null, null, "Generation timed out");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Script gen failed for case '{Title}' — skipping", uiCase.Title);
                        Pub(key, EventKinds.Error, "ui-script",
                            $"Script gen failed for '{uiCase.Title}': {ex.Message}", EventLevels.Warn);
                        genResults[i] = (i, uiCase, null, null, ex.Message);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            var generated = 0;
            foreach (var result in genResults.OrderBy(r => r.Index))
            {
                if (result.Error is not null)
                {
                    result.Case.ErrorMessage = result.Error;
                    result.Case.UpdatedAt = now;
                    continue;
                }
                if (result.SpecPath is null) continue;

                result.Case.ErrorMessage = null;
                result.Case.UpdatedAt = now;
                var uiTags = QaMetaHeader.ExtractTags(result.SpecCode ?? "");
                var specPath = result.SpecPath;
                var specCode = result.SpecCode ?? "";

                // INT-303 — relocate UI scripts under AUTOMATION_ROOT when product mode.
                if (_scriptPaths.IsProductMode)
                {
                    try
                    {
                        var unit = metaModule;
                        var bucket = result.Case.Bucket ?? "regression";
                        var dest = _scriptPaths.UiSpecPath(unit, bucket, ScriptPathAdapter.Slug(result.Case.Title));
                        _scriptPaths.EnsureUnderRoot(dest);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (!string.IsNullOrEmpty(specCode))
                            await File.WriteAllTextAsync(dest, specCode, ct);
                        else if (File.Exists(specPath))
                            File.Copy(specPath, dest, overwrite: true);
                        else
                            await File.WriteAllTextAsync(dest, specCode, ct);
                        EnsureUiFixtures(unit);
                        TouchRegistry(unit, _scriptPaths.ToRootRelative(dest));
                        specPath = dest;
                        Pub(key, EventKinds.Result, "ui-script",
                            $"UI spec → {_scriptPaths.ToRootRelative(dest)}");
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to relocate UI spec under AUTOMATION_ROOT for '{Title}'",
                            result.Case.Title);
                    }
                }

                db.ApiTestDrafts.Add(new ApiTestDraft
                {
                    SessionId = sessionId,
                    Level = TestLevel.Ui,
                    DraftKind = ApiDraftKind.UiScript,
                    Bucket = result.Case.Bucket,
                    Title = result.Case.Title,
                    Preconditions = result.Case.Preconditions,
                    StepsJson = result.Case.StepsJson,
                    SpecCode = specCode,
                    SpecFilePath = specPath,
                    Tags = uiTags.Count > 0 ? JsonSerializer.Serialize(uiTags) : null,
                    Status = ApiDraftStatus.Draft,
                    SortOrder = sortBase + result.Index,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                generated++;
            }
            await db.SaveChangesAsync(ct);

            session.UiStatus = ApiUiStatus.Drafted;
            Pub(key, EventKinds.System, "ui-script",
                $"UI scripts generated ({generated}/{cases.Count}) — awaiting review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI script generation failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    /// <summary>Pulls the first entry from an atlas YAML <c>routes:</c> array in frontmatter, if present.</summary>
    private static string? ExtractFirstAtlasRoute(string? atlasMarkdown)
    {
        if (string.IsNullOrWhiteSpace(atlasMarkdown)) return null;
        var lines = atlasMarkdown.Replace("\r\n", "\n").Split('\n');
        int start = -1, end = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "---") continue;
            if (start == -1) start = i;
            else { end = i; break; }
        }
        if (start < 0 || end < 0) return null;

        var inRoutes = false;
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var top = System.Text.RegularExpressions.Regex.Match(line, @"^(\w[\w_-]*):\s*(.*)$");
            if (top.Success && !line.StartsWith(' ') && !line.StartsWith('\t') && !line.StartsWith('-'))
            {
                inRoutes = string.Equals(top.Groups[1].Value, "routes", StringComparison.OrdinalIgnoreCase);
                var scalar = top.Groups[2].Value.Trim().Trim('"', '\'');
                if (inRoutes && scalar.Length > 0 && scalar is not ("|" or ">"))
                    return scalar;
                continue;
            }

            if (!inRoutes) continue;
            var item = System.Text.RegularExpressions.Regex.Match(line, @"^[\s]*-\s+(.+)$");
            if (!item.Success) continue;
            var val = item.Groups[1].Value.Trim().Trim('"', '\'');
            if (val.Length > 0) return val;
        }
        return null;
    }

    // ── KBG-113: RunUiScripts (step 10b) ─────────────────────────────────────

    public Task RunUiScriptsAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        // CancellationRegistry via Launch; PipelineConcurrency is acquired inside PlaywrightRunner.RunAsync
        // (do NOT gate here — same singleton SemaphoreSlim would deadlock).
        Launch(sessionId, ct => RunUiScriptsInnerAsync(sessionId, ct));
        return Task.CompletedTask;
    }

    private async Task RunUiScriptsInnerAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        // Prevent double-start (controller also returns 409).
        if (session.UiStatus == ApiUiStatus.RunningUiScripts)
        {
            Pub(key, EventKinds.Error, EventPhases.RunUi,
                "UI scripts already running", EventLevels.Warn, Terminal());
            PersistLog(session, key);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            session.UiStatus = ApiUiStatus.RunningUiScripts;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, EventPhases.RunUi, "UI script execution started");

            var drafts = await db.ApiTestDrafts
                .Where(d => d.SessionId == sessionId
                    && d.DraftKind == ApiDraftKind.UiScript
                    && d.Status == ApiDraftStatus.Approved
                    && d.SpecFilePath != null && d.SpecFilePath != "")
                .OrderBy(d => d.SortOrder)
                .ToListAsync(ct);

            var runnable = drafts.Where(d => File.Exists(d.SpecFilePath!)).ToList();
            if (runnable.Count == 0)
            {
                Pub(key, EventKinds.Error, EventPhases.RunUi,
                    "No approved UiScript drafts with valid SpecFilePath to run",
                    EventLevels.Warn, Terminal());
                session.UiStatus = ApiUiStatus.Drafted;
                PersistLog(session, key);
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var playwright = scope.ServiceProvider.GetRequiredService<IPlaywrightAgentService>();
            const int maxAttempts = 3;
            var pending = runnable.ToList();
            PlaywrightRunResult? lastResult = null;
            var cumulativePassed = 0;
            var cumulativeFailed = 0;
            var cumulativeSkipped = 0;

            for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
            {
                var runId = session.Id * 10_000
                    + (session.LastUiRunId is null ? 1 : session.LastUiRunId.Value % 10_000 + 1);
                session.LastUiRunId = runId;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                var spec = new PlaywrightRunSpec
                {
                    RunId = runId,
                    SpecFilePaths = pending.Select(d => d.SpecFilePath!).ToList(),
                    EnableCapture = true,
                    Headed = false,
                    Workers = null,
                    Module = session.FrsSlug ?? session.Module,
                    ActivityEnabled = session.ActivityEnabled,
                    IsUiRun = true,
                };

                Pub(key, EventKinds.Phase, EventPhases.RunUi,
                    attempt == 1
                        ? $"UI Playwright attempt {attempt}/{maxAttempts} · {pending.Count} spec(s)"
                        : $"UI Playwright re-run attempt {attempt}/{maxAttempts} · {pending.Count} failed spec(s)");

                // PlaywrightRunner acquires PipelineConcurrency.Gate internally.
                lastResult = await _playwrightRunner.RunAsync(spec, key, ct);

                var failedByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var failure in lastResult.FailedTests)
                {
                    var resolved = ResolveJUnitSpecPath(failure.SpecFilePath);
                    if (resolved is null) continue;
                    failedByKey[SpecFileKey(resolved)] = failure.ErrorMessage ?? "Test failed";
                }

                var now = DateTime.UtcNow;
                var stillFailing = new List<ApiTestDraft>();
                foreach (var draft in pending)
                {
                    var sk = SpecFileKey(draft.SpecFilePath!);
                    var fileName = Path.GetFileName(draft.SpecFilePath!);
                    if (failedByKey.TryGetValue(sk, out var err))
                    {
                        draft.ErrorMessage = err;
                        draft.UpdatedAt = now;
                        Pub(key, EventKinds.Result, EventPhases.RunUi,
                            $"Spec {fileName}: failed", EventLevels.Warn);
                        stillFailing.Add(draft);
                    }
                    else
                    {
                        draft.ErrorMessage = null;
                        draft.UpdatedAt = now;
                        Pub(key, EventKinds.Result, EventPhases.RunUi,
                            $"Spec {fileName}: passed", EventLevels.Success);
                        cumulativePassed++;
                    }
                }

                cumulativeFailed = stillFailing.Count;
                cumulativeSkipped = lastResult.Skipped;

                if (stillFailing.Count == 0 || attempt == maxAttempts)
                {
                    pending = stillFailing;
                    break;
                }

                // Auto-heal failed specs in place; keep Approved so the next attempt can re-run.
                Pub(key, EventKinds.Phase, "ui-heal",
                    $"Auto-healing {stillFailing.Count} failed spec(s) before re-run");
                foreach (var draft in stillFailing)
                {
                    try
                    {
                        var healMsg = $"{draft.Title}\n{draft.ErrorMessage}";
                        await playwright.HealAsync(draft.SpecFilePath!, healMsg, progress: null, ct: ct);
                        if (File.Exists(draft.SpecFilePath!))
                        {
                            draft.SpecCode = await File.ReadAllTextAsync(draft.SpecFilePath!, ct);
                            // Keep Approved — do not reset to Draft (that would block re-run).
                            draft.Status = ApiDraftStatus.Approved;
                            draft.UpdatedAt = DateTime.UtcNow;
                            Pub(key, EventKinds.Result, "ui-heal",
                                $"Healed {Path.GetFileName(draft.SpecFilePath)}", EventLevels.Success);
                        }
                    }
                    catch (Exception hex)
                    {
                        _log.LogWarning(hex, "Auto-heal failed for draft {DraftId}", draft.Id);
                        Pub(key, EventKinds.Error, "ui-heal",
                            $"Heal failed for {Path.GetFileName(draft.SpecFilePath)}: {hex.Message}",
                            EventLevels.Warn);
                    }
                }
                await db.SaveChangesAsync(ct);
                pending = stillFailing;
            }

            session.UiStatus = ApiUiStatus.Drafted;
            var passed = cumulativePassed;
            var failed = cumulativeFailed;
            var skipped = cumulativeSkipped;
            Pub(key, EventKinds.System, EventPhases.RunUi,
                $"UI script run complete — {passed} passed · {failed} failed · {skipped} skipped",
                failed > 0 ? EventLevels.Warn : EventLevels.Success, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, EventPhases.RunUi,
                "UI script run cancelled by user", EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI script run failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, EventPhases.RunUi,
                $"UI script run failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    /// <summary>Resolve a JUnit-reported spec path (often relative to PlaywrightRoot) to absolute.</summary>
    private string? ResolveJUnitSpecPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Path.IsPathRooted(raw)) return Path.GetFullPath(raw);
        if (!string.IsNullOrWhiteSpace(_playwrightRoot))
            return Path.GetFullPath(Path.Combine(_playwrightRoot, raw));
        return Path.GetFullPath(raw);
    }

    /// <summary>Basename match key — draft stores absolute path; JUnit often emits relative.</summary>
    private static string SpecFileKey(string path) =>
        Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant();

    // ── QLV2-206: HealSpecs ──────────────────────────────────────────────────

    public Task HealSpecsAsync(int sessionId, int[] draftIds, string? userContext = null,
        CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => HealSpecsInnerAsync(sessionId, draftIds, userContext, ct));
        return Task.CompletedTask;
    }

    private async Task HealSpecsInnerAsync(int sessionId, int[] draftIds, string? userContext, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var playwright = scope.ServiceProvider.GetRequiredService<IPlaywrightAgentService>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            Pub(key, EventKinds.Phase, "ui-heal", $"Healing {draftIds.Length} spec(s)");

            var drafts = await db.ApiTestDrafts
                .Where(d => d.SessionId == sessionId
                    && draftIds.Contains(d.Id)
                    && d.DraftKind == ApiDraftKind.UiScript)
                .OrderBy(d => d.SortOrder)
                .ToListAsync(ct);

            var healed = 0;
            for (var i = 0; i < drafts.Count; i++)
            {
                var draft = drafts[i];
                Pub(key, EventKinds.Result, "ui-heal",
                    $"{i + 1}/{drafts.Count}: healing — {draft.Title}");

                try
                {
                    if (string.IsNullOrWhiteSpace(draft.SpecFilePath))
                        throw new InvalidOperationException("Draft has no SpecFilePath — cannot heal.");

                    // INT-304 — heal only under AUTOMATION_ROOT when product mode (write boundary).
                    if (_scriptPaths.IsProductMode)
                        _scriptPaths.EnsureUnderRoot(draft.SpecFilePath);

                    var progress = new Progress<string>(msg =>
                        Pub(key, "stream", "ui-heal", msg));

                    await playwright.HealAsync(
                        draft.SpecFilePath, draft.ErrorMessage ?? "", progress, ct,
                        userContext: userContext);

                    if (File.Exists(draft.SpecFilePath))
                    {
                        var rawCode = await File.ReadAllTextAsync(draft.SpecFilePath, ct);
                        // QLV2-301: re-stamp — carry forward the prior header's fields (module/type/
                        // level/bucket/tcId/sourceDraftId) if the file had one, else mint a fresh one
                        // now that draft.Id is known.
                        var priorMeta = QaMetaHeader.TryParse(draft.SpecCode) ?? QaMetaHeader.TryParse(rawCode);
                        var body = QaMetaHeader.StripHeader(rawCode);
                        var tags = QaMetaHeader.ExtractTags(body);
                        draft.Tags = tags.Count > 0 ? JsonSerializer.Serialize(tags) : null;
                        var meta = priorMeta is not null
                            ? priorMeta with
                            {
                                GeneratedAt = DateTime.UtcNow,
                                SpecHash = QaMetaHeader.ComputeHash(body),
                                Tags = tags.Count > 0 ? tags : null,
                            }
                            : new QaMeta(session.FrsSlug ?? session.Module, "ui", "ui", draft.Bucket,
                                draft.AdoTestCaseId, draft.Id, DateTime.UtcNow,
                                QaMetaHeader.ComputeHash(body), Tags: tags.Count > 0 ? tags : null);
                        var stamped = QaMetaHeader.Upsert(body, meta);
                        if (stamped != rawCode)
                            try { await File.WriteAllTextAsync(draft.SpecFilePath, stamped, ct); } catch { /* best-effort */ }
                        draft.SpecCode = stamped;
                    }
                    draft.Status = ApiDraftStatus.Draft;
                    draft.ErrorMessage = null;
                    draft.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    healed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Heal failed for draft {Id} '{Title}' — skipping", draft.Id, draft.Title);
                    Pub(key, EventKinds.Error, "ui-heal",
                        $"Heal failed for '{draft.Title}': {ex.Message}", EventLevels.Warn);
                }
            }

            Pub(key, EventKinds.System, "ui-heal",
                $"Heal complete ({healed}/{drafts.Count}) — specs reset to Draft", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Pub(key, EventKinds.System, text: "Cancelled by user", level: EventLevels.Warn, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI spec healing failed for session {SessionId}", sessionId);
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── QAL-023: Push / PushDraft ────────────────────────────────────────────

    public Task PushAllAsync(int sessionId, int testPlanId, int testSuiteId, int? storyId = null,
        string? draftKindFilter = null, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RunPushAsync(sessionId, testPlanId, testSuiteId, storyId, draftKindFilter, ct));
        return Task.CompletedTask;
    }

    // ── QLV2-107: RefineDrafts ───────────────────────────────────────────────

    public Task RefineDraftsAsync(int sessionId, string? overallFeedback, IReadOnlyList<Core.Contracts.ApiDraftFeedback> perDraft, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RefineDraftsInnerAsync(sessionId, overallFeedback, perDraft, ct));
        return Task.CompletedTask;
    }

    private async Task RefineDraftsInnerAsync(int sessionId, string? overallFeedback, IReadOnlyList<Core.Contracts.ApiDraftFeedback> perDraft, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.Include(s => s.Drafts).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);
            var inventory = session.InventoryJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<InventoryDoc>(session.InventoryJson)
                  ?? throw new InvalidOperationException("InventoryJson deserialization returned null.")
                : throw new InvalidOperationException("InventoryJson missing — run Analyze first.");

            session.Status = ApiSessionStatus.Drafting;
            if (!string.IsNullOrWhiteSpace(overallFeedback)) session.LastFeedback = overallFeedback;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, EventPhases.Generate, "Refining drafts with feedback");

            var feedbackById = perDraft
                .Where(f => !string.IsNullOrWhiteSpace(f.Feedback))
                .ToDictionary(f => f.DraftId, f => f.Feedback!);
            var removeIds = perDraft
                .Where(f => f.Remove)
                .Select(f => f.DraftId)
                .ToHashSet();

            // Only refine Draft/Rejected rows that have per-test feedback; skip Approved + Pushed
            var mutableStatuses = new[] { ApiDraftStatus.Draft, ApiDraftStatus.Rejected };
            var targetedIds = feedbackById.Keys
                .Where(id => session.Drafts.Any(d => d.Id == id && mutableStatuses.Contains(d.Status)))
                .ToHashSet();

            var now = DateTime.UtcNow;
            var genExtra = BuildGeneratorExtraContext(session);
            var productMode = _scriptPaths.IsProductMode;
            var metaModule = session.FrsSlug ?? session.Module;
            foreach (var draft in session.Drafts.Where(d => targetedIds.Contains(d.Id)))
            {
                var draftFeedback = feedbackById[draft.Id];
                var combined = string.IsNullOrWhiteSpace(overallFeedback)
                    ? draftFeedback
                    : $"{draftFeedback}\n\nOverall: {overallFeedback}";
                var priorSteps = ParseSteps(draft.StepsJson);
                GeneratedSpec? refined = null;
                try
                {
                    refined = draft.Level switch
                    {
                        TestLevel.Contract => await RefineSingle(
                            () => MatchRouteForDraft(draft, inventory),
                            route => new ContractGeneratorAgent(llm).RefineAsync(
                                session.Module, route, draft.SpecCode, priorSteps, combined,
                                extraContext: genExtra, isProductMode: productMode,
                                emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct)),
                        TestLevel.Endpoint => await RefineSingleMulti(
                            () => MatchRouteForDraft(draft, inventory),
                            route => new EndpointGeneratorAgent(llm).RefineAsync(
                                session.Module, route, draft.SpecCode, priorSteps, combined,
                                extraContext: genExtra, isProductMode: productMode,
                                emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct),
                            draft.CaseIndex ?? 0),
                        TestLevel.Workflow => await RefineSingle(
                            () => MatchWorkflowForDraft(draft, inventory),
                            wf => new WorkflowGeneratorAgent(llm).RefineAsync(
                                session.Module, wf, inventory, draft.SpecCode, priorSteps, combined,
                                extraContext: genExtra, isProductMode: productMode,
                                emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct)),
                        _ => null,
                    };
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Refine failed for draft {DraftId} — leaving unchanged", draft.Id);
                    continue;
                }
                if (refined is null) continue;

                // Update in place
                draft.Title = refined.TestCase?.Title ?? refined.Title;
                draft.SpecCode = refined.SpecCode;
                draft.StepsJson = refined.TestCase is not null
                    ? JsonSerializer.Serialize(refined.TestCase.Steps.Select(s => new { action = s.Action, expected = s.Expected }))
                    : draft.StepsJson;
                if (refined.SchemaJson is not null) draft.SchemaJson = refined.SchemaJson;
                // Rewrite disk spec (case 0 or single)
                if (draft.SpecFilePath is not null && (draft.CaseIndex is null or 0))
                {
                    // QLV2-301: re-stamp @qa-meta — draft.Id is known here, so SourceDraftId is filled
                    // in (unlike the initial-generate path). Preserve TcId if the draft was already pushed.
                    var tags = QaMetaHeader.ExtractTags(refined.SpecCode);
                    draft.Tags = tags.Count > 0 ? JsonSerializer.Serialize(tags) : null;
                    var stamped = QaMetaHeader.Upsert(refined.SpecCode, new QaMeta(
                        metaModule, "api", draft.Level.ToWire(), Bucket: null, draft.AdoTestCaseId,
                        draft.Id, DateTime.UtcNow, QaMetaHeader.ComputeHash(refined.SpecCode),
                        Tags: tags.Count > 0 ? tags : null));
                    draft.SpecCode = stamped;
                    try { File.WriteAllText(draft.SpecFilePath, stamped); } catch (Exception ex) { _log.LogWarning(ex, "Spec rewrite failed for draft {DraftId}", draft.Id); }
                }
                else
                {
                    var tags = QaMetaHeader.ExtractTags(refined.SpecCode);
                    draft.Tags = tags.Count > 0 ? JsonSerializer.Serialize(tags) : null;
                }
                draft.UpdatedAt = now;
                Pub(key, EventKinds.Result, EventPhases.Generate, $"Refined: {draft.Title}");
            }

            // Remove flagged Draft/Rejected rows (never Approved/Pushed)
            var toRemove = session.Drafts
                .Where(d => removeIds.Contains(d.Id) && mutableStatuses.Contains(d.Status))
                .ToList();
            db.ApiTestDrafts.RemoveRange(toRemove);

            await db.SaveChangesAsync(ct);
            session.Status = ApiSessionStatus.Drafted;
            Pub(key, EventKinds.System, text: "Refine complete", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
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
            _log.LogError(e, "QA Loop refine-drafts failed for session {SessionId}", sessionId);
            session.Status = ApiSessionStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, text: $"Failed: {e.Message}", level: EventLevels.Error, data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    private static RouteInfo? MatchRouteForDraft(ApiTestDraft draft, InventoryDoc inventory)
    {
        if (draft.SourceRoutesJson is null) return inventory.Routes.FirstOrDefault();
        var sources = JsonSerializer.Deserialize<List<string>>(draft.SourceRoutesJson) ?? [];
        return inventory.Routes.FirstOrDefault(r =>
            sources.Any(s => string.Equals(s, $"{r.Method} {r.Route}", StringComparison.OrdinalIgnoreCase)));
    }

    private static WorkflowInfo? MatchWorkflowForDraft(ApiTestDraft draft, InventoryDoc inventory)
    {
        if (draft.SourceRoutesJson is null) return inventory.Workflows.FirstOrDefault();
        var sources = JsonSerializer.Deserialize<List<string>>(draft.SourceRoutesJson) ?? [];
        return inventory.Workflows.FirstOrDefault(w =>
            w.Steps.Any(s => sources.Any(src => string.Equals(src, s.Endpoint, StringComparison.OrdinalIgnoreCase))));
    }

    private static async Task<GeneratedSpec?> RefineSingle<T>(Func<T?> resolve, Func<T, Task<GeneratedSpec>> refine) where T : class
    {
        var item = resolve();
        return item is null ? null : await refine(item);
    }

    private static async Task<GeneratedSpec?> RefineSingleMulti(Func<RouteInfo?> resolve, Func<RouteInfo, Task<List<GeneratedSpec>>> refine, int caseIndex)
    {
        var route = resolve();
        if (route is null) return null;
        var specs = await refine(route);
        return caseIndex < specs.Count ? specs[caseIndex] : specs.FirstOrDefault();
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
        var wasExisting = draft.AdoTestCaseId is > 0;
        int tcId;
        if (wasExisting)
        {
            tcId = draft.AdoTestCaseId!.Value;
            await _ado.UpdateTestCaseAsync(tcId, draft.Title, draft.Preconditions, steps, cancellationToken);
        }
        else
        {
            tcId = await _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, cancellationToken);
        }

        var levelSuites = await EnsureLevelSuitesAsync(
            testPlanId, testSuiteId, session.Module, new[] { draft.Level }, sessionKey: null, cancellationToken);

        int targetSuiteId;
        if (draft.DraftKind == ApiDraftKind.UiScript && draft.Bucket is not null)
        {
            IReadOnlyList<AdoTestSuite>? planSuites = null;
            try { planSuites = await _ado.GetTestSuitesAsync(testPlanId, cancellationToken); } catch { }
            var uiLevelId = levelSuites.TryGetValue(TestLevel.Ui, out var lsid2) ? lsid2 : testSuiteId;
            var buckets = await EnsureUiBucketSuitesAsync(testPlanId, uiLevelId, planSuites, sessionKey: null, cancellationToken);
            targetSuiteId = buckets.TryGetValue(draft.Bucket, out var bsid) ? bsid : uiLevelId;
        }
        else
            targetSuiteId = levelSuites.TryGetValue(draft.Level, out var lsid) ? lsid : testSuiteId;

        try
        {
            await _ado.AddTestCaseToSuiteAsync(testPlanId, targetSuiteId, tcId, cancellationToken);
        }
        catch (Exception ex) when (wasExisting)
        {
            _log.LogDebug(ex, "AddTestCaseToSuite for existing TC {Id} (may already be linked)", tcId);
        }

        var workItemId = storyId ?? session.WorkItemId;
        if (workItemId.HasValue && !wasExisting)
            await _ado.LinkTestedByAsync(workItemId.Value, tcId, cancellationToken);

        draft.AdoTestCaseId = tcId;
        draft.Status = ApiDraftStatus.Pushed;
        draft.Title = RewriteTag(draft.Title, tcId, draft.CaseIndex);
        draft.ErrorMessage = null;
        draft.UpdatedAt = DateTime.UtcNow;
        RewriteSpecOnDisk(draft, tcId);

        await db.SaveChangesAsync(cancellationToken);
        await ApplySessionStatusAfterDraftPushAsync(db, session, draft, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    private async Task ApplySessionStatusAfterDraftPushAsync(
        QaDbContext db, ApiTestSession session, ApiTestDraft pushedDraft, CancellationToken ct)
    {
        var isUi = pushedDraft.DraftKind is ApiDraftKind.UiCase or ApiDraftKind.UiScript;
        if (isUi)
        {
            var pendingUi = await db.ApiTestDrafts.AnyAsync(
                d => d.SessionId == session.Id
                     && d.Id != pushedDraft.Id
                     && (d.DraftKind == ApiDraftKind.UiCase || d.DraftKind == ApiDraftKind.UiScript)
                     && d.Status == ApiDraftStatus.Approved, ct);
            if (!pendingUi)
                session.UiStatus = ApiUiStatus.Done;
        }

        if (session.HasUiPhase)
        {
            if (isUi)
            {
                var pendingUi = await db.ApiTestDrafts.AnyAsync(
                    d => d.SessionId == session.Id
                         && d.Id != pushedDraft.Id
                         && (d.DraftKind == ApiDraftKind.UiCase || d.DraftKind == ApiDraftKind.UiScript)
                         && d.Status == ApiDraftStatus.Approved, ct);
                if (!pendingUi)
                    session.Status = ApiSessionStatus.Done;
            }
            else
            {
                var pendingApi = await db.ApiTestDrafts.AnyAsync(
                    d => d.SessionId == session.Id
                         && d.Id != pushedDraft.Id
                         && (d.DraftKind == null || d.DraftKind == ApiDraftKind.ApiCase)
                         && d.Status == ApiDraftStatus.Approved, ct);
                if (!pendingApi)
                    session.Status = ApiSessionStatus.ApiDone;
            }
        }
        else
        {
            var remaining = await db.ApiTestDrafts.AnyAsync(
                d => d.SessionId == session.Id
                     && (d.DraftKind == null || d.DraftKind == ApiDraftKind.ApiCase)
                     && d.Status == ApiDraftStatus.Approved, ct);
            if (!remaining)
                session.Status = ApiSessionStatus.Done;
        }

        session.UpdatedAt = DateTime.UtcNow;
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

            // ── Step 1: Retrieve KB (KBG-107) ────────────────────────────────────
            FrsModuleContext? frsContext = null;
            {
                session.Status = ApiSessionStatus.RetrievingKb;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Phase, EventPhases.RetrieveKb, $"Retrieving FRS KB for module '{session.Module}'");
                try
                {
                    var frsKb = scope.ServiceProvider.GetRequiredService<IFrsKbService>();
                    frsContext = await frsKb.ResolveModuleAsync(session.Module, session.ProjectId, ct);
                    if (frsContext != null)
                    {
                        session.FrsSlug    = frsContext.Slug;
                        session.KbContextJson = JsonSerializer.Serialize(frsContext);
                        await db.SaveChangesAsync(ct);
                        Pub(key, EventKinds.Result, EventPhases.RetrieveKb,
                            $"KB: {frsContext.Slug}", EventLevels.Success);
                    }
                    else
                    {
                        Pub(key, EventKinds.Result, EventPhases.RetrieveKb,
                            "No FRS KB found — continuing without KB context", EventLevels.Warn);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "FRS KB retrieval failed for module '{Module}'", session.Module);
                    Pub(key, EventKinds.Result, EventPhases.RetrieveKb,
                        $"KB retrieval failed: {ex.Message} — continuing", EventLevels.Warn);
                }
            }

            // ── Step 1.5: Plan Intake (INT-204) ───────────────────────────────────
            if (session.TestPlanId is > 0)
            {
                session.Status = ApiSessionStatus.ImportingPlan;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Phase, EventPhases.PlanIntake,
                    $"Plan intake for Test Plan #{session.TestPlanId}");
                try
                {
                    var planMapper = scope.ServiceProvider.GetRequiredService<IPlanSuiteUnitMapper>();
                    var planImport = scope.ServiceProvider.GetRequiredService<IPlanImportService>();
                    await planMapper.MapAndPersistSessionAsync(sessionId, useAiForAmbiguous: true, ct);
                    // Reload session after map persist (same tracked instance usually ok)
                    await db.Entry(session).ReloadAsync(ct);
                    var meta = await planImport.ImportAsync(sessionId, remapIfMissing: false, ct: ct);
                    await db.Entry(session).ReloadAsync(ct);
                    await db.Entry(session).Collection(s => s.Drafts).LoadAsync(ct);
                    Pub(key, EventKinds.Result, EventPhases.PlanIntake,
                        $"Imported thin={meta.ThinImproved} rich={meta.RichLinked} gaps={meta.GapsCreated} (unit={meta.SelectedUnit})",
                        EventLevels.Success);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Plan intake failed for session {Id}", sessionId);
                    Pub(key, EventKinds.Result, EventPhases.PlanIntake,
                        $"Plan intake failed: {ex.Message} — continuing", EventLevels.Warn);
                }
            }

            // ── Step 2: Code Scan (KBG-107) ───────────────────────────────────────
            CodeScanBundle? codeScanBundle = null;
            string source;
            {
                session.Status = ApiSessionStatus.Scanning;
                session.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                Pub(key, EventKinds.Phase, EventPhases.Scan, $"Code scan for module '{session.Module}'");
                var codeScanSvc = scope.ServiceProvider.GetRequiredService<IQaLoopCodeScanService>();
                codeScanBundle = await codeScanSvc.ScanAsync(
                    session.Module, session.Repository ?? "", session.Branch ?? "",
                    frsContext, key, ct);
                session.CodeScanJson = JsonSerializer.Serialize(codeScanBundle);
                await db.SaveChangesAsync(ct);
                // Build source string for the analyzer from the scan bundle.
                var sb = new System.Text.StringBuilder();
                foreach (var f in codeScanBundle.FrontendFiles)
                    sb.AppendLine($"// --- {f.Path}\n{f.Content}\n");
                foreach (var f in codeScanBundle.BackendFiles)
                    sb.AppendLine($"// --- {f.Path}\n{f.Content}\n");
                source = sb.Length > 0 ? sb.ToString() : $"Module: {session.Module}";
                var isDegenerate = source == $"Module: {session.Module}";
                Pub(key, EventKinds.Result, EventPhases.Scan,
                    isDegenerate
                        ? $"Scanned: no code found — analysis will be KB-only"
                        : $"Scanned: {codeScanBundle.FrontendFiles.Length} frontend, {codeScanBundle.BackendFiles.Length} backend ({codeScanBundle.DiscoverySource})",
                    isDegenerate ? EventLevels.Warn : EventLevels.Success);
            }

            // Existing test cases for analyzer reconciliation — prefer Plan-imported drafts (INT-204),
            // else fall back to the optional single TestSuiteId.
            IReadOnlyList<LinkedTestCase> existingCases = [];
            await db.Entry(session).Collection(s => s.Drafts).LoadAsync(ct);
            var fromImport = DraftsToLinkedCases(session.Drafts);
            if (fromImport.Count > 0)
            {
                existingCases = fromImport;
                Pub(key, EventKinds.Result, EventPhases.Fetch,
                    $"Using {existingCases.Count} Plan-imported test case(s) for coverage reconciliation");
            }
            else if (session.TestSuiteId.HasValue && session.TestPlanId.HasValue)
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
                        var hit = (await _ado.SearchCodeAsync(pathOrTerm, top: 1, repoId: repo, ct: fct)).FirstOrDefault();
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
                additionalContext: FormatScanContext(session),
                frsContext: frsContext,
                fetchFile: fetchFile, emit: EmitClosure(key, EventPhases.Analyze, session.ActivityEnabled), ct: ct);

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

            await db.Entry(session).Collection(s => s.Drafts).LoadAsync(ct);
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

        // Generators fan out concurrently, but CAPPED: each generator spawns claude CLI child process(es),
        // and firing all selected surfaces at once (a module can have 40+) starves the thread pool so the
        // stdin-writer can't feed a spawned `claude -p` within its 3s window → every call dies with
        // "no stdin data received in 3s". Gate the fan-out to a handful at a time.
        using var genGate = new System.Threading.SemaphoreSlim(4, 4);
        Task<List<GeneratedSpec>?> Gated(Func<Task<List<GeneratedSpec>?>> factory) =>
            GenerateSafeAsync(async () =>
            {
                await genGate.WaitAsync(ct);
                try { return await factory(); }
                finally { genGate.Release(); }
            }, key);

        var allTasks = new List<Task<List<GeneratedSpec>?>>();
        var genExtra = BuildGeneratorExtraContext(session);
        var productMode = _scriptPaths.IsProductMode;

        foreach (var r in contractRoutes)
        {
            var route = r;
            allTasks.Add(Gated(async () =>
            {
                var spec = await new ContractGeneratorAgent(llm).GenerateAsync(
                    session.Module, route, extraContext: genExtra, isProductMode: productMode,
                    emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct);
                return new List<GeneratedSpec> { spec };
            }));
        }

        foreach (var r in endpointRoutes)
        {
            var route = r;
            allTasks.Add(Gated(async () =>
                (List<GeneratedSpec>?) await new EndpointGeneratorAgent(llm).GenerateAsync(
                    session.Module, route, extraContext: genExtra, isProductMode: productMode,
                    emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct)));
        }

        foreach (var w in workflowItems)
        {
            var workflow = w;
            allTasks.Add(Gated(async () =>
            {
                var spec = await new WorkflowGeneratorAgent(llm).GenerateAsync(
                    session.Module, workflow, analysis.Inventory, extraContext: genExtra,
                    isProductMode: productMode,
                    emit: EmitClosure(key, EventPhases.Generate, session.ActivityEnabled), ct: ct);
                return new List<GeneratedSpec> { spec };
            }));
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

    private async Task RunPushAsync(int sessionId, int testPlanId, int testSuiteId, int? storyId,
        string? draftKindFilter, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) { _log.LogError("Push: session {Id} not found", sessionId); return; }

        var isUiLane = string.Equals(draftKindFilter, "ui", StringComparison.OrdinalIgnoreCase);
        session.Status = ApiSessionStatus.Pushing;
        if (isUiLane)
            session.UiStatus = ApiUiStatus.Pushing;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Pub(key, EventKinds.Phase, EventPhases.Push, "Push to ADO started");

        var allApproved = await db.ApiTestDrafts
            .Where(d => d.SessionId == sessionId && d.Status == ApiDraftStatus.Approved)
            .OrderBy(d => d.SortOrder)
            .ToListAsync(ct);
        var drafts = allApproved
            .Where(d => MatchesDraftKindFilter(d, draftKindFilter))
            .ToList();

        if (drafts.Count == 0)
        {
            session.Status = ApiSessionStatus.Failed;
            if (isUiLane) session.UiStatus = ApiUiStatus.Drafted;
            session.ErrorMessage = draftKindFilter is null
                ? "No approved drafts to push."
                : $"No approved {draftKindFilter} drafts to push.";
            Pub(key, EventKinds.Error, EventPhases.Push,
                $"Push failed: {session.ErrorMessage}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var levelSuiteIds = await EnsureLevelSuitesAsync(
                testPlanId, testSuiteId, session.Module, drafts.Select(d => d.Level).Distinct(), key, ct);

            Dictionary<string, int> bucketSuiteIds = new(StringComparer.OrdinalIgnoreCase);
            var uiScriptDrafts = drafts.Where(d => d.DraftKind == ApiDraftKind.UiScript).ToList();
            if (uiScriptDrafts.Count > 0 && levelSuiteIds.TryGetValue(TestLevel.Ui, out var uiLevelSuiteId))
            {
                IReadOnlyList<AdoTestSuite>? planSuites = null;
                try { planSuites = await _ado.GetTestSuitesAsync(testPlanId, ct); } catch { }
                bucketSuiteIds = await EnsureUiBucketSuitesAsync(testPlanId, uiLevelSuiteId, planSuites, key, ct);
            }

            var pushed = 0;
            var failed = 0;
            var patched = 0;
            var created = 0;
            for (var i = 0; i < drafts.Count; i++)
            {
                var draft = drafts[i];
                var n = i + 1;
                var result = await TryPushDraftToAdoAsync(
                    db, session, draft, testPlanId, testSuiteId, storyId, levelSuiteIds, bucketSuiteIds,
                    n, drafts.Count, key, ct);
                if (result.Success)
                {
                    pushed++;
                    if (result.Patched) patched++;
                    else created++;
                }
                else failed++;
            }

            if (pushed == 0)
            {
                session.Status = ApiSessionStatus.Failed;
                session.ErrorMessage = "All approved drafts failed to push.";
                if (isUiLane) session.UiStatus = ApiUiStatus.Drafted;
            }
            else if (failed > 0)
            {
                session.Status = isUiLane && session.HasUiPhase
                    ? ApiSessionStatus.ApiDone
                    : ApiSessionStatus.Drafted;
                session.ErrorMessage = $"Push incomplete — {failed} of {drafts.Count} failed.";
                if (isUiLane) session.UiStatus = ApiUiStatus.Drafted;
            }
            else if (isUiLane)
            {
                session.UiStatus = ApiUiStatus.Done;
                session.Status = ApiSessionStatus.Done;
                session.ErrorMessage = null;
            }
            else
            {
                session.Status = session.HasUiPhase ? ApiSessionStatus.ApiDone : ApiSessionStatus.Done;
                session.ErrorMessage = null;
            }

            var terminal = !session.HasUiPhase || isUiLane || session.Status == ApiSessionStatus.Failed;
            Pub(key, EventKinds.System, EventPhases.Push,
                failed > 0
                    ? $"Pushed {pushed}/{drafts.Count} test case(s) to ADO ({failed} failed)"
                    : $"Pushed {pushed} test case(s) to ADO (patched={patched}, created={created})",
                pushed > 0 ? EventLevels.Success : EventLevels.Warn,
                new() { ["terminal"] = terminal, ["count"] = pushed, ["patched"] = patched, ["created"] = created, ["failed"] = failed });
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var kickUiPhase = session.HasUiPhase
                && !isUiLane
                && pushed > 0
                && failed == 0
                && (draftKindFilter is null
                    || string.Equals(draftKindFilter, "api", StringComparison.OrdinalIgnoreCase));
            if (kickUiPhase)
            {
                try
                {
                    await RunUiPhaseAsync(sessionId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // RunUiPhaseAsync already persisted UiStatus=Cancelled.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var pushedCount = drafts.Count(d => d.Status == ApiDraftStatus.Pushed);
            session.Status = ApiSessionStatus.Drafted;
            if (isUiLane) session.UiStatus = ApiUiStatus.Drafted;
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
            if (isUiLane) session.UiStatus = ApiUiStatus.Drafted;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, EventPhases.Push,
                $"Push failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    private static bool MatchesDraftKindFilter(ApiTestDraft draft, string? filter) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            "api" => draft.DraftKind is null or ApiDraftKind.ApiCase,
            "ui" => draft.DraftKind is ApiDraftKind.UiCase or ApiDraftKind.UiScript,
            _ => true,
        };

    private static bool IsTransientPushError(Exception ex) =>
        ex is HttpRequestException or IOException
        || ex.Message.Contains("copying content to a stream", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Success, bool Patched)> TryPushDraftToAdoAsync(
        QaDbContext db, ApiTestSession session, ApiTestDraft draft,
        int testPlanId, int testSuiteId, int? storyId,
        IReadOnlyDictionary<TestLevel, int> levelSuiteIds,
        IReadOnlyDictionary<string, int> bucketSuiteIds,
        int index, int total, string key, CancellationToken ct)
    {
        try
        {
            var steps = ParseSteps(draft.StepsJson);
            var wasExisting = draft.AdoTestCaseId is > 0;

            int tcId;
            if (wasExisting)
            {
                tcId = draft.AdoTestCaseId!.Value;
                Pub(key, "push", EventPhases.Push, $"{index}/{total}: patching Test Case #{tcId} — {draft.Title}");
                await RetryTransientAsync(
                    () => _ado.UpdateTestCaseAsync(tcId, draft.Title, draft.Preconditions, steps, ct), ct);
            }
            else
            {
                Pub(key, "push", EventPhases.Push, $"{index}/{total}: creating Test Case — {draft.Title}");
                tcId = await RetryTransientAsync(
                    () => _ado.CreateTestCaseAsync(draft.Title, draft.Preconditions, steps, ct), ct);
            }

            int targetSuite;
            if (draft.DraftKind == ApiDraftKind.UiScript
                && draft.Bucket is not null
                && bucketSuiteIds.TryGetValue(draft.Bucket, out var bsid))
                targetSuite = bsid;
            else
                targetSuite = levelSuiteIds.TryGetValue(draft.Level, out var lsid) ? lsid : testSuiteId;

            try
            {
                await RetryTransientAsync(
                    () => _ado.AddTestCaseToSuiteAsync(testPlanId, targetSuite, tcId, ct), ct);
            }
            catch (Exception ex) when (wasExisting)
            {
                _log.LogDebug(ex, "AddTestCaseToSuite for existing TC {Id} (may already be linked)", tcId);
            }

            var workItemId = storyId ?? session.WorkItemId;
            if (workItemId.HasValue && !wasExisting)
                await RetryTransientAsync(() => _ado.LinkTestedByAsync(workItemId.Value, tcId, ct), ct);

            draft.AdoTestCaseId = tcId;
            draft.Status = ApiDraftStatus.Pushed;
            draft.Title = RewriteTag(draft.Title, tcId, draft.CaseIndex);
            draft.ErrorMessage = null;
            draft.UpdatedAt = DateTime.UtcNow;
            RewriteSpecOnDisk(draft, tcId);
            await db.SaveChangesAsync(ct);

            Pub(key, EventKinds.Result, EventPhases.Push,
                wasExisting
                    ? $"TC #{tcId} patched → suite"
                    : $"TC #{tcId} created → suite" + (workItemId.HasValue ? " + linked to story" : ""),
                EventLevels.Success, new() { ["testCaseId"] = tcId, ["patched"] = wasExisting });
            return (true, wasExisting);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Push failed for draft {DraftId} — continuing batch", draft.Id);
            draft.ErrorMessage = ex.Message;
            draft.Status = ApiDraftStatus.Approved;
            draft.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Error, EventPhases.Push,
                $"Draft push failed ({index}/{total}): {ex.Message}", EventLevels.Warn);
            return (false, false);
        }
    }

    private static async Task RetryTransientAsync(Func<Task> action, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (IsTransientPushError(ex) && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
            }
        }
    }

    private static async Task<T> RetryTransientAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransientPushError(ex) && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
            }
        }

        return await action();
    }

    // ── KBG-110: UI Analyst (step 7) ─────────────────────────────────────────

    /// <summary>
    /// Runs the UI Analyst after API push when <c>HasUiPhase</c>. Called inline from push
    /// (same background token) — not via <see cref="Launch"/>.
    /// </summary>
    private async Task RunUiPhaseAsync(int sessionId, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            var llm = await _llmFactory.CreateAsync(ct);

            session.UiStatus = ApiUiStatus.AnalyzingUi;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, EventPhases.AnalyzeUi, "UI Analyst started");

            FrsModuleContext? frsContext = null;
            if (session.KbContextJson is { Length: > 0 })
            {
                try { frsContext = JsonSerializer.Deserialize<FrsModuleContext>(session.KbContextJson); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "KbContextJson deserialize failed for session {Id} — UI Analyst proceeds without KB", sessionId);
                }
            }

            CodeScanBundle? codeScanBundle = null;
            if (session.CodeScanJson is { Length: > 0 })
            {
                try { codeScanBundle = JsonSerializer.Deserialize<CodeScanBundle>(session.CodeScanJson); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "CodeScanJson deserialize failed for session {Id} — UI Analyst proceeds with empty FE files", sessionId);
                }
            }

            var analysis = await new QaLoopUiAnalystAgent(llm).AnalyzeAsync(
                session.Module,
                frsContext?.ModuleMarkdown ?? "",
                codeScanBundle?.FrontendFiles ?? [],
                emit: EmitClosure(key, EventPhases.AnalyzeUi, session.ActivityEnabled),
                ct: ct);

            session.UiAnalysisJson = JsonSerializer.Serialize(analysis, UiAnalysisJsonOpts);
            session.UiStatus = ApiUiStatus.AwaitingUiAnalysisReview;
            Pub(key, EventKinds.System, EventPhases.AnalyzeUi,
                "UI Analyst done — awaiting UI analysis review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, EventPhases.AnalyzeUi,
                "UI Analyst cancelled by user", EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop UI Analyst failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, EventPhases.AnalyzeUi,
                $"UI Analyst failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    public Task RerunUiAnalysisAsync(int sessionId, string feedback, CancellationToken cancellationToken = default)
    {
        Launch(sessionId, ct => RerunUiAnalysisInnerAsync(sessionId, feedback, ct));
        return Task.CompletedTask;
    }

    private async Task RerunUiAnalysisInnerAsync(int sessionId, string feedback, CancellationToken ct)
    {
        var key = SessionKey(sessionId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        try
        {
            if (session.UiStatus != ApiUiStatus.AwaitingUiAnalysisReview)
                throw new InvalidOperationException(
                    $"UiStatus must be AwaitingUiAnalysisReview to rerun UI analysis (was {session.UiStatus}).");

            var prior = session.UiAnalysisJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<QaLoopUiAnalystOutput>(session.UiAnalysisJson, UiAnalysisJsonOpts)
                  ?? throw new InvalidOperationException("UiAnalysisJson deserialization returned null.")
                : throw new InvalidOperationException("UiAnalysisJson missing — run UI Analyst first.");

            var llm = await _llmFactory.CreateAsync(ct);

            session.UiStatus = ApiUiStatus.AnalyzingUi;
            session.LastFeedback = feedback;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Pub(key, EventKinds.Phase, EventPhases.AnalyzeUi, "UI Analyst refine started");

            var analysis = await new QaLoopUiAnalystAgent(llm).RefineAsync(
                session.Module, prior, feedback,
                emit: EmitClosure(key, EventPhases.AnalyzeUi, session.ActivityEnabled),
                ct: ct);

            session.UiAnalysisJson = JsonSerializer.Serialize(analysis, UiAnalysisJsonOpts);
            session.UiStatus = ApiUiStatus.AwaitingUiAnalysisReview;
            Pub(key, EventKinds.System, EventPhases.AnalyzeUi,
                "UI Analyst refine done — awaiting UI analysis review", data: Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.UiStatus = ApiUiStatus.Cancelled;
            Pub(key, EventKinds.System, EventPhases.AnalyzeUi,
                "UI Analyst refine cancelled by user", EventLevels.Warn, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "QA Loop rerun-ui-analysis failed for session {SessionId}", sessionId);
            session.UiStatus = ApiUiStatus.Failed;
            session.ErrorMessage = e.Message;
            Pub(key, EventKinds.Error, EventPhases.AnalyzeUi,
                $"UI Analyst refine failed: {e.Message}", EventLevels.Error, Terminal());
            PersistLog(session, key);
            session.UpdatedAt = DateTime.UtcNow;
            await SafeCommitAsync(db);
        }
    }

    // ── Source fetch ─────────────────────────────────────────────────────────

    private async Task<string> FetchSourceAsync(
        ApiTestSession session, string key, IModuleSourceDiscoveryService? sourceDiscovery, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.Repository) || string.IsNullOrEmpty(session.Branch))
        {
            Pub(key, EventKinds.System, EventPhases.Fetch,
                "No Repository/Branch configured for this session — skipping code-aware source fetch " +
                "(the analyzer will only see the module name + any free-text context). Set them on the " +
                "scan-start form (or re-run with a repository/branch selected) to get real coverage analysis.",
                EventLevels.Warn);
            return $"Module: {session.Module}";
        }

        // Frontend-module-driven scan: seed with the module's frontend apis/ so the analyzer can read the
        // backend endpoints it calls and request the backend handlers via additionalFilesNeeded. Falls
        // back to the SourceDirs / module-name path below if the module has no apis/ folder.
        if (!string.IsNullOrEmpty(session.FrontendPath))
        {
            var seeded = await FetchFrontendApisAsync(session, key, ct);
            if (seeded is not null) return seeded;
            Pub(key, EventKinds.System, EventPhases.Fetch,
                $"No frontend api-client files found under '{session.FrontendPath}/apis' — falling back " +
                "to Source Dirs / module-name lookup. If the frontend folder layout moved, update the " +
                "module's Frontend Path (re-run the module scan) or supply Source Dirs explicitly.",
                EventLevels.Warn);
        }

        var sourceDirs = ParseSourceDirs(session.SourceDirsJson);

        try
        {
            var paths = new List<string>();
            var failedDirs = new List<string>();
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
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Skipping unreadable source dir {Dir}", dir);
                        failedDirs.Add($"{dir} ({ex.Message})");
                    }
                }
            }
            else
            {
                // Fallback: treat the module name itself as a top-level scopePath. Only correct when
                // the module happens to live at the repo root — supply SourceDirs for anything nested.
                try
                {
                    paths.AddRange(await _ado.ListRepoFilePathsAsync(session.Module, branch: session.Branch, repoId: session.Repository, ct));
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Module-name fallback path unreadable for {Module}", session.Module);
                    failedDirs.Add($"{session.Module} ({ex.Message})");
                }
            }

            if (failedDirs.Count > 0)
                Pub(key, EventKinds.System, EventPhases.Fetch,
                    $"Could not read {failedDirs.Count} source path(s) from ADO Git: {string.Join("; ", failedDirs.Take(3))}" +
                    (failedDirs.Count > 3 ? $" (+{failedDirs.Count - 3} more)" : "") +
                    ". If this is a 401/403, check the ADO PAT has the 'Code (Read)' scope.",
                    EventLevels.Warn);

            if (paths.Count == 0)
            {
                var discovered = sourceDiscovery is null ? null : await sourceDiscovery.DiscoverAsync(
                    session.Repository!, session.Branch!, session.Module,
                    session.FrontendPath, session.FrontendModule, ct);
                if (discovered is not null)
                {
                    session.DiscoveryJson = JsonSerializer.Serialize(discovered);
                    session.SourceDirsJson = JsonSerializer.Serialize(discovered.SourceDirectories);
                    Pub(key, EventKinds.Result, EventPhases.Fetch,
                        $"Smart discovery: {discovered.Explanation}",
                        EventLevels.Success, new()
                        {
                            ["frontendFiles"] = discovered.FrontendFiles.Count,
                            ["apiCalls"] = discovered.ApiCalls.Count,
                            ["backendFiles"] = discovered.BackendFiles.Count,
                            ["confidence"] = discovered.Confidence,
                        });
                    return discovered.SourceBundle;
                }
                Pub(key, EventKinds.System, EventPhases.Fetch,
                    $"No source files found for module '{session.Module}' under the configured paths " +
                    (sourceDirs.Count > 0 ? $"({string.Join(", ", sourceDirs)})" : $"('{session.Module}' as a top-level repo folder)") +
                    " — automatic branch discovery also found no usable module evidence. Check Source Dirs or Frontend Path.",
                    EventLevels.Warn);
                return $"Module: {session.Module}";
            }

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
            Pub(key, EventKinds.Error, EventPhases.Fetch,
                $"Source fetch failed for module '{session.Module}': {ex.Message}", EventLevels.Warn);
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
    private async Task<string?> FetchFrontendApisAsync(ApiTestSession session, string key, CancellationToken ct)
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
            Pub(key, EventKinds.System, EventPhases.Fetch,
                $"Frontend apis/ fetch failed for '{session.FrontendPath}': {ex.Message}" +
                " — check the ADO PAT scope (needs 'Code (Read)') and that the path still exists on the target branch.",
                EventLevels.Warn);
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

    /// <summary>Merge free-text AdditionalContext with scan-scoped markdown attachments.</summary>
    internal static string? FormatScanContext(ApiTestSession session)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.AdditionalContext))
            parts.Add(session.AdditionalContext.Trim());

        if (session.ContextAttachmentsJson is { Length: > 0 })
        {
            try
            {
                var attachments = JsonSerializer.Deserialize<List<ContextAttachmentDto>>(session.ContextAttachmentsJson);
                if (attachments is { Count: > 0 })
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("## Scan context attachments");
                    foreach (var a in attachments)
                    {
                        if (string.IsNullOrWhiteSpace(a.Content)) continue;
                        var path = string.IsNullOrWhiteSpace(a.RelativePath) ? "attachment.md" : a.RelativePath.Trim();
                        sb.AppendLine($"### {path}");
                        sb.AppendLine(a.Content.Trim());
                        sb.AppendLine();
                    }
                    if (sb.Length > 0)
                        parts.Add(sb.ToString().TrimEnd());
                }
            }
            catch { /* ignore malformed attachment JSON */ }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>KBG-108: merge scan context + session.GenerateContext with FRS RULES / FE↔BE / FeaturesJson + tag
    /// instructions when KbContextJson deserializes. System prompts stay frozen — enrichment is user-prompt only.</summary>
    internal static string? BuildGeneratorExtraContext(ApiTestSession session)
    {
        var parts = new List<string>();
        var scanCtx = FormatScanContext(session);
        if (!string.IsNullOrWhiteSpace(scanCtx))
            parts.Add(scanCtx);
        if (!string.IsNullOrWhiteSpace(session.GenerateContext))
            parts.Add(session.GenerateContext.Trim());

        FrsModuleContext? frs = null;
        if (session.KbContextJson is { Length: > 0 })
        {
            try { frs = JsonSerializer.Deserialize<FrsModuleContext>(session.KbContextJson); }
            catch { /* leave frs null — generate without KB enrichment */ }
        }

        if (frs is not null)
        {
            parts.Add(
                $"## Domain Rules (FRS: {frs.Slug})\n" +
                $"{frs.RulesMarkdown}\n\n" +
                $"## FE-BE Contracts\n" +
                $"{frs.FeBackLinksMarkdown}\n\n" +
                $"## Features and suggested packs\n" +
                $"{frs.FeaturesJson}\n" +
                "For each generated test: title format is " +
                "`[Module] - [FeatureName] - [Contract|Endpoint|Workflow] - [detail]` " +
                "(FeatureName from Features aliases when the route matches an api; include @smoke/@regression/@e2e per the feature's suggested pack).");
        }

        var importCtx = FormatImportedDraftsContext(session.Drafts);
        if (importCtx is not null)
            parts.Add(importCtx);

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>INT-204 — inject Plan-imported drafts so generators avoid duplicate ADO creates.</summary>
    internal static string? FormatImportedDraftsContext(IEnumerable<ApiTestDraft>? drafts)
    {
        if (drafts is null) return null;
        var imported = drafts
            .Where(d => d.AdoTestCaseId is not null || (d.Tags is not null && d.Tags.Contains("import-", StringComparison.Ordinal)))
            .OrderBy(d => d.SortOrder)
            .Take(80)
            .ToList();
        if (imported.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Existing Plan-imported drafts (do NOT recreate; improve thin ones in place)");
        foreach (var d in imported)
        {
            var kind = d.AdoTestCaseId is int id ? $"ado#{id}" : "gap";
            var steps = 0;
            try
            {
                if (JsonNode.Parse(d.StepsJson ?? "[]") is JsonArray arr) steps = arr.Count;
            }
            catch { /* ignore */ }
            sb.AppendLine($"- [{kind}] steps={steps} · {d.Title}");
        }
        return sb.ToString();
    }

    internal static List<LinkedTestCase> DraftsToLinkedCases(IEnumerable<ApiTestDraft> drafts) =>
        drafts
            .Where(d => d.AdoTestCaseId is not null)
            .Select(d => new LinkedTestCase(
                d.AdoTestCaseId!.Value,
                d.Title,
                d.Preconditions ?? "",
                ParseSteps(d.StepsJson)))
            .ToList();

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
                var unit = session.FrsSlug ?? session.Module;
                // QAL-067: a multi-case spec (Endpoint's 200/401/400) is ONE shared physical file for
                // all its cases — slug it from something identical across those cases (the route), not
                // from the per-case title, or only case 0's write would ever "stick" as the group's file.
                var slug = spec.SourceRoutes.Count > 0
                    ? ScriptPathAdapter.Slug(spec.SourceRoutes[0])
                    : ScriptPathAdapter.Slug(spec.TestCase?.Title ?? spec.Title);

                string specPath;
                string? schemaImportRel = null;
                string? schemaAbsPath = null;
                if (_scriptPaths.IsProductMode)
                {
                    specPath = _scriptPaths.ApiSpecPath(unit, level, slug);
                    schemaAbsPath = _scriptPaths.ApiSchemaPath(unit, slug);
                    schemaImportRel = $"../../schemas/{slug}.schema";
                    _scriptPaths.EnsureUnderRoot(specPath);
                }
                else
                {
                    var levelDir = Path.Combine(_scriptsDir, session.Module, level.ToDirName());
                    Directory.CreateDirectory(levelDir);
                    specPath = Path.Combine(levelDir, $"{slug}.spec.ts");
                }

                // QAL-071b: resolve the schema import against a filename WE control.
                var (specCode, schemaWritten) = RewriteSchemaImport(
                    specPath, slug, spec.SpecCode, spec.SchemaJson, session.Id,
                    schemaAbsPath, schemaImportRel);
                var tags = QaMetaHeader.ExtractTags(specCode);
                draft.Tags = tags.Count > 0 ? JsonSerializer.Serialize(tags) : null;
                specCode = QaMetaHeader.Upsert(specCode, new QaMeta(
                    session.FrsSlug ?? session.Module, "api", level.ToWire(), Bucket: null,
                    TcId: null, SourceDraftId: null, DateTime.UtcNow, QaMetaHeader.ComputeHash(specCode),
                    Tags: tags.Count > 0 ? tags : null));
                draft.SpecCode = specCode;

                // Only case 0 actually needs to write the (identical) shared file to disk; cases 1..N
                // still get SpecFilePath pointed at it so their push-time rewrite isn't a silent no-op.
                if (spec.CaseIndex is null or 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
                    File.WriteAllText(specPath, specCode);
                    if (_scriptPaths.IsProductMode)
                        TouchRegistry(unit, _scriptPaths.ToRootRelative(specPath));
                    Pub(SessionKey(session.Id), EventKinds.Result, EventPhases.Generate,
                        $"Spec written: {(_scriptPaths.IsProductMode ? _scriptPaths.ToRootRelative(specPath) : Path.GetFileName(specPath))}");
                }
                draft.SpecFilePath = specPath;
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

    /// <summary>Ensures the two-level suite structure the QA Loop pushes into: a module-named suite
    /// (e.g. "Deals") under the user-chosen <paramref name="parentSuiteId"/>, then plural level suites
    /// ("Contracts" / "Endpoints" / "Workflows") under the module suite. Find-or-create at each level,
    /// case-insensitive on direct children. Returns level → leaf-suiteId. Best-effort: any failure falls
    /// back to the chosen parent so the push still lands.</summary>
    private async Task<Dictionary<TestLevel, int>> EnsureLevelSuitesAsync(
        int planId, int parentSuiteId, string module, IEnumerable<TestLevel> levels, string? sessionKey, CancellationToken ct)
    {
        var map = new Dictionary<TestLevel, int>();
        IReadOnlyList<AdoTestSuite>? all = null;
        try { all = await _ado.GetTestSuitesAsync(planId, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not list suites for plan {PlanId}; will fall back to parent suite", planId); }

        int? moduleSuiteId = null;
        var moduleName = TitleTag.TitleCaseModule(module);
        try
        {
            var existingModule = all?.FirstOrDefault(s =>
                s.ParentId == parentSuiteId && string.Equals(s.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            if (existingModule is not null)
            {
                moduleSuiteId = existingModule.Id;
            }
            else
            {
                moduleSuiteId = await _ado.CreateStaticTestSuiteAsync(planId, parentSuiteId, moduleName, ct);
                if (sessionKey is not null)
                    Pub(sessionKey, EventKinds.Result, EventPhases.Push, $"Created '{moduleName}' suite (#{moduleSuiteId}) under suite {parentSuiteId}");
                // Re-list so child level suites can be matched against the freshly-created module suite.
                try { all = await _ado.GetTestSuitesAsync(planId, ct); } catch { /* keep going with stale list */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not ensure module suite '{Module}' under {ParentId}; every level will fall back to the parent", moduleName, parentSuiteId);
        }

        var levelParent = moduleSuiteId ?? parentSuiteId;
        foreach (var level in levels)
        {
            var name = LevelSuiteName(level);
            try
            {
                var existing = all?.FirstOrDefault(s =>
                    s.ParentId == levelParent && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    map[level] = existing.Id;
                    continue;
                }
                var newId = await _ado.CreateStaticTestSuiteAsync(planId, levelParent, name, ct);
                map[level] = newId;
                if (sessionKey is not null)
                    Pub(sessionKey, EventKinds.Result, EventPhases.Push, $"Created '{name}' sub-suite (#{newId}) under '{moduleName}'");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not ensure {Name} sub-suite under {LevelParent}; falling back to parent", name, levelParent);
                map[level] = parentSuiteId;
            }
        }
        return map;
    }

    /// <summary>Ensures Smoke/Regression/E2E sub-suites exist under the given UI level suite.
    /// Returns a case-insensitive map from bucket key ("smoke"/"regression"/"e2e") → suite id.
    /// Any sub-suite that fails to create falls back to <paramref name="uiSuiteId"/>.</summary>
    private async Task<Dictionary<string, int>> EnsureUiBucketSuitesAsync(
        int planId, int uiSuiteId, IReadOnlyList<AdoTestSuite>? allSuites, string? sessionKey, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var buckets = new[] { ("Smoke", "smoke"), ("Regression", "regression"), ("E2E", "e2e") };
        foreach (var (displayName, bucketKey) in buckets)
        {
            try
            {
                var existing = allSuites?.FirstOrDefault(s =>
                    s.ParentId == uiSuiteId && string.Equals(s.Name, displayName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    map[bucketKey] = existing.Id;
                    continue;
                }
                var newId = await _ado.CreateStaticTestSuiteAsync(planId, uiSuiteId, displayName, ct);
                map[bucketKey] = newId;
                if (sessionKey is not null)
                    Pub(sessionKey, EventKinds.Result, EventPhases.Push,
                        $"Created '{displayName}' bucket suite (#{newId}) under UI suite {uiSuiteId}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not ensure {Name} bucket suite under {UiSuiteId}; falling back to UI suite", displayName, uiSuiteId);
                map[bucketKey] = uiSuiteId;
            }
        }
        return map;
    }

    private static string LevelSuiteName(TestLevel level) => level switch
    {
        TestLevel.Contract => "Contracts",
        TestLevel.Endpoint => "Endpoints",
        TestLevel.Workflow => "Workflows",
        _ => "Ui",
    };

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
            var changed = RewriteTagText(text, tcId, draft.CaseIndex, out var updated);

            // QLV2-301: opportunistically fill in TcId now that it's known. Only re-stamps specs that
            // already carry a header (generated from this point forward) — a legacy header-less spec
            // stays unstamped here (backfill is out of scope for v1; it'll get one on its next refine).
            var priorMeta = QaMetaHeader.TryParse(updated);
            if (priorMeta is not null && priorMeta.TcId != tcId)
            {
                var body = QaMetaHeader.StripHeader(updated);
                updated = QaMetaHeader.Upsert(body, priorMeta with { TcId = tcId });
                changed = true;
            }

            if (changed && updated != text)
                File.WriteAllText(draft.SpecFilePath, updated);
        }
        catch { /* best-effort disk rewrite */ }
    }

    /// <summary>QAL-071: matches the one local relative import a generated spec uses for its schema,
    /// e.g. <c>from './get-lead-deal-dealId.schema'</c> or <c>from '../schemas/x.schema'</c> — captures
    /// the quote char (group 1) and the path (group 2). Package imports (@playwright/test,
    /// allure-playwright, ...) are never relative so they never match.</summary>
    private static readonly System.Text.RegularExpressions.Regex SchemaImportPattern =
        new(@"from\s+(['""])(\.\.?/[\w./-]+)\1", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>QAL-071b: the model's own relative import path is unreliable — it has invented
    /// same-dir ('./x') and parent-dir ('../schemas/x') conventions, sometimes with no matching
    /// "schema" field at all. A single unresolvable import aborts Playwright's test COLLECTION for
    /// every selected spec, not just the broken one, silently zeroing out an entire run. So we never
    /// trust the model's path: rewrite the spec's import to a filename WE control (same slug as the
    /// .spec.ts file) and write the schema there — spec and schema are then always mutually
    /// consistent regardless of what the model wrote. If an import exists but no schema content was
    /// returned, log/publish loudly instead of silently leaving a dangling import.</summary>
    private (string SpecCode, bool SchemaWritten) RewriteSchemaImport(
        string specPath, string slug, string specCode, string? schemaJson, int sessionId,
        string? productSchemaAbsPath = null, string? productImportRel = null)
    {
        var match = SchemaImportPattern.Match(specCode);
        if (!match.Success) return (specCode, false);

        if (string.IsNullOrEmpty(schemaJson))
        {
            _log.LogWarning(
                "Session {SessionId}: spec '{Slug}' imports a schema ({Import}) but generation returned no schema content",
                sessionId, slug, match.Groups[2].Value);
            Pub(SessionKey(sessionId), EventKinds.System, EventPhases.Generate,
                $"WARNING: '{slug}' imports a schema but none was generated — this spec will fail until regenerated.",
                EventLevels.Warn);
            return (specCode, false);
        }

        string schemaWritePath;
        string importPath;
        if (productSchemaAbsPath is not null && productImportRel is not null)
        {
            schemaWritePath = productSchemaAbsPath;
            importPath = productImportRel;
            _scriptPaths.EnsureUnderRoot(schemaWritePath);
        }
        else
        {
            var levelDir = Path.GetDirectoryName(specPath)!;
            schemaWritePath = Path.Combine(levelDir, $"{slug}.schema.ts");
            importPath = $"./{slug}.schema";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(schemaWritePath)!);
            File.WriteAllText(schemaWritePath, schemaJson);
            Pub(SessionKey(sessionId), EventKinds.Result, EventPhases.Generate,
                $"Schema written: {Path.GetFileName(schemaWritePath)}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write schema file '{Path}'", schemaWritePath);
            return (specCode, false);
        }

        var quote = match.Groups[1].Value;
        var replacement = $"from {quote}{importPath}{quote}";
        var rewritten = specCode[..match.Index] + replacement + specCode[(match.Index + match.Length)..];
        return (rewritten, true);
    }

    /// <summary>INT-302 — merge a spec path into <c>resources/registry.{unit}.json</c>.</summary>
    private void TouchRegistry(string unit, string rootRelativeSpec)
    {
        try
        {
            var regPath = _scriptPaths.RegistryPath(unit);
            Directory.CreateDirectory(Path.GetDirectoryName(regPath)!);
            var existing = File.Exists(regPath) ? File.ReadAllText(regPath) : null;
            var merged = RegistryFileMerge.Merge(existing, ScriptPathAdapter.Slug(unit), rootRelativeSpec);
            File.WriteAllText(regPath, merged);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Registry update failed for unit {Unit}", unit);
        }
    }

    /// <summary>INT-303 / L15 — ensure UI fixture dirs exist under automation/ (write-only).</summary>
    private void EnsureUiFixtures(string unit)
    {
        if (!_scriptPaths.IsProductMode || _scriptPaths.AutomationRoot is null) return;
        try
        {
            var root = _scriptPaths.AutomationRoot;
            var commonFixtures = Path.Combine(root, "common", "fixtures");
            Directory.CreateDirectory(commonFixtures);
            var marker = Path.Combine(commonFixtures, ".gitkeep");
            if (!File.Exists(marker))
                File.WriteAllText(marker, "");
            var unitUi = Path.Combine(root, "modules", ScriptPathAdapter.Slug(unit), "ui");
            Directory.CreateDirectory(Path.Combine(unitUi, "fixtures"));
            var authStub = Path.Combine(unitUi, "fixtures", "auth.setup.ts");
            if (!File.Exists(authStub))
            {
                File.WriteAllText(authStub,
                    "// <auto-generated> UI auth fixture placeholder — extend under automation/ only.\n" +
                    "import { test as setup } from '@playwright/test';\n" +
                    "setup('authenticate', async ({ page }) => {\n" +
                    "  // TODO: product login storageState\n" +
                    "});\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EnsureUiFixtures failed for {Unit}", unit);
        }
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
}
