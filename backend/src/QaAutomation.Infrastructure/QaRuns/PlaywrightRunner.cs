using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>
/// Runs Playwright specs in a child process. Provisions the shared runtime once on first call
/// (npm ci + npx playwright install); all subsequent runs are read-only against that runtime.
/// AUTOMATION_PROFILE is set on the child process environment, not a CLI flag.
/// </summary>
public sealed class PlaywrightRunner : IPlaywrightRunner
{
    private readonly IEventBus _bus;
    private readonly PipelineConcurrency _concurrency;
    private readonly string _scriptsDir;
    private readonly string _runsDir;
    private readonly string _runtimeDir;
    private readonly ILogger<PlaywrightRunner> _log;
    private readonly string _reportProvider;
    private readonly string _adoBaseUrl;

    // Provisioned exactly once; guarded by _runtimeLock.
    private volatile bool _runtimeReady;
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);

    public PlaywrightRunner(
        IEventBus bus, PipelineConcurrency concurrency,
        IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop,
        IOptions<AzureDevOpsOptions> ado,
        ILogger<PlaywrightRunner> log)
    {
        _bus = bus;
        _concurrency = concurrency;
        var dataDir = storage.Value.DataDirectory;
        _scriptsDir      = Path.GetFullPath(Path.Combine(dataDir, qaLoop.Value.ScriptsFolderName));
        _runsDir         = Path.GetFullPath(Path.Combine(dataDir, qaLoop.Value.RunsFolderName));
        _runtimeDir      = Path.GetFullPath(Path.Combine(dataDir, "playwright-runtime"));
        _reportProvider  = qaLoop.Value.ReportProvider;
        _adoBaseUrl      = ado.Value.BaseUrl;
        _log = log;
    }

    public async Task<PlaywrightRunResult> RunAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
    {
        await EnsureRuntimeAsync(eventKey, ct);

        // Only Pushed drafts with numeric ids are eligible
        if (spec.GrepTags.Count == 0)
        {
            _log.LogWarning("Run {RunId}: no eligible (Pushed) grep tags — aborting", spec.RunId);
            return new PlaywrightRunResult { ExitCode = 1, Failed = 0 };
        }

        var runDir = Path.Combine(_runsDir, spec.RunId.ToString());
        Directory.CreateDirectory(runDir);

        var junitPath   = Path.Combine(runDir, "junit.xml");
        var resultsPath = Path.Combine(runDir, "results.json");

        WriteRunConfig(runDir, spec);

        var grep = BuildGrep(spec.GrepTags);
        // Reporters are declared in playwright.config.ts (allure-playwright + conditional html);
        // --output sets the artifacts folder; junit/json paths are relative in the config.
        var args = $"test --grep \"{grep}\" --output={EscPath(runDir)}";

        Pub(eventKey, EventKinds.Phase, "run", $"Playwright run {spec.RunId} starting · grep={grep}");

        var sw = Stopwatch.StartNew();
        int exitCode;

        await _concurrency.Gate.WaitAsync(ct);
        try
        {
            exitCode = await SpawnAsync("npx", $"playwright {args}", runDir, spec.Profile, eventKey, ct);
        }
        finally
        {
            _concurrency.Gate.Release();
        }

        sw.Stop();
        WriteAllureContext(runDir, spec);
        var junit = ParseJUnit(junitPath);

        Pub(eventKey, EventKinds.Result, "run",
            $"Run {spec.RunId} done — exit {exitCode} · {junit.Passed} passed · {junit.Failed} failed · {junit.Skipped} skipped",
            exitCode == 0 ? EventLevels.Success : EventLevels.Warn,
            new() { ["exitCode"] = exitCode, ["passed"] = junit.Passed, ["failed"] = junit.Failed });

        return new PlaywrightRunResult
        {
            ExitCode       = exitCode,
            Passed         = junit.Passed,
            Failed         = junit.Failed,
            Skipped        = junit.Skipped,
            DurationMs     = sw.ElapsedMilliseconds,
            JunitPath      = File.Exists(junitPath) ? junitPath : null,
            ResultsJsonPath = File.Exists(resultsPath) ? resultsPath : null,
            FailedTests    = junit.FailedTests,
        };
    }

    // ── Runtime provisioning (once) ──────────────────────────────────────────

    private async Task EnsureRuntimeAsync(string eventKey, CancellationToken ct)
    {
        if (_runtimeReady) return;
        await _runtimeLock.WaitAsync(ct);
        try
        {
            if (_runtimeReady) return;
            Pub(eventKey, EventKinds.System, text: "Provisioning shared Playwright runtime (first run)…");
            Directory.CreateDirectory(_runtimeDir);
            WriteRuntimePackageJson(_runtimeDir);
            await SpawnAsync("npm", "ci", _runtimeDir, profile: null, eventKey, ct);
            await SpawnAsync("npx", "playwright install --with-deps chromium", _runtimeDir, profile: null, eventKey, ct);
            // Allure capability check — log version; a non-zero exit here only warns (generate handled later)
            // ponytail: fallback if @allurereport/cli lacks --single-file: ship allure-commandline + bundled JRE
            var allureExit = await SpawnAsync("npx", "allure --version", _runtimeDir, profile: null, eventKey, ct);
            if (allureExit != 0)
                Pub(eventKey, EventKinds.System, text: "WARNING: allure --version check failed; report generation may be unavailable.", level: EventLevels.Warn);
            _runtimeReady = true;
            Pub(eventKey, EventKinds.System, text: "Playwright runtime ready.", level: EventLevels.Success);
        }
        finally
        {
            _runtimeLock.Release();
        }
    }

    // ── Run directory config files ────────────────────────────────────────────

    private void WriteRunConfig(string runDir, PlaywrightRunSpec spec)
    {
        var levelSubdir = spec.LevelFilter is { Length: > 0 }
            ? spec.LevelFilter
            : "*"; // all levels

        var testDir = Path.Combine(_scriptsDir, spec.Module, levelSubdir)
            .Replace('\\', '/');

        // package.json — resolves node_modules at the shared read-only runtime
        var pkgJson = $$"""
            {
              "name": "qa-run-{{spec.RunId}}",
              "private": true,
              "devDependencies": {}
            }
            """;
        File.WriteAllText(Path.Combine(runDir, "package.json"), pkgJson);

        // playwright.config.ts — reporters: junit+json always-on, allure-playwright always-on,
        // html gated on report_provider ∈ {playwright, both}
        var htmlReporter = (_reportProvider == "playwright" || _reportProvider == "both")
            ? ", ['html']" : "";
        var config = $$"""
            import { defineConfig } from '@playwright/test';
            import path from 'path';
            export default defineConfig({
              testDir: '{{testDir}}',
              reporter: [['junit', { outputFile: 'junit.xml' }], ['json', { outputFile: 'results.json' }], ['allure-playwright', { resultsDir: 'allure-results' }]{{htmlReporter}}],
              use: { baseURL: process.env.BASE_URL ?? 'http://localhost' },
              projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
            });
            """;
        File.WriteAllText(Path.Combine(runDir, "playwright.config.ts"), config);

        // node_modules symlink → shared runtime (read-only)
        var nmLink = Path.Combine(runDir, "node_modules");
        var nmTarget = Path.Combine(_runtimeDir, "node_modules");
        if (!Directory.Exists(nmLink) && Directory.Exists(nmTarget))
        {
            try { Directory.CreateSymbolicLink(nmLink, nmTarget); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not symlink node_modules; Playwright may re-install"); }
        }
    }

    // ── Allure context files (QAL-056) ───────────────────────────────────────

    private void WriteAllureContext(string runDir, PlaywrightRunSpec spec)
    {
        try
        {
            var resultsDir = Path.Combine(runDir, "allure-results");
            Directory.CreateDirectory(resultsDir);

            // environment.properties
            var env = new StringBuilder();
            env.AppendLine($"profile={spec.Profile}");
            env.AppendLine($"module={spec.Module}");
            env.AppendLine($"trigger.source={spec.TriggerSourceWire}");
            if (spec.TriggerBuildId.HasValue)  env.AppendLine($"build.id={spec.TriggerBuildId}");
            if (!string.IsNullOrEmpty(spec.TriggerCommitSha)) env.AppendLine($"commit={spec.TriggerCommitSha}");
            if (spec.AdoTestRunId.HasValue)    env.AppendLine($"ado.test.run.id={spec.AdoTestRunId}");
            File.WriteAllText(Path.Combine(resultsDir, "environment.properties"), env.ToString());

            // categories.json — maps Allure failure statuses to our triage taxonomy
            var categories = """
                [
                  { "name": "Real Bug",    "matchedStatuses": ["failed"] },
                  { "name": "Flaky",       "matchedStatuses": ["failed"] },
                  { "name": "Test Issue",  "matchedStatuses": ["broken"] },
                  { "name": "Stale Test",  "matchedStatuses": ["skipped"] }
                ]
                """;
            File.WriteAllText(Path.Combine(resultsDir, "categories.json"), categories);

            // executor.json — deep links to ADO build + ADO Test Run
            var buildUrl = spec.TriggerBuildId.HasValue
                ? $"{_adoBaseUrl}/_build/results?buildId={spec.TriggerBuildId}"
                : null;
            var testRunUrl = spec.AdoTestRunId.HasValue
                ? $"{_adoBaseUrl}/_testManagement/runs?runId={spec.AdoTestRunId}"
                : null;

            var executor = new Dictionary<string, object?>(6)
            {
                ["name"] = "ADO QA Pipeline",
            };
            if (spec.TriggerBuildId.HasValue) executor["buildName"] = $"Build #{spec.TriggerBuildId}";
            if (buildUrl is not null)         executor["buildUrl"]  = buildUrl;
            if (spec.AdoTestRunId.HasValue)   executor["reportName"] = $"ADO Test Run #{spec.AdoTestRunId}";
            if (testRunUrl is not null)        executor["reportUrl"] = testRunUrl;

            File.WriteAllText(Path.Combine(resultsDir, "executor.json"),
                JsonSerializer.Serialize(executor, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write Allure context files for run {RunId} — generate may be incomplete", spec.RunId);
        }
    }

    private static void WriteRuntimePackageJson(string dir)
    {
        // @allurereport/cli: Node-native Allure 3 CLI (no JRE). allure-playwright: Playwright reporter.
        // ponytail: if @allurereport/cli --single-file is unsupported, fallback = allure-commandline + bundled JRE
        const string json = """
            {
              "name": "qa-playwright-runtime",
              "private": true,
              "devDependencies": {
                "@playwright/test": "latest",
                "allure-playwright": "latest",
                "@allurereport/cli": "latest"
              }
            }
            """;
        File.WriteAllText(Path.Combine(dir, "package.json"), json);
    }

    // ── Process spawn ────────────────────────────────────────────────────────

    private async Task<int> SpawnAsync(
        string exe, string args, string workDir, string? profile, string eventKey, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(profile))
            psi.Environment["AUTOMATION_PROFILE"] = profile;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void RelayLine(string? line, bool isErr)
        {
            if (string.IsNullOrEmpty(line)) return;
            Pub(eventKey, EventKinds.Stat, "run", line, isErr ? EventLevels.Warn : EventLevels.Info);
        }

        proc.OutputDataReceived += (_, e) => RelayLine(e.Data, false);
        proc.ErrorDataReceived  += (_, e) => RelayLine(e.Data, true);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        await using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        return await tcs.Task;
    }

    // ── Grep builder ────────────────────────────────────────────────────────

    private static string BuildGrep(IReadOnlyList<int> ids) =>
        string.Join("|", ids.Select(id => $@"\[{id}\]"));

    // ── JUnit parser ─────────────────────────────────────────────────────────

    private (int Passed, int Failed, int Skipped, IReadOnlyList<FailedTestResult> FailedTests)
        ParseJUnit(string junitPath)
    {
        if (!File.Exists(junitPath))
            return (0, 0, 0, []);

        try
        {
            var doc = XDocument.Load(junitPath);
            var suites = doc.Descendants("testsuite").ToList();

            var passed  = suites.Sum(s => int.TryParse(s.Attribute("tests")?.Value, out var t) ? t : 0)
                        - suites.Sum(s => int.TryParse(s.Attribute("failures")?.Value, out var f) ? f : 0)
                        - suites.Sum(s => int.TryParse(s.Attribute("skipped")?.Value, out var sk) ? sk : 0)
                        - suites.Sum(s => int.TryParse(s.Attribute("errors")?.Value, out var e) ? e : 0);
            var failed  = suites.Sum(s => int.TryParse(s.Attribute("failures")?.Value, out var f) ? f : 0)
                        + suites.Sum(s => int.TryParse(s.Attribute("errors")?.Value, out var e) ? e : 0);
            var skipped = suites.Sum(s => int.TryParse(s.Attribute("skipped")?.Value, out var sk) ? sk : 0);

            var failures = doc.Descendants("testcase")
                .Where(tc => tc.Element("failure") is not null || tc.Element("error") is not null)
                .Select(tc => new FailedTestResult
                {
                    TestTitle    = tc.Attribute("name")?.Value ?? "",
                    ErrorMessage = (tc.Element("failure") ?? tc.Element("error"))?.Value
                        ?.Split('\n').FirstOrDefault()?.Trim(),
                })
                .ToList();

            return (Math.Max(passed, 0), failed, skipped, failures);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JUnit parse failed for {Path}", junitPath);
            return (0, 0, 0, []);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private static string EscPath(string path) => path.Replace('\\', '/');
}
