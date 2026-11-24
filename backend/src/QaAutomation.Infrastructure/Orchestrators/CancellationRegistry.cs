using System.Collections.Concurrent;

namespace QaAutomation.Infrastructure.Orchestrators;

/// <summary>
/// Per-session cancellation tokens for in-flight background pipelines (spec B5 — replaces the Python
/// <c>dict[int, asyncio.Task]</c> registry with <c>ConcurrentDictionary&lt;int, CancellationTokenSource&gt;</c>).
/// A token registered before the pipeline awaits the concurrency gate also cancels a still-queued child.
/// Singleton. Not sealed so a distinct keyspace (e.g. <see cref="RunCancellationRegistry"/>) can be a
/// separate singleton instance without sharing this one's session-keyed dictionary.
/// </summary>
public class CancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _registry = new();

    /// <summary>Register a fresh token source for a session, disposing any prior one. Returns the source
    /// whose <see cref="CancellationTokenSource.Token"/> the pipeline should observe.</summary>
    public CancellationTokenSource Register(int sessionId)
    {
        var cts = new CancellationTokenSource();
        if (_registry.TryRemove(sessionId, out var old))
        {
            // L14: starting a new job (e.g. refine) must cancel in-flight UI script generation.
            try { if (!old.IsCancellationRequested) old.Cancel(); } catch { /* ignore */ }
            old.Dispose();
        }
        _registry[sessionId] = cts;
        return cts;
    }

    /// <summary>Atomically claim a session for a new job. Unlike <see cref="Register"/> it never cancels
    /// or replaces a job already in flight: it returns false (and no source) when one is registered, so
    /// a duplicate request can be refused instead of killing the run it collided with.</summary>
    public bool TryRegister(int sessionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CancellationTokenSource? cts)
    {
        var fresh = new CancellationTokenSource();
        if (_registry.TryAdd(sessionId, fresh))
        {
            cts = fresh;
            return true;
        }
        fresh.Dispose();
        cts = null;
        return false;
    }

    /// <summary>Signal cancellation for a session. Returns true if a live (not-yet-cancelled) token existed.</summary>
    public bool Cancel(int sessionId)
    {
        if (_registry.TryGetValue(sessionId, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Remove and dispose a session's token source — call in the pipeline's <c>finally</c>.</summary>
    public void Remove(int sessionId)
    {
        if (_registry.TryRemove(sessionId, out var cts))
            cts.Dispose();
    }

    /// <summary>True while a session has a registered (in-flight) token source.</summary>
    public bool IsRunning(int sessionId) => _registry.ContainsKey(sessionId);
}
