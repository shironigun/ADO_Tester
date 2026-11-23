using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>Exit-code + junit-count → durable TestRunStatus. Guards the regression where a
/// Playwright setup crash (ExitCode≠0, Failed=0) was wrongly marked Passed.</summary>
public sealed class PlaywrightRunResultStatusTests
{
    [Fact]
    public void ResolveStatus_failures_win_over_exit_code()
    {
        var r = new PlaywrightRunResult { ExitCode = 1, Passed = 1, Failed = 2, Skipped = 0 };
        Assert.Equal(TestRunStatus.Failed, r.ResolveStatus());
        Assert.Null(r.ResolveErrorMessage());
    }

    [Fact]
    public void ResolveStatus_nonzero_exit_zero_failures_is_Error()
    {
        var r = new PlaywrightRunResult { ExitCode = 1, Passed = 0, Failed = 0, Skipped = 0 };
        Assert.Equal(TestRunStatus.Error, r.ResolveStatus());
        Assert.Contains("exited with code 1", r.ResolveErrorMessage());
    }

    [Fact]
    public void ResolveStatus_all_passed_is_Passed()
    {
        var r = new PlaywrightRunResult { ExitCode = 0, Passed = 3, Failed = 0, Skipped = 1 };
        Assert.Equal(TestRunStatus.Passed, r.ResolveStatus());
        Assert.Null(r.ResolveErrorMessage());
    }

    [Fact]
    public void ResolveStatus_exit_zero_but_nothing_ran_is_Error()
    {
        var r = new PlaywrightRunResult { ExitCode = 0, Passed = 0, Failed = 0, Skipped = 0 };
        Assert.Equal(TestRunStatus.Error, r.ResolveStatus());
        Assert.Contains("no tests executed", r.ResolveErrorMessage());
    }

    [Fact]
    public void ResolveStatus_all_skipped_is_Passed()
    {
        // Skipped-only is a successful Playwright run (exit 0) — not an infra Error.
        var r = new PlaywrightRunResult { ExitCode = 0, Passed = 0, Failed = 0, Skipped = 2 };
        Assert.Equal(TestRunStatus.Passed, r.ResolveStatus());
        Assert.Null(r.ResolveErrorMessage());
    }
}
