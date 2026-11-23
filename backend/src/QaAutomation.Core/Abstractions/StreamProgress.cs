using System.Diagnostics;

namespace QaAutomation.Core.Abstractions;

/// <summary>A live token/thinking delta emitted by a streaming provider (ClaudeCli). Non-streaming
/// providers never emit. <see cref="Kind"/> is "token" or "thinking" (spec §5.6).</summary>
public sealed record LlmEmit(string Kind, string Text);

/// <summary>Immutable read of a <see cref="StreamProgress"/>.</summary>
public readonly record struct StreamSnapshot(long Chars, long Ticks, double ElapsedSeconds);

/// <summary>
/// Thread-safe progress holder shared between a streaming LLM call (which mutates it as tokens arrive)
/// and the orchestrator's polling loop (which reads <see cref="Snapshot"/> ~every 1.5s). Plain object +
/// lock, no DB / event-loop coupling. Mirrors the Python <c>StreamProgress</c>.
/// </summary>
public sealed class StreamProgress
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _chars;
    private long _ticks;

    /// <summary>Record the running character count (also counts as a tick).</summary>
    public void Update(long chars)
    {
        lock (_lock)
        {
            _chars = chars;
            _ticks++;
        }
    }

    /// <summary>Record activity without a new character count.</summary>
    public void Tick()
    {
        lock (_lock) { _ticks++; }
    }

    public StreamSnapshot Snapshot()
    {
        lock (_lock) { return new StreamSnapshot(_chars, _ticks, _clock.Elapsed.TotalSeconds); }
    }
}
