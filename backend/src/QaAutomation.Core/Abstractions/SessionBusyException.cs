namespace QaAutomation.Core.Abstractions;

/// <summary>
/// A session already has a pipeline run in flight (or is still stopping), so a second run was refused.
/// The API maps this to HTTP 409. Without the refusal, a double-click or retry cancelled the run in
/// progress and left the session stamped "Cancelled" while the replacement kept working.
/// </summary>
public sealed class SessionBusyException(int sessionId) : InvalidOperationException(
    $"Session {sessionId} already has a run in progress (or is still stopping). " +
    "Wait for it to finish, or cancel it first.")
{
    public int SessionId { get; } = sessionId;
}
