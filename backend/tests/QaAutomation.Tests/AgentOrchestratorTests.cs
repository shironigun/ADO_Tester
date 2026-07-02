using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Tests;

/// <summary>
/// Integration tests for the Author-Tests pipeline (B5b). Drives <see cref="AgentOrchestrator"/>
/// against a real SQLite DB + KB, a stub ADO client, and a stub LLM returning canned Analyst/Tester
/// JSON, then waits for the background pipeline to reach a terminal status.
/// </summary>
public sealed class AgentOrchestratorTests
{
    private const string AnalystJson =
        """
        {"module":"Search","feature":"VIN search","summary":"s","happyPath":"hp",
         "acceptanceCriteria":["AC1","AC2"],"businessRules":["BR1"],"edgeCases":["E1"],
         "negativeCases":["N1","N2"],"permissions":[],"crossFlow":[],"openQuestions":[]}
        """;

    private const string TesterJson =
        """
        [{"title":"TC one","preconditions":"","steps":[{"action":"do x","expected":"see y"}]},
         {"title":"TC two","preconditions":"","steps":[]}]
        """;

    [Fact]
    public async Task Auto_handoff_runs_analyst_then_tester_to_drafted()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson, TesterJson));

        var sid = await h.Orchestrator.StartAsync(projectId: 1, workItemId: 160728, autoHandoff: true);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(SessionStatus.Drafted, session.Status);
        Assert.Equal("WI-160728", session.WorkItemTitle);   // from the stub ADO work item
        Assert.Contains("Drafted 2 test case", session.CurrentActivity);
        Assert.False(string.IsNullOrEmpty(session.AnalysisJson));
        Assert.Contains("\"feature\":\"VIN search\"", session.AnalysisJson);
        Assert.False(string.IsNullOrEmpty(session.EventLogJson));   // narrative persisted
        Assert.Contains("Drafted", session.EventLogJson!);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = db.TestCaseDrafts.Where(d => d.SessionId == sid).OrderBy(d => d.SortOrder).ToList();
        Assert.Equal(2, drafts.Count);
        Assert.Equal("TC one", drafts[0].Title);
        Assert.Equal(DraftStatus.Draft, drafts[0].Status);
        Assert.Contains("\"action\":\"do x\"", drafts[0].StepsJson);
    }

    [Fact]
    public async Task No_auto_handoff_stops_at_awaiting_tester()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson));

        var sid = await h.Orchestrator.StartAsync(projectId: 1, workItemId: 5, autoHandoff: false);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(SessionStatus.AwaitingTester, session.Status);
        Assert.False(string.IsNullOrEmpty(session.AnalysisJson));

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        Assert.Equal(0, db.TestCaseDrafts.Count(d => d.SessionId == sid));
    }

    [Fact]
    public async Task Push_creates_links_and_marks_done()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson, TesterJson));

        var sid = await h.Orchestrator.StartAsync(projectId: 1, workItemId: 160728, autoHandoff: true);
        await h.WaitForTerminalAsync(sid);

        // Approve both drafts, then push.
        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            foreach (var d in db.TestCaseDrafts.Where(d => d.SessionId == sid))
                d.Status = DraftStatus.Approved;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.PushToAdoAsync(sid, testPlanId: 10, testSuiteId: 20);
        var session = await h.WaitForTerminalAsync(sid, SessionStatus.Done);

        Assert.Equal(SessionStatus.Done, session.Status);
        Assert.Contains("Pushed 2 test case", session.CurrentActivity);
        Assert.Equal(2, h.Ado.CreatedTestCases.Count);
        Assert.Equal(2, h.Ado.SuiteAdds.Count);
        Assert.Equal(2, h.Ado.TestedByLinks.Count);
        Assert.All(h.Ado.SuiteAdds, x => Assert.Equal((10, 20), (x.PlanId, x.SuiteId)));

        using var verify = h.Provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = vdb.TestCaseDrafts.Where(d => d.SessionId == sid).ToList();
        Assert.All(drafts, d => Assert.Equal(DraftStatus.Pushed, d.Status));
        Assert.All(drafts, d => Assert.NotNull(d.AdoTestCaseId));
    }

    [Fact]
    public async Task Push_with_no_approved_drafts_fails()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson, TesterJson));

        var sid = await h.Orchestrator.StartAsync(projectId: 1, workItemId: 160728, autoHandoff: true);
        await h.WaitForTerminalAsync(sid);   // drafts exist but remain in Draft (not Approved)

        await h.Orchestrator.PushToAdoAsync(sid, 10, 20);
        var session = await h.WaitForTerminalAsync(sid, SessionStatus.Failed);

        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal("No approved drafts to push.", session.ErrorMessage);
        Assert.Empty(h.Ado.CreatedTestCases);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required AgentOrchestrator Orchestrator { get; init; }
        public required StubAdoClient Ado { get; init; }
        private string DbPath { get; init; } = "";
        private string DataDir { get; init; } = "";

        public static Harness Create(StubLlmClient llm)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"qa_orch_{Guid.NewGuid():N}.db");
            var dataDir = Path.Combine(Path.GetTempPath(), $"qa_orch_data_{Guid.NewGuid():N}");

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
            services.AddScoped<DatabaseInitializer>();
            services.AddScoped<IKnowledgebaseService, KnowledgebaseService>();
            services.AddSingleton(Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }));
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var ado = new StubAdoClient();
            var orchestrator = new AgentOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EventBus(),
                new PipelineConcurrency(),
                new CancellationRegistry(),
                ado,
                new StubLlmClientFactory(llm),
                NullLogger<AgentOrchestrator>.Instance);

            return new Harness { Provider = provider, Orchestrator = orchestrator, Ado = ado, DbPath = dbPath, DataDir = dataDir };
        }

        public async Task<TestSession> WaitForTerminalAsync(int sessionId, SessionStatus? expected = null)
        {
            var terminal = new[] { SessionStatus.Done, SessionStatus.Drafted, SessionStatus.AwaitingTester, SessionStatus.Failed, SessionStatus.Cancelled };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                var session = await db.TestSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
                if (session is not null && (expected is { } e ? session.Status == e : terminal.Contains(session.Status)))
                    return session;
                await Task.Delay(40);
            }
            throw new TimeoutException($"Session {sessionId} did not reach {(expected?.ToString() ?? "a terminal status")} in time.");
        }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(DbPath + s); } catch { /* best effort */ }
            }
            try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class StubLlmClientFactory : ILlmClientFactory
    {
        private readonly ILlmClient _client;
        public StubLlmClientFactory(ILlmClient client) => _client = client;
        public Task<ILlmClient> CreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(_client);
    }

    /// <summary>Records the four write calls the Author-Tests push exercises; the read returns a
    /// synthetic work item. Everything else throws (not reached by these tests).</summary>
    internal sealed class StubAdoClient : IAzureDevOpsClient
    {
        private int _nextTestCaseId = 5000;
        public List<(string Title, string Preconditions, IReadOnlyList<TestCaseStep> Steps)> CreatedTestCases { get; } = [];
        public List<(int PlanId, int SuiteId, int TestCaseId)> SuiteAdds { get; } = [];
        public List<(int StoryId, int TestCaseId)> TestedByLinks { get; } = [];

        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new AdoWorkItem
            {
                Id = workItemId,
                Title = $"WI-{workItemId}",
                WorkItemType = "User Story",
                AcceptanceCriteria = "AC text",
            });

        public Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default)
        {
            CreatedTestCases.Add((title, preconditions, steps));
            return Task.FromResult(_nextTestCaseId++);
        }

        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int testCaseId, CancellationToken ct = default)
        {
            SuiteAdds.Add((planId, suiteId, testCaseId));
            return Task.CompletedTask;
        }

        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default)
        {
            TestedByLinks.Add((storyId, testCaseId));
            return Task.CompletedTask;
        }

        // ── unused in these tests ────────────────────────────────────────────
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default) => throw new NotImplementedException();
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
        public Task<IReadOnlyDictionary<int, int>> GetAdoTestResultIdsAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAdoTestResultsAsync(int adoTestRunId, IReadOnlyList<AdoTestResultUpdate> updates, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(int pipelineId, int? minBuildId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(int buildId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
