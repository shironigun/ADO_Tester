namespace QaAutomation.Core.Models;

/// <summary>One ad-hoc Scripts Board execution (Slab 3, QLV2-302) — deliberately ADO-optional (no
/// AdoTestRunId/TriggerSource/etc., unlike <see cref="TestRun"/>). Table: ScriptBoardRuns. Reuses
/// <see cref="TestRunStatus"/> for status parity with QA Runs. Executed via <c>IPlaywrightRunner</c>
/// with <c>PlaywrightRunSpec.RunId = -Id</c> (negative namespace) so its on-disk artifacts under
/// <c>{runsDir}/{runId}</c> never collide with a positive <see cref="TestRun"/> id.</summary>
public sealed class ScriptBoardRun
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Human-readable description of what was selected, e.g. "module=leads, bucket=smoke".</summary>
    public string SelectionSummary { get; set; } = "";
    /// <summary>Resolved single module for Allure-history keying (mirrors <see cref="TestRun.Module"/>'s
    /// role in <c>PlaywrightRunner.HistoryFile</c>). Null when the selection spans multiple modules or
    /// none was resolvable — the run's history then lands under the shared "_all" bucket.</summary>
    public string? Module { get; set; }
    /// <summary>Resolved absolute spec file paths this run executed (JSON string[]), for re-run/audit.</summary>
    public string SpecFilePathsJson { get; set; } = "[]";
    public bool Headed { get; set; }
    public int? Workers { get; set; }
    public TestRunStatus Status { get; set; } = TestRunStatus.Queued;
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? JunitPath { get; set; }
    public string? ReportPath { get; set; }
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
