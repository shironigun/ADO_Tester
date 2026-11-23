using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Product-repo git status / sync-from-base under <c>automation/</c>.
/// Never pushes and never opens a PR — only reminds the operator to create one.
/// </summary>
public sealed class AutomationGitStatusService(
    IScriptPathAdapter paths,
    IOptionsMonitor<QaLoopOptions> opts,
    ILogger<AutomationGitStatusService> log) : IAutomationGitStatusService
{
    private string? PipelineUrl
    {
        get
        {
            var u = opts.CurrentValue.AutomationPipelineUrl?.Trim();
            return string.IsNullOrWhiteSpace(u) ? null : u;
        }
    }

    private string BaseBranch
    {
        get
        {
            var b = opts.CurrentValue.AutomationBaseBranch?.Trim();
            return string.IsNullOrWhiteSpace(b) ? "qa" : b;
        }
    }

    public AutomationGitStatusSnapshot GetStatus()
    {
        if (!paths.IsProductMode || paths.AutomationRoot is null)
        {
            return new AutomationGitStatusSnapshot(
                ProductMode: false,
                AutomationRoot: null,
                RepoRoot: null,
                ChangedUnderAutomation: [],
                Hint: "AUTOMATION_ROOT is not set; legacy private scripts have no product git lane.",
                AutomationPipelineUrl: PipelineUrl,
                BaseBranch: BaseBranch);
        }

        var autoRoot = paths.AutomationRoot;
        var repoRoot = FindGitRoot(autoRoot);
        if (repoRoot is null)
        {
            return new AutomationGitStatusSnapshot(
                true, autoRoot, null, [],
                "Could not locate a .git root above AUTOMATION_ROOT.",
                PipelineUrl, BaseBranch: BaseBranch);
        }

        var relAuto = RelAutomation(repoRoot, autoRoot);
        var porcelain = RunGit(repoRoot, $"status --porcelain -- \"{relAuto}\"", out _);
        var changed = ParsePorcelain(porcelain)
            .Select(p => p.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var branch = RunGit(repoRoot, "branch --show-current", out _).Trim();
        if (string.IsNullOrWhiteSpace(branch)) branch = null;

        var baseBr = BaseBranch;
        var aheadRaw = RunGit(repoRoot,
            $"log --oneline origin/{baseBr}..HEAD -- \"{relAuto}\"", out _);
        var ahead = aheadRaw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(15)
            .ToList();

        var hint = BuildHint(changed.Count, ahead.Count, branch, baseBr);
        var reminder = BuildPrReminder(changed, ahead, branch, baseBr);

        return new AutomationGitStatusSnapshot(
            ProductMode: true,
            AutomationRoot: autoRoot,
            RepoRoot: repoRoot,
            ChangedUnderAutomation: changed,
            Hint: hint,
            AutomationPipelineUrl: PipelineUrl,
            CurrentBranch: branch,
            BaseBranch: baseBr,
            CommitsAheadOfBase: ahead,
            PrDraftReminder: reminder);
    }

    public AutomationGitSyncResult SyncFromBase()
    {
        var before = GetStatus();
        if (!before.ProductMode || before.RepoRoot is null)
        {
            return new AutomationGitSyncResult(false,
                "AUTOMATION_ROOT / git repo not available.", before);
        }

        var repo = before.RepoRoot;
        var baseBr = before.BaseBranch ?? BaseBranch;

        // Refuse if uncommitted work under automation/ — merge would be risky.
        if (before.ChangedUnderAutomation.Count > 0)
        {
            return new AutomationGitSyncResult(false,
                "Uncommitted changes under automation/. Commit or stash them before syncing from "
                + baseBr + ".", before);
        }

        var fetch = RunGit(repo, "fetch origin", out var fetchCode, timeoutMs: 120_000);
        if (fetchCode != 0)
        {
            return new AutomationGitSyncResult(false,
                "git fetch origin failed: " + Truncate(fetch), GetStatus());
        }

        var merge = RunGit(repo, $"merge --no-edit origin/{baseBr}", out var mergeCode, timeoutMs: 120_000);
        if (mergeCode != 0)
        {
            // Best-effort abort so the operator isn't left mid-merge.
            RunGit(repo, "merge --abort", out _, timeoutMs: 30_000);
            return new AutomationGitSyncResult(false,
                $"Merge origin/{baseBr} failed (aborted). Resolve manually if needed: "
                + Truncate(merge), GetStatus());
        }

        var after = GetStatus();
        var msg = string.IsNullOrWhiteSpace(merge)
            ? $"Synced current branch with origin/{baseBr} (no push, no PR)."
            : $"Synced with origin/{baseBr}: {Truncate(merge)}";
        return new AutomationGitSyncResult(true, msg, after);
    }

    private static string BuildHint(int dirty, int ahead, string? branch, string baseBr)
    {
        var sb = new StringBuilder();
        sb.Append("Push updates ADO and local files under automation/ — it does not run CI or open a PR. ");
        if (!string.IsNullOrEmpty(branch))
            sb.Append($"Current branch: {branch}. ");
        sb.Append($"Sync from {baseBr} before a run when this branch may be behind. ");
        if (dirty > 0)
            sb.Append($"{dirty} uncommitted file(s) under automation/. ");
        if (ahead > 0)
            sb.Append($"{ahead} commit(s) ahead of origin/{baseBr} touching automation/. ");
        return sb.ToString().Trim();
    }

    private static string? BuildPrReminder(
        IReadOnlyList<string> changed, IReadOnlyList<string> ahead, string? branch, string baseBr)
    {
        if (changed.Count == 0 && ahead.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("Draft reminder — create a PR yourself (ADO_Tester will not open one). ");
        if (!string.IsNullOrEmpty(branch))
            sb.Append($"Branch `{branch}` → `{baseBr}`. ");
        else
            sb.Append($"Target base branch: `{baseBr}`. ");

        if (changed.Count > 0)
        {
            sb.Append($"Uncommitted under automation/ ({changed.Count}): ");
            sb.Append(string.Join(", ", changed.Take(8)));
            if (changed.Count > 8) sb.Append($" …+{changed.Count - 8} more");
            sb.Append(". Commit & push this branch, then open the PR. ");
        }

        if (ahead.Count > 0)
        {
            sb.Append($"Already committed ahead of origin/{baseBr} ({ahead.Count}): ");
            sb.Append(string.Join("; ", ahead.Take(5)));
            if (ahead.Count > 5) sb.Append($" …+{ahead.Count - 5} more");
            sb.Append(". Push the branch if needed, then create the PR into ").Append(baseBr).Append('.');
        }

        return sb.ToString();
    }

    private static string RelAutomation(string repoRoot, string autoRoot)
    {
        var rel = Path.GetRelativePath(repoRoot, autoRoot).Replace('\\', '/');
        return rel.StartsWith("..", StringComparison.Ordinal) ? "automation" : rel;
    }

    private static string? FindGitRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private string RunGit(string cwd, string args, out int exitCode, int timeoutMs = 15_000)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                log.LogWarning("git {Args} timed out under {Cwd}", args, cwd);
                return stdout + stderr;
            }
            exitCode = p.ExitCode;
            if (exitCode != 0)
                log.LogDebug("git {Args} exit {Code}: {Err}", args, exitCode, stderr);
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "git failed under {Cwd}: {Args}", cwd, args);
            return ex.Message;
        }
    }

    private static string Truncate(string s, int max = 400)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s.Trim() : s.Trim()[..max] + "…");

    /// <summary>Parse <c>git status --porcelain</c> paths (supports rename <c>R  a -> b</c>).</summary>
    internal static IReadOnlyList<string> ParsePorcelain(string output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return list;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length < 4) continue;
            var pathPart = raw[3..].Trim();
            if (pathPart.Contains(" -> ", StringComparison.Ordinal))
                pathPart = pathPart.Split(" -> ", 2, StringSplitOptions.None)[^1].Trim();
            if (pathPart.StartsWith('"') && pathPart.EndsWith('"') && pathPart.Length >= 2)
                pathPart = pathPart[1..^1];
            if (pathPart.Length > 0) list.Add(pathPart);
        }
        return list;
    }
}
