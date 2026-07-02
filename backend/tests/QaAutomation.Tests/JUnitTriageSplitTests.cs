using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.QaPipelines;
using QaAutomation.Infrastructure.Results;

namespace QaAutomation.Tests;

/// <summary>
/// Contract tests for TriageAsync/FileBugsAsync split and PipelineWatcherHostedService impact analysis (QAL-053).
/// </summary>
public sealed class JUnitTriageSplitTests
{
    // Valid FailureTriageAgent JSON for a real_bug classification.
    private const string RealBugTriageJson = """
        {"classification":"real_bug","confidence":"high","reason":"checkout returns 500",
         "bug":{"title":"Checkout 500","severity":"High","preconditions":"logged in",
                "reproSteps":["open cart","click checkout"],"expected":"order created",
                "actual":"500 error on checkout"},
         "observations":["500 error on checkout"]}
        """;

    // Valid ImpactAnalysis JSON pointing at a specific draft id.
    private static string ImpactJson(int draftId) =>
        $$$"""{"impactedDraftIds":[{{{draftId}}}],"reasoning":"route changed","confidence":"high"}""";

    // ── DI composition (QAL-063) ─────────────────────────────────────────────
    // AddHostedService<T>() alone only exposes T as IHostedService, never as itself — the exact gap
    // that crashed every QaPipelinesController action (it injects the concrete type). Mirrors the
    // fixed registration in DependencyInjection.AddQaLoop.

    [Fact]
    public void PipelineWatcherHostedService_resolves_both_as_itself_and_as_the_same_IHostedService_instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IAzureDevOpsClient>(new WatcherAdoStub([]));
        services.AddSingleton<ILlmClientFactory>(new FixedLlmFactory(null!));
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IOptions<QaLoopOptions>>(Options.Create(new QaLoopOptions()));

        services.AddSingleton<PipelineWatcherHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PipelineWatcherHostedService>());

        using var provider = services.BuildServiceProvider();

        var direct = provider.GetRequiredService<PipelineWatcherHostedService>();
        var hosted = provider.GetServices<IHostedService>().OfType<PipelineWatcherHostedService>().Single();
        Assert.Same(direct, hosted);
    }

    // ── testDir level-folder naming (QAL-062 follow-up) ──────────────────────
    // PlaywrightRunner.WriteRunConfig resolves a run's testDir from a caller-supplied levelFilter
    // string of arbitrary casing; it must land on the exact folder QaLoopOrchestrator.PersistDraft
    // wrote the specs to, or the run silently matches zero test files (no error, no report — a run
    // that looks "Passed" with 0/0/0 counts and no junit.xml).

    [Theory]
    [InlineData("Contract", "contracts")]
    [InlineData("contract", "contracts")]
    [InlineData("ENDPOINT", "endpoints")]
    [InlineData("workflow", "workflows")]
    public void TestLevel_ToDirName_matches_regardless_of_caller_casing(string input, string expectedDir)
    {
        Assert.True(Enum.TryParse<TestLevel>(input, ignoreCase: true, out var level));
        Assert.Equal(expectedDir, level.ToDirName());
    }

    // ── TriageAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TriageAsync_sets_BugDraftJson_for_real_bug_without_calling_CreateBugAsync()
    {
        using var h = IngestHarness.Create(new StubLlmClient(RealBugTriageJson));
        var (runId, failureId) = await h.SeedRunAsync("Checkout throws 500");

        var result = await h.Service.TriageAsync(runId);

        Assert.Equal(1, result.TriagedCount);
        Assert.Equal(1, result.RealBugCount);
        Assert.Equal(0, h.Ado.BugCreateCount);   // draft-only — NOT filed

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var failure = db.TestFailures.Single(f => f.Id == failureId);
        Assert.NotNull(failure.BugDraftJson);
        Assert.Contains("Checkout 500", failure.BugDraftJson);
        Assert.Null(failure.BugId);
    }

    [Fact]
    public async Task TriageAsync_skips_failures_that_already_have_BugDraftJson()
    {
        using var h = IngestHarness.Create(new StubLlmClient());   // empty — must not be called
        var (runId, _) = await h.SeedRunAsync("Already triaged", bugDraftJson: "{}");

        var result = await h.Service.TriageAsync(runId);

        Assert.Equal(0, result.TriagedCount);
        Assert.Equal(0, result.RealBugCount);
        Assert.Equal(0, h.Ado.BugCreateCount);
    }

    // ── FileBugsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FileBugsAsync_calls_CreateBugAsync_once_per_selected_failure_without_LLM()
    {
        const string draft = """
            {"Title":"Checkout 500","Severity":"High","Preconditions":"",
             "ReproSteps":["click checkout"],"Expected":"order created","Observations":["500 error"]}
            """;

        using var h = IngestHarness.Create(new StubLlmClient());   // factory not invoked
        var (runId, failureId) = await h.SeedRunAsync("Checkout throws 500", bugDraftJson: draft);

        var result = await h.Service.FileBugsAsync(runId, [failureId], storyId: null);

        Assert.Equal(1, result.FiledCount);
        Assert.Single(result.BugIds);
        Assert.Equal(1, h.Ado.BugCreateCount);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var failure = db.TestFailures.Single(f => f.Id == failureId);
        Assert.NotNull(failure.BugId);
        Assert.Equal(h.Ado.LastBugId, failure.BugId);
    }

    // ── PipelineWatcherHostedService.RunLatestAsync ───────────────────────────

    [Fact]
    public async Task Watcher_empty_inventory_creates_Cancelled_run()
    {
        var build = new AdoBuild(5, "sha5", "repo1", DateTime.UtcNow);
        using var h = WatcherHarness.Create(new StubLlmClient(), builds: [build]);
        var watchId = await h.SeedWatchAsync("Deals", pipelineId: 99);
        // No pushed drafts → inventory empty → Cancelled

        var runId = await h.Watcher.RunLatestAsync(watchId);

        Assert.Null(runId);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var run = db.TestRuns.Single();
        Assert.Equal(TestRunStatus.Cancelled, run.Status);
        Assert.Equal("no impacted tests", run.ErrorMessage);
    }

    [Fact]
    public async Task Watcher_malformed_impact_json_falls_back_to_all_pushed_draft_ids()
    {
        var build = new AdoBuild(7, "sha7", "repo1", DateTime.UtcNow);
        using var h = WatcherHarness.Create(new StubLlmClient("not valid json {{"), builds: [build]);
        var watchId = await h.SeedWatchAsync("Deals", pipelineId: 99);
        var draftId = await h.SeedPushedDraftAsync("Deals");

        var runId = await h.Watcher.RunLatestAsync(watchId);

        Assert.NotNull(runId);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var run = db.TestRuns.Single(r => r.Id == runId);
        Assert.Equal(TestRunStatus.Queued, run.Status);
        Assert.NotNull(run.ImpactAnalysisJson);

        var impact = JsonSerializer.Deserialize<ImpactAnalysis>(run.ImpactAnalysisJson!)!;
        Assert.True(impact.Fallback);
        Assert.Contains(draftId, impact.ImpactedDraftIds);
    }

    [Fact]
    public async Task Watcher_valid_impact_produces_Queued_run_and_updates_LastSeenBuildId()
    {
        var build = new AdoBuild(11, "sha11", "repo1", DateTime.UtcNow);
        using var h = WatcherHarness.Create(new StubLlmClient("placeholder"), builds: [build]);
        var watchId = await h.SeedWatchAsync("Deals", pipelineId: 99);
        var draftId = await h.SeedPushedDraftAsync("Deals");
        h.SetLlm(new StubLlmClient(ImpactJson(draftId)));

        var runId = await h.Watcher.RunLatestAsync(watchId);

        Assert.NotNull(runId);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var run = db.TestRuns.Single(r => r.Id == runId);
        Assert.Equal(TestRunStatus.Queued, run.Status);
        Assert.NotNull(run.ImpactAnalysisJson);

        var impact = JsonSerializer.Deserialize<ImpactAnalysis>(run.ImpactAnalysisJson!)!;
        Assert.False(impact.Fallback);
        Assert.Contains(draftId, impact.ImpactedDraftIds);

        var watch = db.PipelineWatches.Single(w => w.Id == watchId);
        Assert.Equal(build.Id, watch.LastSeenBuildId);
    }

    // ── IngestHarness ─────────────────────────────────────────────────────────

    private sealed class IngestHarness : IDisposable
    {
        public ServiceProvider Provider { get; }
        public ResultsIngestService Service { get; }
        public IngestAdoStub Ado { get; }
        private readonly IServiceScope _svcScope;
        private readonly string _dbPath;

        private IngestHarness(ServiceProvider provider, ResultsIngestService svc, IngestAdoStub ado,
            IServiceScope svcScope, string dbPath)
        {
            Provider = provider; Service = svc; Ado = ado; _svcScope = svcScope; _dbPath = dbPath;
        }

        public static IngestHarness Create(StubLlmClient llm)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"qa_triage_{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
            services.AddScoped<DatabaseInitializer>();
            var provider = services.BuildServiceProvider();

            using (var init = provider.CreateScope())
                init.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var ado = new IngestAdoStub();
            var svcScope = provider.CreateScope();
            var db = svcScope.ServiceProvider.GetRequiredService<QaDbContext>();
            var svc = new ResultsIngestService(
                new FixedLlmFactory(llm), new NullKb(), ado, db,
                NullLogger<ResultsIngestService>.Instance);

            return new IngestHarness(provider, svc, ado, svcScope, dbPath);
        }

        public async Task<(int runId, int failureId)> SeedRunAsync(string testTitle, string? bugDraftJson = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var run = new TestRun { ProjectId = 1, Status = TestRunStatus.Running, CreatedAt = DateTime.UtcNow };
            db.TestRuns.Add(run);
            await db.SaveChangesAsync();
            var failure = new TestFailure { RunId = run.Id, TestTitle = testTitle, BugDraftJson = bugDraftJson };
            db.TestFailures.Add(failure);
            await db.SaveChangesAsync();
            return (run.Id, failure.Id);
        }

        public void Dispose()
        {
            _svcScope.Dispose();
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { File.Delete(_dbPath + s); } catch { }
        }
    }

    // ── WatcherHarness ────────────────────────────────────────────────────────

    private sealed class WatcherHarness : IDisposable
    {
        public ServiceProvider Provider { get; }
        public PipelineWatcherHostedService Watcher { get; }
        private readonly SettableLlmFactory _factory;
        private readonly string _dbPath;

        private WatcherHarness(ServiceProvider provider, PipelineWatcherHostedService watcher,
            SettableLlmFactory factory, string dbPath)
        {
            Provider = provider; Watcher = watcher; _factory = factory; _dbPath = dbPath;
        }

        public static WatcherHarness Create(StubLlmClient llm, IReadOnlyList<AdoBuild> builds)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"qa_watch_{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
            services.AddScoped<DatabaseInitializer>();
            var provider = services.BuildServiceProvider();

            using (var init = provider.CreateScope())
                init.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var factory = new SettableLlmFactory(llm);
            var ado = new WatcherAdoStub(builds);
            var watcher = new PipelineWatcherHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                ado,
                factory,
                new EventBus(),
                Options.Create(new QaLoopOptions()),
                NullLogger<PipelineWatcherHostedService>.Instance);

            return new WatcherHarness(provider, watcher, factory, dbPath);
        }

        public void SetLlm(StubLlmClient llm) => _factory.Client = llm;

        public async Task<int> SeedWatchAsync(string module, int pipelineId)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var watch = new PipelineWatch
            {
                ProjectId = 1, Module = module, AdoPipelineId = pipelineId,
                AdoPipelineName = "Test Pipeline", Enabled = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.PipelineWatches.Add(watch);
            await db.SaveChangesAsync();
            return watch.Id;
        }

        /// <summary>Seeds an ApiTestSession + a Pushed ApiTestDraft for the given module; returns the draft id.</summary>
        public async Task<int> SeedPushedDraftAsync(string module)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var session = new ApiTestSession
            {
                ProjectId = 1, Module = module, Status = ApiSessionStatus.Done,
                AutoHandoff = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestSessions.Add(session);
            await db.SaveChangesAsync();

            var draft = new ApiTestDraft
            {
                SessionId = session.Id, Title = "[1001] Deals GET /deals contract",
                Preconditions = "", StepsJson = "[]",
                Status = ApiDraftStatus.Pushed, Level = TestLevel.Contract,
                AdoTestCaseId = 1001, SourceRoutesJson = """["GET /deals"]""",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { File.Delete(_dbPath + s); } catch { }
        }
    }

    // ── stubs ──────────────────────────────────────────────────────────────────

    private sealed class IngestAdoStub : IAzureDevOpsClient
    {
        private int _nextBugId = 5000;
        public int BugCreateCount { get; private set; }
        public int? LastBugId { get; private set; }

        public Task<int> CreateBugAsync(
            string title, string severity, string preconditions, IReadOnlyList<string> reproSteps,
            string expected, string actual, string suggestedFix,
            string systemInfo = "", string reproHtml = "", string foundWith = "Manual Test",
            CancellationToken ct = default)
        {
            BugCreateCount++;
            LastBugId = _nextBugId;
            return Task.FromResult(_nextBugId++);
        }

        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default) => Task.CompletedTask;

        // ── unused ──
        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int tcId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkTestedByAsync(int storyId, int tcId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, IReadOnlyList<string>? types = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddWorkItemAttachmentLinkAsync(int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitRefs> GetWorkItemGitRefsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedChange>> GetLinkedChangesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetCommitChangedFilesAsync(string sha, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetPullRequestDiffAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetCommitDiffAsync(string sha, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetCommitsForPathAsync(string path, int top = 5, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetFileContentAsync(string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListRepoFilePathsAsync(string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAdoTestRunAsync(int planId, int suiteId, string name, IReadOnlyList<int> pointIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetAdoTestResultAsync(int adoTestRunId, int testPointId, string outcome, string? errorMessage, long durationMs, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(int pipelineId, int? minBuildId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(int buildId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class WatcherAdoStub : IAzureDevOpsClient
    {
        private readonly IReadOnlyList<AdoBuild> _builds;
        public WatcherAdoStub(IReadOnlyList<AdoBuild> builds) => _builds = builds;

        public Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(int pipelineId, int? minBuildId = null, CancellationToken ct = default)
            => Task.FromResult(_builds);

        public Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(int buildId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AdoBuildChange>>([new AdoBuildChange("abc123", "fix: update deals endpoint")]);

        public Task<IReadOnlyList<string>> GetCommitChangedFilesAsync(string sha, string? repoId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["src/Deals/DealController.cs"]);

        // ── unused ──
        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int tcId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkTestedByAsync(int storyId, int tcId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, IReadOnlyList<string>? types = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateBugAsync(string title, string severity, string preconditions, IReadOnlyList<string> reproSteps, string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "", string foundWith = "Manual Test", CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddWorkItemAttachmentLinkAsync(int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitRefs> GetWorkItemGitRefsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedChange>> GetLinkedChangesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetPullRequestDiffAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetCommitDiffAsync(string sha, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetCommitsForPathAsync(string path, int top = 5, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetFileContentAsync(string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListRepoFilePathsAsync(string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAdoTestRunAsync(int planId, int suiteId, string name, IReadOnlyList<int> pointIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetAdoTestResultAsync(int adoTestRunId, int testPointId, string outcome, string? errorMessage, long durationMs, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FixedLlmFactory : ILlmClientFactory
    {
        private readonly ILlmClient _client;
        public FixedLlmFactory(ILlmClient client) => _client = client;
        public Task<ILlmClient> CreateAsync(CancellationToken ct = default) => Task.FromResult(_client);
    }

    private sealed class SettableLlmFactory : ILlmClientFactory
    {
        public ILlmClient Client { get; set; }
        public SettableLlmFactory(ILlmClient client) => Client = client;
        public Task<ILlmClient> CreateAsync(CancellationToken ct = default) => Task.FromResult(Client);
    }

    private sealed class NullKb : IKnowledgebaseService
    {
        public Task<KnowledgebaseDocument> UploadAsync(int projectId, string fileName, string markdown, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(int projectId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgebaseDocument>>([]);
        public Task DeleteAsync(int documentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(int projectId, string query, int topK = 8, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgebaseHit>>([]);
    }
}
