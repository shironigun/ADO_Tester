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
using QaAutomation.Infrastructure.TextSearch;

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
    public async Task Analyze_populates_WorkItemTitle_from_the_linked_work_item()
    {
        // QAL-070: mirrors AgentOrchestrator/BugOrchestrator, which fetch and store WorkItemTitle at
        // session creation — QaLoopOrchestrator's session is pre-seeded by the controller instead, so
        // the fetch has to happen in the background pipeline itself.
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));
        h.Ado.WorkItemToReturn = new AdoWorkItem { Id = 171740, Title = "Deals: add lead endpoint" };

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false, workItemId: 171740);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);
        Assert.Equal("Deals: add lead endpoint", session.WorkItemTitle);
    }

    [Fact]
    public async Task Analyze_leaves_WorkItemTitle_null_and_still_completes_when_the_ADO_fetch_throws()
    {
        // A failed work-item fetch must degrade gracefully: catch-log, leave WorkItemTitle null,
        // never fail the scan.
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));
        h.Ado.ThrowOnGetWorkItem = true;

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false, workItemId: 171740);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);   // scan still completed
        Assert.Null(session.WorkItemTitle);
        Assert.Null(session.ErrorMessage);   // the failure never bubbled up as a scan error
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
    public async Task GenerateDrafts_rewrites_Contract_schema_import_to_a_deterministic_filename()
    {
        // QAL-071b: the model's own relative import path is untrustworthy — it has been observed
        // using both './x' and '../schemas/x' conventions, sometimes with no matching "schema"
        // field at all. A single unresolvable import aborts Playwright's test COLLECTION for every
        // spec in the run (not just the broken one), so the writer no longer trusts the model's
        // path: it rewrites the spec's import to a filename it controls — the same slug as the
        // spec's own .spec.ts file — and writes the schema there.
        const string specWithSchemaImport = """
            import { test, expect } from '@playwright/test';
            import { DealDetailsDtoSchema } from './get-lead-deal-dealId.schema';

            test('[<tcId-pending>] Contract: GET /deals/{dealId} - response shape matches schema', async () => {
              expect(DealDetailsDtoSchema).toBeDefined();
            });
            """;
        var specJson = System.Text.Json.JsonSerializer.Serialize(
            new { spec = specWithSchemaImport, schema = "{\"type\":\"object\"}" });

        using var h = Harness.Create(new StubLlmClient(OneGapAnalyzerJson, specJson, ContractTcJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        await h.WaitForTerminalAsync(sid);    // AwaitingReview

        await h.Orchestrator.GenerateDraftsAsync(sid);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = db.ApiTestDrafts.Single(d => d.SessionId == sid);
        Assert.NotNull(draft.SpecFilePath);

        // Slug = spec filename minus ".spec.ts" — the same slug PersistDraft derives the schema
        // filename from, regardless of whatever import path the model originally wrote.
        var slug = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(draft.SpecFilePath!));
        var schemaPath = Path.Combine(Path.GetDirectoryName(draft.SpecFilePath)!, $"{slug}.schema.ts");
        Assert.True(File.Exists(schemaPath), $"expected schema file at {schemaPath}");
        Assert.Equal("{\"type\":\"object\"}", File.ReadAllText(schemaPath));

        // The on-disk spec's import must be rewritten to the deterministic path, not left as the
        // model's original './get-lead-deal-dealId.schema'.
        var writtenSpec = File.ReadAllText(draft.SpecFilePath!);
        Assert.Contains($"from './{slug}.schema'", writtenSpec);
        Assert.Equal(writtenSpec, draft.SpecCode);
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
    public async Task Push_of_shared_multicase_spec_in_scrambled_order_rewrites_each_cases_own_tag_only()
    {
        // QAL-067: a shared Endpoint spec file has 3 test( ) blocks, one per case, each carrying its
        // own INDEXED placeholder. Pushing case 1 before case 0 before case 2 must still land each
        // case's real ADO id on its own line — a global unscoped string replace would let whichever
        // case pushes FIRST stomp every other case's still-pending placeholder in the shared file.
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);

        var specPath = Path.Combine(Path.GetTempPath(), $"qal067_shared_{Guid.NewGuid():N}.spec.ts");
        const string sharedSpec = """
            import { test, expect } from '@playwright/test';

            test('[<tcId-pending:0>] Endpoint: GET /deals - 200 happy path', async () => {
              expect(200).toBe(200);
            });

            test('[<tcId-pending:1>] Endpoint: GET /deals - 401 without bearer', async () => {
              expect(401).toBe(401);
            });

            test('[<tcId-pending:2>] Endpoint: GET /deals - 400 on invalid input', async () => {
              expect(400).toBe(400);
            });
            """;
        File.WriteAllText(specPath, sharedSpec);
        try
        {
            var d0 = await h.SeedDraftAsync(sid, "[<tcId-pending:0>] Endpoint: GET /deals - 200 happy path",
                ApiDraftStatus.Approved, caseIndex: 0, specFilePath: specPath);
            var d1 = await h.SeedDraftAsync(sid, "[<tcId-pending:1>] Endpoint: GET /deals - 401 without bearer",
                ApiDraftStatus.Approved, caseIndex: 1, specFilePath: specPath);
            var d2 = await h.SeedDraftAsync(sid, "[<tcId-pending:2>] Endpoint: GET /deals - 400 on invalid input",
                ApiDraftStatus.Approved, caseIndex: 2, specFilePath: specPath);

            // Scrambled push order: 1, 0, 2.
            var pushed1 = await h.Orchestrator.PushDraftAsync(d1, testPlanId: 10, testSuiteId: 20);
            var pushed0 = await h.Orchestrator.PushDraftAsync(d0, testPlanId: 10, testSuiteId: 20);
            var pushed2 = await h.Orchestrator.PushDraftAsync(d2, testPlanId: 10, testSuiteId: 20);

            var ids = new[] { pushed0.AdoTestCaseId, pushed1.AdoTestCaseId, pushed2.AdoTestCaseId };
            Assert.All(ids, id => Assert.NotNull(id));
            Assert.Equal(3, ids.Distinct().Count());   // three distinct real ADO ids, one per case

            // Each draft's own title carries only ITS id — never another case's placeholder or id.
            Assert.Contains($"[{pushed0.AdoTestCaseId}]", pushed0.Title);
            Assert.Contains($"[{pushed1.AdoTestCaseId}]", pushed1.Title);
            Assert.Contains($"[{pushed2.AdoTestCaseId}]", pushed2.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed0.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed1.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed2.Title);

            // The physical file (read once, after all 3 pushes) must show each case's OWN id on its
            // OWN test( ) line, with no leftover placeholders and no cross-contamination.
            var finalText = File.ReadAllText(specPath);
            var lines = finalText.Split('\n');
            var line0 = Assert.Single(lines, l => l.Contains("200 happy path"));
            var line1 = Assert.Single(lines, l => l.Contains("401 without bearer"));
            var line2 = Assert.Single(lines, l => l.Contains("400 on invalid input"));
            Assert.Contains($"[{pushed0.AdoTestCaseId}]", line0);
            Assert.Contains($"[{pushed1.AdoTestCaseId}]", line1);
            Assert.Contains($"[{pushed2.AdoTestCaseId}]", line2);
            Assert.DoesNotContain("tcId-pending", finalText);   // no placeholder survives
        }
        finally
        {
            try { File.Delete(specPath); } catch { }
        }
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
            services.AddSingleton<ITextSearchProvider, SqliteFts5Search>();
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

        public async Task<int> SeedDraftAsync(
            int sessionId, string title, ApiDraftStatus status, int? adoId = null,
            int? caseIndex = null, string? specFilePath = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"do x\",\"expected\":\"see y\"}]",
                Status = status,
                AdoTestCaseId = adoId,
                Level = TestLevel.Contract,
                CaseIndex = caseIndex,
                SpecFilePath = specFilePath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
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

    /// <summary>Stub ADO client for QA Loop push tests: records write calls; reads throw unless
    /// configured (GetWorkItemAsync — QAL-070 — returns WorkItemToReturn, or throws if configured to).</summary>
    internal sealed class QaLoopAdoStub : IAzureDevOpsClient
    {
        private int _nextTcId = 7000;
        public List<string> CreatedTestCases { get; } = [];
        public List<(int PlanId, int SuiteId, int TcId)> SuiteAdds { get; } = [];
        public List<(int StoryId, int TcId)> TestedByLinks { get; } = [];
        public AdoWorkItem? WorkItemToReturn { get; set; }
        public bool ThrowOnGetWorkItem { get; set; }

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

        public int NextSuiteId = 900_000;
        public List<(int PlanId, int ParentId, string Name)> CreatedSuites { get; } = [];
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default)
        {
            CreatedSuites.Add((planId, parentSuiteId, name));
            return Task.FromResult(NextSuiteId++);
        }

        public Task LinkTestedByAsync(int storyId, int tcId, CancellationToken ct = default)
        {
            TestedByLinks.Add((storyId, tcId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);

        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default) =>
            ThrowOnGetWorkItem
                ? throw new InvalidOperationException("simulated ADO failure")
                : Task.FromResult(WorkItemToReturn ?? throw new NotImplementedException());

        // ── unused ──
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverWorkItemTypesAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, IReadOnlyList<string>? types = null, IReadOnlyList<string>? dateFields = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateBugAsync(string title, string severity, string preconditions, IReadOnlyList<string> reproSteps, string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "", string foundWith = "Manual Test", CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkParentAsync(int childId, int parentId, CancellationToken ct = default) => Task.CompletedTask;
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
