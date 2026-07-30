using System.Diagnostics;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>Shared run-directory configuration writer for QA Runs and Scripts Board executions.</summary>
internal static class PlaywrightConfigWriter
{
    internal static void WriteRunConfig(
        string runDir,
        PlaywrightRunSpec spec,
        string scriptsDir,
        string runtimeDir,
        string reportProvider,
        Action<Exception> onLinkFailure)
    {
        var testDir = spec.LevelFilter is { Length: > 0 } lf &&
                      Enum.TryParse<TestLevel>(lf, ignoreCase: true, out var level)
            ? Path.Combine(scriptsDir, spec.Module, level.ToDirName())
            : Path.Combine(scriptsDir, spec.Module);
        testDir = testDir.Replace('\\', '/');

        var pkgJson = $$"""
            {
              "name": "qa-run-{{spec.RunId}}",
              "private": true,
              "devDependencies": {}
            }
            """;
        File.WriteAllText(Path.Combine(runDir, "package.json"), pkgJson);

        var htmlReporter = reportProvider is "playwright" or "both" ? ", ['html']" : "";
        var testMatchLine = spec.SpecFilePaths.Count > 0
            ? $"\n  testMatch: [{string.Join(", ", spec.SpecFilePaths.Select(p => $"'{EscPath(p)}'"))}],"
            : "";
        var captureOpts = spec.EnableCapture ? ", screenshot: 'only-on-failure', trace: 'on-first-retry'" : "";
        var headedOpts = spec.Headed ? ", headless: false" : "";
        var workersExpr = spec.Workers is int w ? w.ToString() : "Number(process.env.AUTOMATION_WORKERS ?? '6')";

        var config = $$"""
            import { defineConfig } from '@playwright/test';
            import path from 'path';
            export default defineConfig({
              testDir: '{{testDir}}',{{testMatchLine}}
              timeout: 30_000,
              globalTimeout: Number(process.env.AUTOMATION_GLOBAL_TIMEOUT_MS ?? '900000'),
              expect: { timeout: 10_000 },
              fullyParallel: false,
              workers: {{workersExpr}},
              retries: 0,
              reporter: [['junit', { outputFile: 'junit.xml' }], ['json', { outputFile: 'results.json' }], ['allure-playwright', { resultsDir: 'allure-results' }]{{htmlReporter}}],
              use: { baseURL: process.env.BASE_URL ?? 'http://localhost'{{captureOpts}}{{headedOpts}} },
              projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
            });
            """;
        File.WriteAllText(Path.Combine(runDir, "playwright.config.ts"), config);

        var nmLink = Path.Combine(runDir, "node_modules");
        var nmTarget = Path.Combine(runtimeDir, "node_modules");
        if (!Directory.Exists(nmLink) && Directory.Exists(nmTarget))
        {
            try { LinkDirectory(nmLink, nmTarget); }
            catch (Exception ex) { onLinkFailure(ex); }
        }
    }

    internal static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J exited {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
    }

    internal static string EscPath(string path) => path.Replace('\\', '/');
}
