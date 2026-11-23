namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Product-repo git status under <c>automation/</c>, sync-from-base, and PR draft reminder
/// (never opens a PR — operator creates it).
/// </summary>
public interface IAutomationGitStatusService
{
    AutomationGitStatusSnapshot GetStatus();

    /// <summary>
    /// Fetch + merge <c>origin/{baseBranch}</c> into the current branch (no push, no PR).
    /// Keeps the working automation branch up to date with QA before a run.
    /// </summary>
    AutomationGitSyncResult SyncFromBase();
}

public sealed record AutomationGitStatusSnapshot(
    bool ProductMode,
    string? AutomationRoot,
    string? RepoRoot,
    IReadOnlyList<string> ChangedUnderAutomation,
    string Hint,
    string? AutomationPipelineUrl = null,
    string? CurrentBranch = null,
    string? BaseBranch = null,
    IReadOnlyList<string>? CommitsAheadOfBase = null,
    string? PrDraftReminder = null);

public sealed record AutomationGitSyncResult(
    bool Ok,
    string Message,
    AutomationGitStatusSnapshot Status);
