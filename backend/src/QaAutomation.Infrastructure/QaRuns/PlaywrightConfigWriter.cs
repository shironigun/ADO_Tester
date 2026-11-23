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

    /// <summary>INT-103 — overlay config for runs whose specs live under product <c>AUTOMATION_ROOT</c>.
    /// Process cwd is the framework root (so its <c>node_modules</c> + fixtures resolve); reporters write
    /// into the ADO_Tester run directory via absolute paths.</summary>
    internal static void WriteFrameworkOverlayConfig(
        string runDir,
        string automationRoot,
        PlaywrightRunSpec spec,
        string junitPath,
        string resultsPath)
    {
        var absPaths = spec.SpecFilePaths.Select(Path.GetFullPath).ToList();
        var root = CommonDirectory(absPaths);
        // Prefer staying under modules/ when possible so project-style discovery still works.
        var modulesRoot = Path.Combine(automationRoot, "modules");
        if (Directory.Exists(modulesRoot) &&
            root.StartsWith(Path.GetFullPath(modulesRoot), StringComparison.OrdinalIgnoreCase))
            root = Path.GetFullPath(modulesRoot);

        var testMatch = string.Join(", ", absPaths.Select(p =>
            $"'{EscPath(Path.GetRelativePath(root, p))}'"));
        var workersExpr = spec.Workers is int w ? w.ToString() : "Number(process.env.AUTOMATION_WORKERS ?? '6')";
        var timeoutMs = spec.IsUiRun ? "90_000" : "30_000";
        var headedOpts = spec.Headed ? ", headless: false" : "";

        var config = $$"""
            import { defineConfig } from '@playwright/test';
            export default defineConfig({
              testDir: '{{EscPath(root)}}',
              testMatch: [{{testMatch}}],
              timeout: {{timeoutMs}},
              globalTimeout: Number(process.env.AUTOMATION_GLOBAL_TIMEOUT_MS ?? '900000'),
              expect: { timeout: 10_000 },
              fullyParallel: false,
              workers: {{workersExpr}},
              retries: 0,
              reporter: [
                ['list'],
                ['junit', { outputFile: '{{EscPath(junitPath)}}' }],
                ['json', { outputFile: '{{EscPath(resultsPath)}}' }],
              ],
              use: { baseURL: process.env.BASE_URL ?? process.env.AUTOMATION_BASE_URL ?? 'http://localhost'{{headedOpts}} },
              projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
            });
            """;
        File.WriteAllText(Path.Combine(runDir, "playwright.config.ts"), config);
    }

    /// <summary>True when every selected spec path is under the configured product automation root.</summary>
    internal static bool AllUnderAutomationRoot(IReadOnlyList<string> specPaths, string? automationRoot)
    {
        if (string.IsNullOrWhiteSpace(automationRoot) || specPaths.Count == 0) return false;
        var root = Path.GetFullPath(automationRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return specPaths.All(p =>
        {
            var full = Path.GetFullPath(p);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        });
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
