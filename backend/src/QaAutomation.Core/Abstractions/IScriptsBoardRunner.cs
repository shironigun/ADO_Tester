namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Runs a Scripts Board selection while preserving raw ANSI stdout/stderr chunks for the live terminal.
/// This is intentionally separate from <see cref="IPlaywrightRunner"/> so the established QA Runs
/// line-buffered process path remains unchanged.
/// </summary>
public interface IScriptsBoardRunner
{
    Task<PlaywrightRunResult> RunWithLiveTermAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default);
}
