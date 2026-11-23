using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Events;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>SB-304: terminal chunks must remain live-only so raw ANSI output never bloats run narratives.</summary>
public sealed class ScriptsBoardRunnerTests
{
    [Fact]
    public async Task Term_event_is_streamed_but_excluded_from_narrative()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe("scripts-board-run:1");

        bus.Publish("scripts-board-run:1", new ConsoleEvent
        {
            Kind = EventKinds.Term,
            Phase = "run",
            Text = "\u001b[32mgreen output\u001b[0m",
        });

        var streamed = await subscription.Reader.ReadAsync();
        Assert.Equal(EventKinds.Term, streamed.Kind);
        Assert.Equal("\u001b[32mgreen output\u001b[0m", streamed.Text);
        Assert.Empty(bus.Narrative("scripts-board-run:1"));
    }
}
