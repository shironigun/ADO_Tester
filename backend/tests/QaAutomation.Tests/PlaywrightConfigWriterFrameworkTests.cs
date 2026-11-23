using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.QaRuns;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>INT-103 — framework cwd / overlay config helpers for Scripts Board runs under AUTOMATION_ROOT.</summary>
public sealed class PlaywrightConfigWriterFrameworkTests
{
    [Fact]
    public void AllUnderAutomationRoot_true_when_every_spec_is_inside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"auto_{Guid.NewGuid():N}", "automation");
        var a = Path.Combine(root, "modules", "deals", "api", "tests", "contracts", "a.spec.ts");
        var b = Path.Combine(root, "modules", "deals", "api", "tests", "endpoints", "b.spec.ts");
        Assert.True(PlaywrightConfigWriter.AllUnderAutomationRoot([a, b], root));
    }

    [Fact]
    public void AllUnderAutomationRoot_false_when_any_spec_is_outside()
    {
        var root = Path.Combine(Path.GetTempPath(), $"auto_{Guid.NewGuid():N}", "automation");
        var inside = Path.Combine(root, "modules", "deals", "a.spec.ts");
        var outside = Path.Combine(Path.GetTempPath(), "other", "x.spec.ts");
        Assert.False(PlaywrightConfigWriter.AllUnderAutomationRoot([inside, outside], root));
        Assert.False(PlaywrightConfigWriter.AllUnderAutomationRoot([inside], null));
        Assert.False(PlaywrightConfigWriter.AllUnderAutomationRoot([], root));
    }

    [Fact]
    public void WriteFrameworkOverlayConfig_points_reporters_at_run_dir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fwcfg_{Guid.NewGuid():N}");
        var auto = Path.Combine(tmp, "automation");
        var runDir = Path.Combine(tmp, "runs", "-1");
        Directory.CreateDirectory(runDir);
        var specPath = Path.Combine(auto, "modules", "deals", "api", "tests", "contracts", "x.spec.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
        File.WriteAllText(specPath, "import { test } from '@playwright/test';\n");

        var junit = Path.Combine(runDir, "junit.xml");
        var results = Path.Combine(runDir, "results.json");
        PlaywrightConfigWriter.WriteFrameworkOverlayConfig(
            runDir, auto,
            new PlaywrightRunSpec { RunId = -1, SpecFilePaths = [specPath] },
            junit, results);

        var cfg = File.ReadAllText(Path.Combine(runDir, "playwright.config.ts"));
        Assert.Contains("junit.xml", cfg.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("results.json", cfg.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x.spec.ts", cfg);
        try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
    }
}
