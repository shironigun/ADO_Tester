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
        Action<Exception> onLinkFailure,
        string? fixturesDir = null)
    {
        // When explicit SpecFilePaths are provided (API scripts under scripts/{module}/… OR UI
        // scripts under {PlaywrightRoot}/generated/ui/…), derive testDir from those files so
        // Playwright's testMatch (resolved relative to testDir) can actually find them.
        // Falling back to scripts/{module} alone left UI runs with "No tests found".
        string testDir;
        string testMatchLine;
        if (spec.SpecFilePaths.Count > 0)
        {
            var absPaths = spec.SpecFilePaths
                .Select(p => Path.GetFullPath(p))
                .ToList();
            var root = CommonDirectory(absPaths);
            testDir = EscPath(root);
            testMatchLine =
                $"\n  testMatch: [{string.Join(", ", absPaths.Select(p => $"'{EscPath(Path.GetRelativePath(root, p))}'"))}],";
        }
        else
        {
            testDir = spec.LevelFilter is { Length: > 0 } lf &&
                      Enum.TryParse<TestLevel>(lf, ignoreCase: true, out var level)
                ? Path.Combine(scriptsDir, spec.Module, level.ToDirName())
                : Path.Combine(scriptsDir, spec.Module);
            testDir = EscPath(testDir);
            testMatchLine = "";
        }

        var pkgJson = $$"""
            {
              "name": "qa-run-{{spec.RunId}}",
              "private": true,
              "devDependencies": {}
            }
            """;
        File.WriteAllText(Path.Combine(runDir, "package.json"), pkgJson);

        var htmlReporter = reportProvider is "playwright" or "both" ? ", ['html']" : "";
        var captureOpts = spec.EnableCapture ? ", screenshot: 'only-on-failure', trace: 'on-first-retry'" : "";
        var headedOpts = spec.Headed ? ", headless: false" : "";
        var workersExpr = spec.Workers is int w ? w.ToString() : "Number(process.env.AUTOMATION_WORKERS ?? '6')";
        var timeoutMs = spec.IsUiRun ? "90_000" : "30_000";

        string globalSetupLine = "";
        string storageStateOpt = "";
        if (spec.IsUiRun && !string.IsNullOrWhiteSpace(fixturesDir))
        {
            var setupPath = EscPath(Path.Combine(fixturesDir, "auth.setup.ts"));
            var statePath = EscPath(Path.Combine(fixturesDir, "storageState.json"));
            globalSetupLine = $"\n  globalSetup: '{setupPath}',";
            storageStateOpt = $", storageState: '{statePath}'";
        }

        var config = $$"""
            import { defineConfig } from '@playwright/test';
            import path from 'path';
            export default defineConfig({
              testDir: '{{testDir}}',{{testMatchLine}}{{globalSetupLine}}
              timeout: {{timeoutMs}},
              globalTimeout: Number(process.env.AUTOMATION_GLOBAL_TIMEOUT_MS ?? '900000'),
              expect: { timeout: 10_000 },
              fullyParallel: false,
              workers: {{workersExpr}},
              retries: 0,
              reporter: [['junit', { outputFile: 'junit.xml' }], ['json', { outputFile: 'results.json' }], ['allure-playwright', { resultsDir: 'allure-results' }]{{htmlReporter}}],
              use: { baseURL: process.env.BASE_URL ?? 'http://localhost'{{storageStateOpt}}{{captureOpts}}{{headedOpts}} },
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

    /// <summary>Longest common ancestor directory of the given absolute file paths.</summary>
    internal static string CommonDirectory(IReadOnlyList<string> absoluteFilePaths)
    {
        if (absoluteFilePaths.Count == 0)
            throw new ArgumentException("Need at least one path.", nameof(absoluteFilePaths));
        if (absoluteFilePaths.Count == 1)
            return Path.GetDirectoryName(absoluteFilePaths[0])!;

        var split = absoluteFilePaths
            .Select(p => Path.GetFullPath(p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();
        var first = split[0];
        var depth = first.Length - 1; // exclude filename
        for (var i = 1; i < split.Count; i++)
        {
            var other = split[i];
            var shared = 0;
            var max = Math.Min(depth, other.Length - 1);
            while (shared < max &&
                   string.Equals(first[shared], other[shared], StringComparison.OrdinalIgnoreCase))
                shared++;
            depth = shared;
        }
        return depth <= 0 ? Path.GetPathRoot(absoluteFilePaths[0])! : string.Join(Path.DirectorySeparatorChar, first.Take(depth));
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
