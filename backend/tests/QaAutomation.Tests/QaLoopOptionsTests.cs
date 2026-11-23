using QaAutomation.Core.Configuration;

namespace QaAutomation.Tests;

/// <summary>Locks the FrontendRoot-derived scanner paths (used by KB extraction, module analysis, and
/// generation) so the config refactor keeps producing the same repo-relative layout it hardcoded before.
/// INT-101: <see cref="QaLoopOptions.AutomationRoot"/> is a settable absolute path; empty falls back to
/// <see cref="QaLoopOptions.DerivedAutomationRoot"/> via <see cref="QaLoopOptions.EffectiveAutomationRoot"/>.</summary>
public sealed class QaLoopOptionsTests
{
    [Fact]
    public void Derived_paths_reproduce_the_configured_repo_layout()
    {
        var o = new QaLoopOptions { FrontendRoot = "web-app/src/app/src" };
        Assert.Equal("web-app/src/app/src/components/deals", o.FrontendComponentsDir("deals"));
        Assert.Equal("web-app/src/app/tests-e2e/deals", o.FrontendTestsDir("deals")); // sibling of the SPA src root
        Assert.Equal("web-app/src/app/automation", o.DerivedAutomationRoot);
        Assert.Equal("", o.AutomationRoot);
        Assert.Equal("web-app/src/app/automation", o.EffectiveAutomationRoot);
    }

    [Fact]
    public void Derived_paths_are_bare_relative_when_unconfigured()
    {
        var o = new QaLoopOptions(); // FrontendRoot = ""
        Assert.Equal("components/deals", o.FrontendComponentsDir("deals"));
        Assert.Equal("tests-e2e/deals", o.FrontendTestsDir("deals"));
        Assert.Equal("automation", o.DerivedAutomationRoot);
        Assert.Equal("automation", o.EffectiveAutomationRoot);
    }

    [Fact]
    public void Configured_AutomationRoot_wins_over_derived()
    {
        var o = new QaLoopOptions
        {
            FrontendRoot = "web-app/src/app/src",
            AutomationRoot = @"C:\repos\CRM Project\automation",
        };
        Assert.Equal("web-app/src/app/automation", o.DerivedAutomationRoot);
        Assert.Equal(@"C:\repos\CRM Project\automation", o.EffectiveAutomationRoot);
    }
}
