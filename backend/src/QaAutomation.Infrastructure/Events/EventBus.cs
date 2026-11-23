using System.Threading.Channels;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Events;

/// <summary>
/// In-memory pub/sub for live pipeline console events (spec B5 / D4). Singleton. Ports the Python
/// <c>EventBus</c>: per session key it keeps a set of subscriber channels (fan-out), a bounded ring
/// buffer for replay, and a monotonic seq. .NET has no event-loop affinity, so a single thread-safe
/// <see cref="Publish"/> replaces the Python loop-side/thread-side split.
/// </summary>
public sealed class EventBus : IEventBus
{
    private const int RingMax = 400;            // recent events kept for live SSE replay
    private const int StructuredMax = 800;      // structured progress events kept for durable narrative
    private const int QueueMax = 1000;          // per-subscriber bound; oldest dropped on overflow
    private static readonly TimeSpan GraceTtl = TimeSpan.FromSeconds(600);  // keep ring after terminal+last unsub

    private readonly object _lock = new();
    private readonly Dictionary<string, SessionState> _sessions = new();

    private sealed class SessionState
    {
        public readonly HashSet<Channel<ConsoleEvent>> Subscribers = new();
        public readonly Queue<ConsoleEvent> Ring = new();
        // Structured narrative events (phase/system/result/error) kept separately so a flood of raw
        // "stat" stdout lines from a long Playwright run can't evict the meaningful progress events.
        public readonly Queue<ConsoleEvent> StructuredRing = new();
        public long Seq;
        public double? TerminalAt;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void Publish(string key, ConsoleEvent ev)
    {
        Channel<ConsoleEvent>[] subscribers;
        lock (_lock)
        {
            var st = State(key);
            st.Seq++;
            ev.Seq = st.Seq;
            ev.Ts = Now();
            ev.SessionKey = key;
            st.Ring.Enqueue(ev);
            while (st.Ring.Count > RingMax)
                st.Ring.Dequeue();
            // Retain structured progress events in their own bounded buffer (kind != stat) so they
            // survive the raw-stdout flood and can be replayed after the run finishes.
            if (ev.IsNarrative && ev.Kind != EventKinds.Stat)
            {
                st.StructuredRing.Enqueue(ev);
                while (st.StructuredRing.Count > StructuredMax)
                    st.StructuredRing.Dequeue();
            }
            if (ev.IsTerminal)
                st.TerminalAt = Now();
            subscribers = st.Subscribers.ToArray();
        }
        // Fan out outside the lock. DropOldest means TryWrite never fails: a wedged client loses the
        // oldest queued event and recovers the gap from the ring on reconnect (seq gap).
        foreach (var ch in subscribers)
            ch.Writer.TryWrite(ev);
    }

    public IEventSubscription Subscribe(string key, long? afterSeq = null)
    {
        var channel = Channel.CreateBounded<ConsoleEvent>(new BoundedChannelOptions(QueueMax)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        ConsoleEvent[] replay;
        lock (_lock)
        {
            var st = State(key);
            st.Subscribers.Add(channel);
            replay = st.Ring.Where(e => afterSeq is null || e.Seq > afterSeq).ToArray();
        }
        foreach (var e in replay)
            channel.Writer.TryWrite(e);

        return new Subscription(this, key, channel);
    }

    public IReadOnlyList<Dictionary<string, object?>> Narrative(string key, int cap = 300)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var st))
                return [];
            // Always retain the structured progress events (phase/system/result/error) — they carry
            // the meaningful narrative (phase transitions, pass/fail summary). Raw Playwright stdout
            // ("stat") can number in the thousands, so it's kept only as a bounded recent tail.
            var structured = st.StructuredRing.AsEnumerable();
            var statTail = st.Ring.Where(e => e.Kind == EventKinds.Stat).TakeLast(cap);
            return structured.Concat(statTail)
                .OrderBy(e => e.Seq)
                .Select(e => e.ToCamel())
                .ToList();
        }
    }

    private SessionState State(string key)
    {
        if (!_sessions.TryGetValue(key, out var st))
            _sessions[key] = st = new SessionState();
        return st;
    }

    private void Unsubscribe(string key, Channel<ConsoleEvent> channel)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var st))
                return;
            st.Subscribers.Remove(channel);
            channel.Writer.TryComplete();
            GcLocked();
        }
    }

    private void GcLocked()
    {
        var now = Now();
        var dead = _sessions
            .Where(kv => kv.Value.Subscribers.Count == 0 && kv.Value.TerminalAt is { } t && now - t > GraceTtl.TotalSeconds)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in dead)
            _sessions.Remove(k);
    }

    private sealed class Subscription : IEventSubscription
    {
        private readonly EventBus _bus;
        private readonly string _key;
        private readonly Channel<ConsoleEvent> _channel;
        private bool _disposed;

        public Subscription(EventBus bus, string key, Channel<ConsoleEvent> channel)
        {
            _bus = bus;
            _key = key;
            _channel = channel;
        }

        public ChannelReader<ConsoleEvent> Reader => _channel.Reader;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _bus.Unsubscribe(_key, _channel);
        }
    }
}
