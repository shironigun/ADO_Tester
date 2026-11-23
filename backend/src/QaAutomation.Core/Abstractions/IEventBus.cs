using System.Threading.Channels;
using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// In-memory pub/sub for live pipeline console events (SSE). Singleton. Per namespaced session key
/// (<c>session:{id}</c>, <c>report:{id}</c>) it holds subscriber channels (fan-out), a bounded ring
/// buffer for reconnect/late-subscriber replay, and a monotonic seq counter (spec B5 / D4).
/// </summary>
public interface IEventBus
{
    /// <summary>Finalize (assign seq/ts/sessionKey, append to ring) and fan out to subscribers. Thread-safe.</summary>
    void Publish(string key, ConsoleEvent ev);

    /// <summary>Subscribe to a key, pre-seeded with ring events newer than <paramref name="afterSeq"/>
    /// (SSE <c>Last-Event-ID</c> replay). Dispose to unsubscribe.</summary>
    IEventSubscription Subscribe(string key, long? afterSeq = null);

    /// <summary>The durable (narrative) events for <c>EventLogJson</c> persistence, most-recent <paramref name="cap"/>.</summary>
    IReadOnlyList<Dictionary<string, object?>> Narrative(string key, int cap = 300);
}

/// <summary>A live subscription. Read events from <see cref="Reader"/>; dispose to unsubscribe.</summary>
public interface IEventSubscription : IDisposable
{
    ChannelReader<ConsoleEvent> Reader { get; }
}
