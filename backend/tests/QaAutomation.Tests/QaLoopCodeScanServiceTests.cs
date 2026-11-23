using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>Unit tests for <see cref="QaLoopCodeScanService"/> — atlas-seeded repo fetch, discovery
/// fallback, file caps, and graceful handling of ADO failures / missing repo config.</summary>
public sealed class QaLoopCodeScanServiceTests
{
    private const string SessionKey = "session:scan-test";
    private const string Repository = "MyRepo";
    private const string Branch = "main";

    // ── Atlas-seeded path ─────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_uses_atlas_paths_first_when_frs_context_has_includes()
    {
        var ado = new CodeScanAdoStub();
        ado.RepoPaths =
        [
            "src/components/messaging/Inbox.tsx",
            "src/api/Messaging/MessagingController.cs",
            "src/unrelated/Other.ts",
        ];
        ado.FileContents["src/components/messaging/Inbox.tsx"] = "export const inbox = true;";
        ado.FileContents["src/api/Messaging/MessagingController.cs"] = "public class MessagingController {}";

        var discovery = new FakeDiscoveryService();
        var bus = new CapturingEventBus();
        var svc = CreateService(ado, discovery, bus);

        var frsContext = AtlasContext(
            feIncludes: ["src/components/messaging/**"],
            beIncludes: ["src/api/Messaging/**"]);

        var bundle = await svc.ScanAsync("messaging", Repository, Branch, frsContext, SessionKey, CancellationToken.None);

        Assert.Equal("atlas", bundle.DiscoverySource);
        Assert.Single(bundle.FrontendFiles);
        Assert.Single(bundle.BackendFiles);
        Assert.Equal("src/components/messaging/Inbox.tsx", bundle.FrontendFiles[0].Path);
        Assert.Equal("src/api/Messaging/MessagingController.cs", bundle.BackendFiles[0].Path);
        Assert.False(discovery.WasCalled);
    }

    // ── Discovery fallback ────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_falls_back_to_discovery_when_atlas_yields_zero_files()
    {
        var ado = new CodeScanAdoStub
        {
            RepoPaths = ["src/unrelated/Other.ts"],
        };
        var discovery = new FakeDiscoveryService
        {
            Result = new ModuleSourceDiscovery(
                RouteFile: null,
                RoutePath: null,
                FrontendFiles: ["src/components/messaging/Fallback.tsx"],
                ApiCalls: [new FrontendApiCall("GET", "/api/messages", "src/components/messaging/Fallback.tsx")],
                BackendFiles: ["src/api/Messaging/FallbackController.cs"],
                SourceDirectories: [],
                Confidence: "high",
                Explanation: "test",
                SourceBundle: "{}"),
        };
        ado.FileContents["src/components/messaging/Fallback.tsx"] = "export const fallback = true;";
        ado.FileContents["src/api/Messaging/FallbackController.cs"] = "public class FallbackController {}";

        var bus = new CapturingEventBus();
        var svc = CreateService(ado, discovery, bus);

        var frsContext = AtlasContext(
            feIncludes: ["src/components/messaging/**"],
            beIncludes: ["src/api/Messaging/**"]);

        var bundle = await svc.ScanAsync("messaging", Repository, Branch, frsContext, SessionKey, CancellationToken.None);

        Assert.Equal("discovery", bundle.DiscoverySource);
        Assert.True(discovery.WasCalled);
        Assert.Single(bundle.FrontendFiles);
        Assert.Single(bundle.BackendFiles);
        Assert.Contains("GET /api/messages", bundle.ApiCallCandidates);
        Assert.Contains(bus.Published, p =>
            p.Ev.Level == EventLevels.Warn
            && p.Ev.Text.Contains("Atlas paths produced no files", StringComparison.OrdinalIgnoreCase));
    }

    // ── Caps ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_caps_frontend_files_at_40()
    {
        var ado = new CodeScanAdoStub();
        for (var i = 0; i < 50; i++)
        {
            var path = $"src/components/messaging/File{i:D2}.tsx";
            ado.RepoPaths.Add(path);
            ado.FileContents[path] = $"export const file{i} = true;";
        }

        var svc = CreateService(ado, new FakeDiscoveryService(), new CapturingEventBus());
        var frsContext = AtlasContext(feIncludes: ["src/components/messaging/**"], beIncludes: []);

        var bundle = await svc.ScanAsync("messaging", Repository, Branch, frsContext, SessionKey, CancellationToken.None);

        Assert.Equal("atlas", bundle.DiscoverySource);
        Assert.Equal(40, bundle.FrontendFiles.Length);
    }

    // ── ADO failure / missing config ──────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_ado_list_failure_publishes_warn_and_returns_empty_none()
    {
        var ado = new CodeScanAdoStub { ThrowOnListRepo = true };
        var bus = new CapturingEventBus();
        var svc = CreateService(ado, new FakeDiscoveryService(), bus);

        var frsContext = AtlasContext(
            feIncludes: ["src/components/messaging/**"],
            beIncludes: []);

        var bundle = await svc.ScanAsync("messaging", Repository, Branch, frsContext, SessionKey, CancellationToken.None);

        Assert.Equal("none", bundle.DiscoverySource);
        Assert.Empty(bundle.FrontendFiles);
        Assert.Empty(bundle.BackendFiles);
        Assert.Contains(bus.Published, p =>
            p.Key == SessionKey
            && p.Ev.Level == EventLevels.Warn
            && p.Ev.Phase == EventPhases.Scan
            && p.Ev.Text.Contains("failed to list repo tree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_no_repo_returns_empty_none()
    {
        var bus = new CapturingEventBus();
        var svc = CreateService(new CodeScanAdoStub(), new FakeDiscoveryService(), bus);

        var bundle = await svc.ScanAsync(
            "messaging", repository: "", Branch, AtlasContext(["src/**"], []), SessionKey, CancellationToken.None);

        Assert.Equal("none", bundle.DiscoverySource);
        Assert.Empty(bundle.FrontendFiles);
        Assert.Contains(bus.Published, p => p.Ev.Text.Contains("no repository/branch configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_no_branch_returns_empty_none()
    {
        var bus = new CapturingEventBus();
        var svc = CreateService(new CodeScanAdoStub(), new FakeDiscoveryService(), bus);

        var bundle = await svc.ScanAsync(
            "messaging", Repository, branch: "", AtlasContext(["src/**"], []), SessionKey, CancellationToken.None);

        Assert.Equal("none", bundle.DiscoverySource);
        Assert.Empty(bundle.FrontendFiles);
        Assert.Contains(bus.Published, p => p.Ev.Text.Contains("no repository/branch configured", StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static QaLoopCodeScanService CreateService(
        CodeScanAdoStub ado, FakeDiscoveryService discovery, CapturingEventBus bus) =>
        new(ado, discovery, bus, NullLogger<QaLoopCodeScanService>.Instance);

    private static FrsModuleContext AtlasContext(string[] feIncludes, string[] beIncludes) =>
        new(
            Slug: "messaging",
            ModuleMarkdown: "# Messaging",
            AtlasMarkdown: "",
            FrontendPathIncludes: feIncludes,
            BackendPathIncludes: beIncludes,
            KeywordsExclusive: [],
            RulesMarkdown: "",
            FeBackLinksMarkdown: "",
            FeaturesJson: "[]");

    private sealed class CodeScanAdoStub : IAzureDevOpsClient
    {
        public List<string> RepoPaths { get; set; } = [];
        public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ThrowOnListRepo { get; set; }

        public Task<IReadOnlyList<string>> ListRepoFilePathsAsync(
            string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default)
        {
            if (ThrowOnListRepo)
                throw new InvalidOperationException("ADO list failed");
            return Task.FromResult<IReadOnlyList<string>>(RepoPaths);
        }

        public Task<string> GetFileContentAsync(
            string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default)
        {
            if (FileContents.TryGetValue(path, out var content))
                return Task.FromResult(content);
            return Task.FromResult(string.Empty);
        }

        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverWorkItemTypesAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(
            int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(
            DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
            IReadOnlyList<string>? types = null, IReadOnlyList<string>? dateFields = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(
            IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateTestCaseAsync(int testCaseId, string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int testCaseId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateBugAsync(
            string title, string severity, string preconditions, IReadOnlyList<string> reproSteps,
            string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "",
            string foundWith = "Manual Test", CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkParentAsync(int childId, int parentId, CancellationToken ct = default) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAdoTestRunAsync(int planId, int suiteId, string name, IReadOnlyList<int> pointIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetAdoTestResultIdsAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAdoTestResultsAsync(int adoTestRunId, IReadOnlyList<AdoTestResultUpdate> updates, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(int pipelineId, int? minBuildId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(int buildId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeDiscoveryService : IModuleSourceDiscoveryService
    {
        public ModuleSourceDiscovery? Result { get; set; }
        public bool WasCalled { get; private set; }

        public Task<ModuleSourceDiscovery?> DiscoverAsync(
            string repository, string branch, string module,
            string? frontendPath, string? frontendRoute, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(Result);
        }
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<(string Key, ConsoleEvent Ev)> Published { get; } = [];

        public void Publish(string key, ConsoleEvent ev) => Published.Add((key, ev));

        public IEventSubscription Subscribe(string key, long? afterSeq = null) =>
            throw new NotImplementedException();

        public IReadOnlyList<Dictionary<string, object?>> Narrative(string key, int cap = 300) =>
            throw new NotImplementedException();
    }
}
