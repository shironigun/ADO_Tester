using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Knowledgebase;
using QaAutomation.Infrastructure.Orchestrators;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

/// <summary>
/// Integration tests for the Find-Bugs pipeline (B5c). Drives <see cref="BugOrchestrator"/> against a
/// real SQLite DB, a stub ADO client (repo-resolve throws → discovery no-ops; no linked test cases),
/// and a stub LLM returning canned Analyst / verdict / bug JSON.
/// </summary>
public sealed class BugOrchestratorTests
{
    private const string AnalystJson =
        """{"module":"Search","feature":"VIN search","summary":"s","happyPath":"hp","acceptanceCriteria":["AC1"],"edgeCases":["E1"],"negativeCases":[],"businessRules":[],"permissions":[],"crossFlow":[],"openQuestions":[]}""";
    private const string EmptyVerdicts = "[]";
    private const string BugsJson =
        """[{"title":"Stale rows after edit","severity":"High","preconditions":"","reproSteps":["Edit a customer","Search within 30s"],"expected":"Fresh row","actual":"Stale row ~30s","suggestedFix":"Invalidate the search cache on write"}]""";

    [Fact]
    public async Task Start_parks_at_awaiting_actual()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson));

        var sid = await h.Bugs.StartAsync(projectId: 1, workItemId: 160728, auto: false);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(BugSessionStatus.AwaitingActual, session.Status);
        Assert.False(string.IsNullOrEmpty(session.AnalysisJson));
        using var scope = h.Provider.CreateScope();
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<QaDbContext>().BugDrafts.Count(d => d.SessionId == sid));
    }

    [Fact]
    public async Task Run_bug_writer_verifies_and_drafts()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson, EmptyVerdicts, BugsJson));

        var sid = await h.Bugs.StartAsync(1, 160728, auto: false);
        await h.WaitForTerminalAsync(sid);

        await h.Bugs.RunBugWriterAsync(sid, "Observed: stale rows linger ~30s", null, verifyCode: true);
        var session = await h.WaitForTerminalAsync(sid, BugSessionStatus.Drafted);

        Assert.Equal(BugSessionStatus.Drafted, session.Status);
        Assert.Contains("Drafted 1 bug", session.CurrentActivity);
        Assert.Equal("[]", session.CodeVerificationJson);   // 0 test cases verified → empty verdicts

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = Assert.Single(db.BugDrafts.Where(d => d.SessionId == sid));
        Assert.Equal("Stale rows after edit", draft.Title);
        Assert.Equal(BugSeverity.High, draft.Severity);
        Assert.Contains("Edit a customer", draft.ReproStepsJson);
    }

    [Fact]
    public async Task Push_creates_bugs_and_links_to_story()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson, EmptyVerdicts, BugsJson));

        var sid = await h.Bugs.StartAsync(1, 160728, auto: false);
        await h.WaitForTerminalAsync(sid);
        await h.Bugs.RunBugWriterAsync(sid, "Observed: stale", null, verifyCode: true);
        await h.WaitForTerminalAsync(sid, BugSessionStatus.Drafted);

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            foreach (var d in db.BugDrafts.Where(d => d.SessionId == sid))
                d.Status = BugDraftStatus.Approved;
            await db.SaveChangesAsync();
        }

        await h.Bugs.PushToAdoAsync(sid);
        var session = await h.WaitForTerminalAsync(sid, BugSessionStatus.Done);

        Assert.Equal(BugSessionStatus.Done, session.Status);
        Assert.Contains("Pushed 1 bug", session.CurrentActivity);
        var bug = Assert.Single(h.Ado.CreatedBugs);
        Assert.Equal("High", bug.Severity);   // enum name passed to ADO
        var link = Assert.Single(h.Ado.RelatedLinks);

        using var verify = h.Provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<QaDbContext>();
        var pushedDraft = Assert.Single(vdb.BugDrafts.Where(d => d.SessionId == sid));
        Assert.Equal(BugDraftStatus.Pushed, pushedDraft.Status);
        Assert.Equal(link.From, pushedDraft.AdoBugId);   // create_bug id → Related link fromId
    }

    [Fact]
    public async Task Create_bug_from_verdict_instant_adds_a_draft()
    {
        using var h = Harness.Create(new StubLlmClient(AnalystJson));

        var sid = await h.Bugs.StartAsync(1, 160728, auto: false);
        await h.WaitForTerminalAsync(sid);   // AwaitingActual — creating a bug is allowed

        var verdict = new JsonObject
        {
            ["mode"] = "instant",
            ["test_case"] = "Search returns fresh row",
            ["severity"] = "High",
            ["failed_step_index"] = 0,
            ["steps"] = new JsonArray(new JsonObject
            {
                ["action"] = "Edit then search",
                ["expected"] = "Fresh row",
                ["status"] = "fail",
                ["actual"] = "Stale row",
            }),
        };

        var draftId = await h.Bugs.CreateBugFromVerdictAsync(sid, verdict);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = db.BugDrafts.Single(d => d.Id == draftId);
        Assert.Equal("Search returns fresh row", draft.Title);
        Assert.Equal(BugSeverity.High, draft.Severity);
        Assert.Contains("Edit then search", draft.ReproStepsJson);
        Assert.Equal("Stale row", draft.ActualBehavior);
        Assert.Equal(BugSessionStatus.Drafted, db.BugSessions.Single(s => s.Id == sid).Status);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required BugOrchestrator Bugs { get; init; }
        public required BugStubAdo Ado { get; init; }
        private string DbPath { get; init; } = "";
        private string DataDir { get; init; } = "";

        public static Harness Create(StubLlmClient llm)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"qa_bug_{Guid.NewGuid():N}.db");
            var dataDir = Path.Combine(Path.GetTempPath(), $"qa_bug_data_{Guid.NewGuid():N}");

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance));
            services.AddScoped<DatabaseInitializer>();
            services.AddSingleton<ITextSearchProvider, SqliteFts5Search>();
            services.AddScoped<IKnowledgebaseService, KnowledgebaseService>();
            services.AddSingleton(Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }));
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var ado = new BugStubAdo();
            var bugs = new BugOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EventBus(), new PipelineConcurrency(), new CancellationRegistry(),
                ado, new StubLlmClientFactory(llm), new NullCodeContextService(),
                NullLogger<BugOrchestrator>.Instance);

            return new Harness { Provider = provider, Bugs = bugs, Ado = ado, DbPath = dbPath, DataDir = dataDir };
        }

        public async Task<BugSession> WaitForTerminalAsync(int sessionId, BugSessionStatus? expected = null)
        {
            var terminal = new[] { BugSessionStatus.Done, BugSessionStatus.Drafted, BugSessionStatus.AwaitingActual, BugSessionStatus.Failed, BugSessionStatus.Cancelled };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                var session = await db.BugSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
                if (session is not null && (expected is { } e ? session.Status == e : terminal.Contains(session.Status)))
                    return session;
                await Task.Delay(40);
            }
            throw new TimeoutException($"Bug session {sessionId} did not reach {(expected?.ToString() ?? "a terminal status")} in time.");
        }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { File.Delete(DbPath + s); } catch { /* best effort */ }
            try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class NullCodeContextService : ICodeContextService
    {
        public Task<(string DiffSummary, string Code)> DiscoverAsync(
            int workItemId, Core.Models.AdoWorkItem wi, Core.Models.AnalystOutput? analysis,
            IReadOnlyList<string> commitIds, IReadOnlyList<string> prIds,
            string? extraHintText, bool alwaysSearch,
            ILlmClient llm, string key, CancellationToken ct)
            => Task.FromResult(("", ""));
    }

    private sealed class StubLlmClientFactory(ILlmClient client) : ILlmClientFactory
    {
        public Task<ILlmClient> CreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    /// <summary>Repo-resolve throws (discovery no-ops); no linked test cases; records bug create + link.</summary>
    internal sealed class BugStubAdo : IAzureDevOpsClient
    {
        private int _nextBugId = 7000;
        public List<(string Title, string Severity)> CreatedBugs { get; } = [];
        public List<(int From, int To)> RelatedLinks { get; } = [];

        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(new AdoWorkItem { Id = workItemId, Title = $"WI-{workItemId}", WorkItemType = "User Story", AcceptanceCriteria = "AC text" });

        public Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default)
            => throw new AdoApiError("resolve_repository_id", 404, "no repo in tests");

        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);

        public Task<int> CreateBugAsync(string title, string severity, string preconditions, IReadOnlyList<string> reproSteps, string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "", string foundWith = "Manual Test", CancellationToken ct = default)
        {
            CreatedBugs.Add((title, severity));
            return Task.FromResult(_nextBugId++);
        }

        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default)
        {
            RelatedLinks.Add((fromId, toId));
            return Task.CompletedTask;
        }

        public Task LinkParentAsync(int childId, int parentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default) => Task.FromResult($"https://attachments/{fileName}");
        public Task AddWorkItemAttachmentLinkAsync(int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default) => Task.CompletedTask;

        // ── unused ────────────────────────────────────────────────────────────
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverWorkItemTypesAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, IReadOnlyList<string>? types = null, IReadOnlyList<string>? dateFields = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int testCaseId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default) => throw new NotImplementedException();
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
