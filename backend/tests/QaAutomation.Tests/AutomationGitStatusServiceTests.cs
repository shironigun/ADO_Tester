using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

public sealed class AutomationGitStatusServiceTests
{
    [Fact]
    public void ParsePorcelain_handles_modified_untracked_and_rename()
    {
        const string raw = """
            M  automation/modules/deals/api/tests/contracts/a.spec.ts
             M automation/resources/impact-map.json
            ?? automation/pipelines/README.md
            R  automation/old.ts -> automation/modules/deals/ui/tests/smoke/b.spec.ts
            """;

        var paths = AutomationGitStatusService.ParsePorcelain(raw);
        Assert.Contains("automation/modules/deals/api/tests/contracts/a.spec.ts", paths);
        Assert.Contains("automation/resources/impact-map.json", paths);
        Assert.Contains("automation/pipelines/README.md", paths);
        Assert.Contains("automation/modules/deals/ui/tests/smoke/b.spec.ts", paths);
        Assert.DoesNotContain(paths, p => p.Contains(" -> "));
    }
}
