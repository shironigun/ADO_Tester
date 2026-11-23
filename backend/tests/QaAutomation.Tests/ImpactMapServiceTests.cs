using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>INT-401 — seed impact-map from build-qa.yml; path sim selects Deals (+ deps).</summary>
public sealed class ImpactMapServiceTests
{
    [Fact]
    public void ParseIncludes_reads_Lead_style_paths()
    {
        const string yaml = """
            trigger:
              branches:
                include:
                - qa
              paths:
                include:
                - Lead/src/*
                - shared/src/*
                - customer/src/*
                - dealer/src/*

            pool:
              vmImage: 'windows-latest'
            """;

        var includes = BuildQaPathIncludeParser.ParseIncludes(yaml);
        Assert.Equal(4, includes.Count);
        Assert.Contains("Lead/src/*", includes);
        Assert.Contains("dealer/src/*", includes);
        Assert.DoesNotContain("qa", includes);
    }

    [Fact]
    public void Seed_and_simulate_selects_deals_for_Lead_and_deps()
    {
        var repo = Path.Combine(Path.GetTempPath(), "impact_repo_" + Guid.NewGuid().ToString("N"));
        var auto = Path.Combine(repo, "automation");
        var leadYmlDir = Path.Combine(repo, "Lead", "build");
        Directory.CreateDirectory(auto);
        Directory.CreateDirectory(leadYmlDir);
        Directory.CreateDirectory(Path.Combine(auto, "resources"));

        File.WriteAllText(Path.Combine(leadYmlDir, "build-qa.yml"), """
            trigger:
              paths:
                include:
                - Lead/src/*
                - shared/src/*
                - customer/src/*
                - dealer/src/*
            pool:
              vmImage: ubuntu-latest
            """);

        try
        {
            var adapter = new ScriptPathAdapter(
                new StaticOptionsMonitor<QaLoopOptions>(new QaLoopOptions { AutomationRoot = auto }));
            var svc = new ImpactMapService(adapter, NullLogger<ImpactMapService>.Instance);

            var seed = svc.SeedFromBuildQa();
            Assert.Contains("deals", seed.UnitsSeeded);
            Assert.True(seed.PathPrefixCount >= 4);
            Assert.True(File.Exists(Path.Combine(auto, "resources", "impact-map.json")));

            var leadHit = svc.Simulate(["Lead/src/Target.Crm.Lead/Deal/DealService.cs"]);
            Assert.False(leadHit.UsedSmokeFallback);
            Assert.Contains("@deals", leadHit.SelectedTags);
            Assert.Equal("@deals", leadHit.Grep);

            var sharedHit = svc.Simulate(["shared/src/Target.Crm.Shared/Foo.cs"]);
            Assert.Contains("@deals", sharedHit.SelectedTags);

            var feHit = svc.Simulate(["web-app/src/target-crm/src/components/deals/DealList.tsx"]);
            Assert.Contains("@deals", feHit.SelectedTags);

            var unrelated = svc.Simulate(["docs/readme.md"]);
            Assert.True(unrelated.UsedSmokeFallback);
            Assert.Equal("@smoke", unrelated.Grep);
        }
        finally
        {
            try { Directory.Delete(repo, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Seed_messaging_unit_without_empty_module_scaffold()
    {
        var repo = Path.Combine(Path.GetTempPath(), "impact_msg_" + Guid.NewGuid().ToString("N"));
        var auto = Path.Combine(repo, "automation");
        Directory.CreateDirectory(Path.Combine(repo, "Lead", "build"));
        Directory.CreateDirectory(Path.Combine(repo, "messaging", "build"));
        Directory.CreateDirectory(Path.Combine(auto, "resources"));

        File.WriteAllText(Path.Combine(repo, "Lead", "build", "build-qa.yml"), """
            trigger:
              paths:
                include:
                - Lead/src/*
                - shared/src/*
            pool:
              vmImage: ubuntu-latest
            """);
        File.WriteAllText(Path.Combine(repo, "messaging", "build", "build-qa.yml"), """
            trigger:
              paths:
                include:
                - messaging/src/*
                - shared/src/*
                - customer/src/*
                - dealer/src/*
            pool:
              vmImage: ubuntu-latest
            """);

        try
        {
            var adapter = new ScriptPathAdapter(
                new StaticOptionsMonitor<QaLoopOptions>(new QaLoopOptions { AutomationRoot = auto }));
            var svc = new ImpactMapService(adapter, NullLogger<ImpactMapService>.Instance);

            var seed = svc.SeedFromBuildQa();
            Assert.Contains("deals", seed.UnitsSeeded);
            Assert.Contains("messaging", seed.UnitsSeeded);
            // No empty modules/messaging scaffold — only resources/impact-map.json
            Assert.False(Directory.Exists(Path.Combine(auto, "modules", "messaging")));

            var msgHit = svc.Simulate(["messaging/src/Target.Crm.Messaging/Api/Foo.cs"]);
            Assert.Contains("@messaging", msgHit.SelectedTags);
            Assert.DoesNotContain("@deals", msgHit.SelectedTags);

            var feHit = svc.Simulate(["web-app/src/target-crm/src/components/messaging/components/messenger.tsx"]);
            Assert.Contains("@messaging", feHit.SelectedTags);

            // Shared deps can hit both units once both are seeded
            var shared = svc.Simulate(["shared/src/X.cs"]);
            Assert.Contains("@deals", shared.SelectedTags);
            Assert.Contains("@messaging", shared.SelectedTags);
            Assert.Contains("|", shared.Grep);

            // Adapter can target messaging paths when a session later generates (still no pre-create)
            var spec = adapter.ApiSpecPath("messaging", Core.Models.TestLevel.Endpoint, "send-message");
            Assert.Contains(Path.Combine("modules", "messaging"), spec);
        }
        finally
        {
            try { Directory.Delete(repo, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Live_CRM_seed_selects_deals_when_env_set()
    {
        var auto = Environment.GetEnvironmentVariable("ADO_LIVE_AUTOMATION_ROOT");
        if (string.IsNullOrWhiteSpace(auto) || !Directory.Exists(auto))
            return; // optional live check — skip silently when unset

        var adapter = new ScriptPathAdapter(
            new StaticOptionsMonitor<QaLoopOptions>(new QaLoopOptions { AutomationRoot = auto }));
        var svc = new ImpactMapService(adapter, NullLogger<ImpactMapService>.Instance);
        var seed = svc.SeedFromBuildQa();
        Assert.Contains("deals", seed.UnitsSeeded);
        Assert.Contains("messaging", seed.UnitsSeeded);
        Assert.Contains("@deals", svc.Simulate(["Lead/src/a.cs"]).SelectedTags);
        Assert.Contains("@messaging", svc.Simulate(["messaging/src/b.cs"]).SelectedTags);
        Assert.Contains("@deals", svc.Simulate(["customer/src/b.cs"]).SelectedTags);
        Assert.Equal("@smoke", svc.Simulate(["unrelated/readme.md"]).Grep);
        Assert.False(Directory.Exists(Path.Combine(auto, "modules", "messaging")),
            "INT-501 must not empty-scaffold modules/messaging");
    }
}
