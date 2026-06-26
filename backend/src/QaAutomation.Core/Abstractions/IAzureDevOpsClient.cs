using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Azure DevOps REST client (PAT / Basic auth). Faithful port of the live Python <c>ado_client.py</c>
/// (spec §5.5). All methods take a <see cref="CancellationToken"/> so the cancellation registry can
/// abort in-flight calls cooperatively (spec §5.8).
/// </summary>
public interface IAzureDevOpsClient
{
    // ── Discovery (PAT → orgs / projects / area paths / branches) ───────────
    Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default);

    /// <summary>List branch (head) names for the given repository, or the configured ado.repository when null.</summary>
    Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default);

    // ── Reads ───────────────────────────────────────────────────────────────
    Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default);
    Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default);
    Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default);

    /// <summary>Children of an Epic/Feature. When recursive, BFS down the hierarchy descending through
    /// containers and collecting testable leaves. Returns the items plus a truncated flag.</summary>
    Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(
        int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default);

    /// <summary>List the Test Cases in a suite (optionally recursing into sub-suites), with parsed steps.</summary>
    Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default);

    // ── Reports query + enrichment ──────────────────────────────────────────
    Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
        IReadOnlyList<string>? types = null, CancellationToken ct = default);
    Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default);
    Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default);
    Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(
        IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default);

    // ── Writes (Test Case create + suite add + Tested-By; Bug create + Related link) ──
    Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default);
    Task AddTestCaseToSuiteAsync(int planId, int suiteId, int testCaseId, CancellationToken ct = default);
    Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default);
    Task<int> CreateBugAsync(
        string title, string severity, string preconditions, IReadOnlyList<string> reproSteps,
        string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "",
        string foundWith = "Manual Test", CancellationToken ct = default);
    Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default);

    /// <summary>Upload a binary as an ADO attachment; returns the attachment reference URL to link onto a WI.</summary>
    Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>Link an already-uploaded attachment onto a work item as an AttachedFile relation.</summary>
    Task AddWorkItemAttachmentLinkAsync(int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default);

    // ── Git repo reads + Code Search (code-analysis phase) ──────────────────
    Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default);
    Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default);
    Task<GitRefs> GetWorkItemGitRefsAsync(int workItemId, CancellationToken ct = default);
    Task<IReadOnlyList<LinkedChange>> GetLinkedChangesAsync(int workItemId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(string prId, string? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCommitChangedFilesAsync(string sha, string? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ChangedFile>> GetPullRequestDiffAsync(string prId, string? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ChangedFile>> GetCommitDiffAsync(string sha, string? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCommitsForPathAsync(string path, int top = 5, string? repoId = null, CancellationToken ct = default);
    Task<string> GetFileContentAsync(string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default);

    /// <summary>List the (non-folder) file paths under a repo scope path, recursively, at a branch.
    /// Repo-relative paths (no leading slash). Used to read a module's source straight from Azure Repos.</summary>
    Task<IReadOnlyList<string>> ListRepoFilePathsAsync(string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default);
}
