using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Tests;

/// <summary>Verifies the B5 per-session cancellation registry.</summary>
public sealed class CancellationRegistryTests
{
    [Fact]
    public void Register_returns_a_live_token()
    {
        var reg = new CancellationRegistry();
        var cts = reg.Register(1);

        Assert.False(cts.Token.IsCancellationRequested);
        Assert.True(reg.IsRunning(1));
    }

    [Fact]
    public void Cancel_signals_token_and_returns_true()
    {
        var reg = new CancellationRegistry();
        var cts = reg.Register(1);

        Assert.True(reg.Cancel(1));
        Assert.True(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_unknown_session_returns_false()
    {
        var reg = new CancellationRegistry();
        Assert.False(reg.Cancel(99));
    }

    [Fact]
    public void Register_replaces_previous_source()
    {
        var reg = new CancellationRegistry();
        var first = reg.Register(1);
        var priorCancelled = false;
        first.Token.Register(() => priorCancelled = true);

        var second = reg.Register(1);

        Assert.NotSame(first, second);
        Assert.True(priorCancelled); // L14: prior job cancelled before replace
        Assert.True(reg.Cancel(1));
        Assert.True(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void TryRegister_claims_a_free_session_and_refuses_a_busy_one_without_cancelling_it()
    {
        var reg = new CancellationRegistry();

        Assert.True(reg.TryRegister(1, out var first));
        Assert.False(reg.TryRegister(1, out var second));   // busy: refused…
        Assert.Null(second);
        Assert.False(first!.Token.IsCancellationRequested); // …and the run in flight is left alone (Register would cancel it)
        Assert.True(reg.TryRegister(2, out _));              // other sessions are independent
    }

    [Fact]
    public void TryRegister_succeeds_again_once_the_session_is_released()
    {
        var reg = new CancellationRegistry();
        Assert.True(reg.TryRegister(1, out _));

        reg.Remove(1);

        Assert.True(reg.TryRegister(1, out var again));
        Assert.False(again!.Token.IsCancellationRequested);
    }

    [Fact]
    public void Remove_clears_running_state()
    {
        var reg = new CancellationRegistry();
        reg.Register(1);

        reg.Remove(1);

        Assert.False(reg.IsRunning(1));
        Assert.False(reg.Cancel(1));
    }
}
