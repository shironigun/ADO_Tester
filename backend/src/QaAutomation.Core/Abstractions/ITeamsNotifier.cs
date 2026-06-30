using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>Posts Teams adaptive-card notifications for QA Loop run outcomes.</summary>
public interface ITeamsNotifier
{
    /// <summary>Posts a run-complete card after a Playwright run finishes (Pass/Fail/Error).</summary>
    Task NotifyRunCompleteAsync(TestRun run, CancellationToken ct = default);

    /// <summary>Posts a triage/file-bugs summary card (triaged count + ADO bug ids filed).</summary>
    Task NotifyTriageAsync(TestRun run, int triagedCount, IReadOnlyList<int> filedBugIds, CancellationToken ct = default);
}
