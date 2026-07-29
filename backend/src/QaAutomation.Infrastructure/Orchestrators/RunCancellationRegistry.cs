namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Cancellation tokens for in-flight <see cref="QaAutomation.Core.Models.TestRun"/> executions, keyed
/// by run id. A distinct singleton from the session-keyed <see cref="CancellationRegistry"/> so that a
/// run id and a session id that happen to share the same integer value can never collide on one shared
/// dictionary. The Playwright child process is killed via the token (see <c>PlaywrightRunner.SpawnAsync</c>),
/// and <c>RunExecutionService.ExecuteAsync</c> catches the resulting cancellation to mark the run Cancelled.
/// </summary>
public sealed class RunCancellationRegistry : CancellationRegistry;
