using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Events;

namespace QaAutomation.Tests;

/// <summary>Verifies the B5 EventBus: seq/ts assignment, fan-out, ring-buffer replay, trimming,
/// terminal flagging, and the narrative snapshot.</summary>
public sealed class EventBusTests
{
    private static ConsoleEvent Phase(string text) =>
        new() { Kind = EventKinds.Phase, Phase = EventPhases.Analyst, Text = text };

    [Fact]
    public void Publish_assigns_seq_ts_sessionKey_and_fans_out()
    {
        var bus = new EventBus();
        using var sub = bus.Subscribe("session:1");
        var ev = Phase("hello");

        bus.Publish("session:1", ev);

        Assert.Equal(1, ev.Seq);
        Assert.True(ev.Ts > 0);
        Assert.Equal("session:1", ev.SessionKey);
        Assert.True(sub.Reader.TryRead(out var got));
        Assert.Same(ev, got);
    }

    [Fact]
    public void Seq_is_monotonic_per_key()
    {
        var bus = new EventBus();
        var a = Phase("a");
        var b = Phase("b");

        bus.Publish("session:1", a);
        bus.Publish("session:1", b);

        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
    }

    [Fact]
    public void Keys_do_not_alias()
    {
        var bus = new EventBus();
        var a = Phase("a");
        var b = Phase("b");

        bus.Publish("session:1", a);
        bus.Publish("report:1", b);

        Assert.Equal(1, a.Seq);
        Assert.Equal(1, b.Seq);   // independent seq per key
    }

    [Fact]
    public void Subscribe_replays_ring_after_seq()
    {
        var bus = new EventBus();
        for (var i = 0; i < 5; i++)
            bus.Publish("session:1", Phase($"e{i}"));   // seq 1..5

        using var sub = bus.Subscribe("session:1", afterSeq: 3);

        Assert.Equal([4L, 5L], Drain(sub).Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Ring_trims_to_max_400()
    {
        var bus = new EventBus();
        for (var i = 0; i < 450; i++)
            bus.Publish("session:1", Phase($"e{i}"));

        using var sub = bus.Subscribe("session:1");   // replays the whole ring
        var seqs = Drain(sub).Select(e => e.Seq).ToArray();

        Assert.Equal(400, seqs.Length);
        Assert.Equal(51, seqs[0]);     // oldest retained
        Assert.Equal(450, seqs[^1]);
    }

    [Fact]
    public void Fans_out_to_multiple_subscribers()
    {
        var bus = new EventBus();
        using var s1 = bus.Subscribe("session:1");
        using var s2 = bus.Subscribe("session:1");

        bus.Publish("session:1", Phase("x"));

        Assert.True(s1.Reader.TryRead(out _));
        Assert.True(s2.Reader.TryRead(out _));
    }

    [Fact]
    public void Unsubscribe_stops_delivery()
    {
        var bus = new EventBus();
        var sub = bus.Subscribe("session:1");
        sub.Dispose();

        bus.Publish("session:1", Phase("x"));

        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public void Narrative_filters_to_narrative_kinds_and_maps_camel()
    {
        var bus = new EventBus();
        bus.Publish("session:1", new ConsoleEvent { Kind = EventKinds.Phase, Text = "p" });
        bus.Publish("session:1", new ConsoleEvent { Kind = EventKinds.Token, Text = "tok" }); // ephemeral
        bus.Publish("session:1", new ConsoleEvent { Kind = EventKinds.Result, Text = "r" });

        var narrative = bus.Narrative("session:1");

        Assert.Equal(2, narrative.Count);
        Assert.Equal("p", narrative[0]["text"]);
        Assert.Equal("r", narrative[1]["text"]);
        Assert.All(narrative, d => Assert.Contains("sessionKey", d.Keys));
    }

    [Fact]
    public void Terminal_event_is_flagged()
    {
        Assert.True(new ConsoleEvent { Kind = EventKinds.Result, Data = new() { ["terminal"] = true } }.IsTerminal);
        Assert.False(new ConsoleEvent { Kind = EventKinds.Phase }.IsTerminal);
    }

    private static List<ConsoleEvent> Drain(IEventSubscription sub)
    {
        var list = new List<ConsoleEvent>();
        while (sub.Reader.TryRead(out var e))
            list.Add(e);
        return list;
    }
}
