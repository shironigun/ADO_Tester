using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Tests;

/// <summary>
/// Integration tests for the QA Loop pipeline (QAL-051). Drives QaLoopOrchestrator against a
/// real SQLite DB, a stub ADO client, and a stub LLM returning canned JSON.
/// </summary>
public sealed class QaLoopOrchestratorTests
{
    // ModuleAnalyzer output with an empty inventory — no generators will be invoked.
    private const string EmptyAnalyzerJson = """
        {"inventory":{"routes":[],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[]},"additionalFilesNeeded":[]}
        """;

    // Analyzer with 1 gap (GET /deals) + 1 covered (POST /deals) — only the gap triggers a generator.
    private const string OneGapAnalyzerJson = """
        {"inventory":{"routes":[
            {"method":"GET","route":"/deals","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]},
            {"method":"POST","route":"/deals","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]}
        ],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[
            {"id":"c1","level":"contract","label":"Contract GET /deals","status":"gap"},
            {"id":"c2","level":"contract","label":"Contract POST /deals","status":"covered"}
        ]},"additionalFilesNeeded":[]}
        """;

    // ContractGenerator LLM outputs (2 passes: spec+schema, then test-case array).
    private const string ContractSpecJson  = """{"spec":"","schema":"{}"}""";
    private const string ContractTcJson    = """
        [{"title":"[<tcId-pending>] Deals GET /deals contract","preconditions":"","steps":[{"action":"do x","expected":"see y"}]}]
        """;

    [Fact]
    public async Task Analyze_stops_at_AwaitingReview_when_AutoHandoff_is_false()
    {
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);
        Assert.NotNull(session.InventoryJson);
        Assert.NotNull(session.AnalysisJson);
    }

    [Fact]
    public async Task Analyze_with_AutoHandoff_reaches_Drafted()
    {
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: true);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.Drafted, session.Status);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        Assert.Empty(db.ApiTestDrafts.Where(d => d.SessionId == sid).ToList());   // empty surface → 0 drafts
    }

    [Fact]
    public async Task GenerateDrafts_creates_draft_only_for_selected_gap_surfaces()
    {
        // Analyze with OneGap → AwaitingReview (1 gap, 1 covered); then generate → only 1 draft
        using var h = Harness.Create(new StubLlmClient(OneGapAnalyzerJson, ContractSpecJson, ContractTcJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        await h.WaitForTerminalAsync(sid);    // AwaitingReview

        await h.Orchestrator.GenerateDraftsAsync(sid);
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        Assert.Equal(ApiSessionStatus.Drafted, session.Status);
        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = db.ApiTestDrafts.Where(d => d.SessionId == sid).ToList();
        var draft = Assert.Single(drafts);    // only the gap, not the covered surface
        Assert.Equal(ApiDraftStatus.Draft, draft.Status);
    }

    [Fact]
    public async Task Push_rewrites_pending_tag_and_sets_AdoTestCaseId()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: 1001);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] My first test case", ApiDraftStatus.Approved);
        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20, storyId: null);
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Equal(ApiSessionStatus.Done, session.Status);
        Assert.Single(h.Ado.CreatedTestCases);
        Assert.Single(h.Ado.SuiteAdds);
        Assert.Single(h.Ado.TestedByLinks);   // WorkItemId set → linked

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = db.ApiTestDrafts.Single(d => d.SessionId == sid);
        Assert.Equal(ApiDraftStatus.Pushed, draft.Status);
        Assert.NotNull(draft.AdoTestCaseId);
        Assert.DoesNotContain("[<tcId-pending>]", draft.Title);    // tag rewritten
        Assert.Contains($"[{draft.AdoTestCaseId}]", draft.Title);  // replaced with real id
    }

    [Fact]
    public async Task Push_skips_LinkTestedBy_when_no_WorkItemId()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] No story test", ApiDraftStatus.Approved);
        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);
        Assert.Empty(h.Ado.TestedByLinks);   // no story → no link
    }

    [Fact]
    public async Task Repress_push_skips_already_pushed_drafts()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        // Draft 1 already pushed; Draft 2 still approved
        await h.SeedDraftAsync(sid, "[123] Already pushed", ApiDraftStatus.Pushed, adoId: 123);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] Still pending", ApiDraftStatus.Approved);

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);   // only the Approved draft
        Assert.Single(h.Ado.SuiteAdds);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required QaLoopOrchestrator Orchestrator { get; init; }
        public required QaLoopAdoStub Ado { get; init; }
        private string DbPath { get; init; } = "";
        private string DataDir { get; init; } = "";

        public static Harness Create(StubLlmClient llm)
        {
            var dbPath  = Path.Combine(Path.GetTempPath(), $"qa_loop_{Guid.NewGuid():N}.db");
            var dataDir = Path.Combine(Path.GetTempPath(), $"qa_loop_data_{Guid.NewGuid():N}");

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
            services.AddScoped<DatabaseInitializer>();
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var ado = new QaLoopAdoStub();
            var orchestrator = new QaLoopOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EventBus(),
                new PipelineConcurrency(),
                new CancellationRegistry(),
                ado,
                new StubLlmClientFactory(llm),
                Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }),
                Options.Create(new QaLoopOptions { ScriptsFolderName = "scripts", RunsFolderName = "runs" }),
                NullLogger<QaLoopOrchestrator>.Instance);

            return new Harness { Provider = provider, Orchestrator = orchestrator, Ado = ado, DbPath = dbPath, DataDir = dataDir };
        }

        public async Task<int> SeedSessionAsync(
            string module, bool autoHandoff = false, int? workItemId = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var session = new ApiTestSession
            {
                ProjectId = 1,
                Module = module,
                AutoHandoff = autoHandoff,
                WorkItemId = workItemId,
                Status = ApiSessionStatus.Analyzing,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        }

        public async Task SeedDraftAsync(
            int sessionId, string title, ApiDraftStatus status, int? adoId = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            db.ApiTestDrafts.Add(new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"do x\",\"expected\":\"see y\"}]",
                Status = status,
                AdoTestCaseId = adoId,
                Level = TestLevel.Contract,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public async Task<ApiTestSession> WaitForTerminalAsync(
            int sessionId, ApiSessionStatus? expected = null)
        {
            ApiSessionStatus[] terminal =
            [
                ApiSessionStatus.Done, ApiSessionStatus.Drafted, ApiSessionStatus.AwaitingReview,
                ApiSessionStatus.Failed, ApiSessionStatus.Cancelled,
            ];
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                var session = await db.ApiTestSessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == sessionId);
                if (session is not null && (expected is { } e
                        ? session.Status == e : terminal.Contains(session.Status)))
                    return session;
                await Task.Delay(40);
            }
            throw new TimeoutException($"Session {sessionId} did not reach terminal status in time.");
        }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { File.Delete(DbPath + s); } catch { }
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }

    private sealed class StubLlmClientFactory : ILlmClientFactory
    {
        private readonly ILlmClient _client;
        public StubLlmClientFactory(ILlmClient client) => _client = client;
        public Task<ILlmClient> CreateAsync(CancellationToken ct = default) => Task.FromResult(_client);
    }

    /// <summary>Stub ADO client for QA Loop push tests: records write calls; reads throw.</summary>
    internal sealed class QaLoopAdoStub : IAzureDevOpsClient
    {
        private int _nextTcId = 7000;
        public List<string> CreatedTestCases { get; } = [];
        public List<(int PlanId, int SuiteId, int TcId)> SuiteAdds { get; } = [];
        public List<(int StoryId, int TcId)> TestedByLinks { get; } = [];

        public Task<int> CreateTestCaseAsync(
            string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default)
        {
            CreatedTestCases.Add(title);
            return Task.FromResult(_nextTcId++);
        }

        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int tcId, CancellationToken ct = default)
        {
            SuiteAdds.Add((planId, suiteId, tcId));
            return Task.CompletedTask;
        }

        public Task LinkTestedByAsync(int storyId, int tcId, CancellationToken ct = default)
        {
            TestedByLinks.Add((storyId, tcId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);

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
}
