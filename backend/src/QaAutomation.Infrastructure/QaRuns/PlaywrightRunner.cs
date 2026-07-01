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
using QaAutomation.Infrastructure.Llm;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>
/// Runs Playwright specs in a child process. Provisions the shared runtime once on first call
/// (npm ci + npx playwright install); all subsequent runs are read-only against that runtime.
/// AUTOMATION_PROFILE (profile key), AUTOMATION_BASE_URL (target host), and AUTOMATION_BEARER_TOKEN
/// (credentialed profiles only) are set on the child process environment, not CLI flags (QAL-068).
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
    private readonly string _historyDir;

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
        _historyDir      = Path.GetFullPath(Path.Combine(dataDir, "allure-history"));
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
        // --output must NOT be runDir itself — Playwright wipes its entire outputDir at the start of
        // every run, which was deleting playwright.config.ts/package.json/the node_modules link before
        // it could even load them (config load then silently fails → 0 tests found, no error surfaced).
        var testOutputDir = Path.Combine(runDir, "test-output");
        var args = $"test --grep \"{grep}\" --output={EscPath(testOutputDir)}";

        Pub(eventKey, EventKinds.Phase, "run", $"Playwright run {spec.RunId} starting · grep={grep}");

        var sw = Stopwatch.StartNew();
        int exitCode;

        // Resolve the profile key (e.g. "infinity") to its registry entry so the child process gets
        // a real target URL/token, not just the key string (QAL-068 — AUTOMATION_PROFILE alone is
        // not a URL; every generated spec was silently falling back to a hardcoded/localhost guess).
        var profileEntry = DmsProfileOptions.All.FirstOrDefault(p => p.Key == spec.Profile);

        await _concurrency.Gate.WaitAsync(ct);
        try
        {
            exitCode = await SpawnAsync("npx", $"playwright {args}", runDir, spec.Profile, eventKey, ct,
                suppressOutput: !spec.ActivityEnabled,
                baseUrl: profileEntry?.BaseUrl, bearerToken: profileEntry?.BearerToken);
        }
        finally
        {
            _concurrency.Gate.Release();
        }

        sw.Stop();
        WriteAllureContext(runDir, spec);
        var reportPath = await GenerateAllureReportAsync(runDir, spec, eventKey, ct);
        var junit = ParseJUnit(junitPath);

        Pub(eventKey, EventKinds.Result, "run",
            $"Run {spec.RunId} done — exit {exitCode} · {junit.Passed} passed · {junit.Failed} failed · {junit.Skipped} skipped",
            exitCode == 0 ? EventLevels.Success : EventLevels.Warn,
            new() { ["exitCode"] = exitCode, ["passed"] = junit.Passed, ["failed"] = junit.Failed });

        return new PlaywrightRunResult
        {
            ExitCode        = exitCode,
            Passed          = junit.Passed,
            Failed          = junit.Failed,
            Skipped         = junit.Skipped,
            DurationMs      = sw.ElapsedMilliseconds,
            JunitPath       = File.Exists(junitPath) ? junitPath : null,
            ResultsJsonPath = File.Exists(resultsPath) ? resultsPath : null,
            ReportPath      = reportPath,
            FailedTests     = junit.FailedTests,
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
            // `npm ci` requires a pre-existing package-lock.json (we only ever write a bare
            // package.json with "latest" ranges) and fails immediately without one; `npm install`
            // is the correct choice here since there's no lockfile to install exactly from.
            var installExit = await SpawnAsync("npm", "install", _runtimeDir, profile: null, eventKey, ct);
            if (installExit != 0)
                throw new InvalidOperationException($"npm install exited {installExit} — Playwright runtime not provisioned.");

            var pwExit = await SpawnAsync("npx", "playwright install --with-deps chromium", _runtimeDir, profile: null, eventKey, ct);
            if (pwExit != 0)
                throw new InvalidOperationException($"playwright install exited {pwExit} — browser binaries not provisioned.");

            // Allure capability check — log version; a non-zero exit here only warns (generate handled later)
            // ponytail: fallback if @allurereport/cli lacks --single-file: ship allure-commandline + bundled JRE
            var allureExit = await SpawnAsync("npx", "allure --version", _runtimeDir, profile: null, eventKey, ct);
            if (allureExit != 0)
                Pub(eventKey, EventKinds.System, text: "WARNING: allure --version check failed; report generation may be unavailable.", level: EventLevels.Warn);

            // Generated specs live under _scriptsDir (module/level subfolders), NOT under the run
            // directory — Node resolves each spec's "@playwright/test" import by walking UP from the
            // spec file itself, so the run directory's own node_modules link is unreachable from there.
            // One link at the scripts root satisfies every module/level file underneath it.
            Directory.CreateDirectory(_scriptsDir);
            var scriptsNm = Path.Combine(_scriptsDir, "node_modules");
            if (!Directory.Exists(scriptsNm))
            {
                try { LinkDirectory(scriptsNm, Path.Combine(_runtimeDir, "node_modules")); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not link node_modules under scripts dir; specs may fail to resolve @playwright/test"); }
            }

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
        // Match the exact folder PersistDraft wrote to (level.ToDirName(), e.g. "contracts") — a bare
        // level name/casing mismatch here silently matches zero test files (Playwright's testDir is a
        // literal path, not a glob, so "*" for "all levels" never resolves either). No filter → point
        // at the module root; Playwright recursively scans all level subfolders under it by default.
        var testDir = spec.LevelFilter is { Length: > 0 } lf && Enum.TryParse<TestLevel>(lf, ignoreCase: true, out var level)
            ? Path.Combine(_scriptsDir, spec.Module, level.ToDirName())
            : Path.Combine(_scriptsDir, spec.Module);
        testDir = testDir.Replace('\\', '/');

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

        // node_modules link → shared runtime (read-only)
        var nmLink = Path.Combine(runDir, "node_modules");
        var nmTarget = Path.Combine(_runtimeDir, "node_modules");
        if (!Directory.Exists(nmLink) && Directory.Exists(nmTarget))
        {
            try { LinkDirectory(nmLink, nmTarget); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not link node_modules; Playwright may re-install"); }
        }
    }

    /// <summary><c>Directory.CreateSymbolicLink</c> requires elevation or Developer Mode on Windows and
    /// fails silently under a normal user account — confirmed live: every run's own node_modules link
    /// never got created, so `npx playwright` fell back to an unrelated global npx cache and couldn't
    /// find `@playwright/test`. An NTFS junction (`mklink /J`) gives the same directory-link behavior
    /// with no special privileges required.</summary>
    private static void LinkDirectory(string link, string target)
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
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start mklink.");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
    }

    // ── Allure generate + history (QAL-057) ─────────────────────────────────

    private async Task<string?> GenerateAllureReportAsync(
        string runDir, PlaywrightRunSpec spec, string eventKey, CancellationToken ct)
    {
        var resultsDir = Path.Combine(runDir, "allure-results");
        var reportDir  = Path.Combine(runDir, "allure-report");

        // provider=playwright → no Allure generate; return Playwright html if present
        if (_reportProvider == "playwright")
        {
            var pwHtml = Path.Combine(runDir, "html", "index.html");
            return File.Exists(pwHtml) ? pwHtml : null;
        }

        if (!Directory.Exists(resultsDir))
        {
            _log.LogWarning("Run {RunId}: allure-results missing — skipping generate", spec.RunId);
            return FallbackReportPath(runDir);
        }

        // Seed history store into allure-results/history/ for trend continuity
        SeedHistory(resultsDir, spec);

        try
        {
            Pub(eventKey, EventKinds.System, "run", "Generating Allure report…");
            var genExit = await SpawnAsync(
                "npx", $"allure generate {EscPath(resultsDir)} -o {EscPath(reportDir)} --single-file --clean",
                runDir, profile: null, eventKey, ct);

            if (genExit != 0)
            {
                _log.LogWarning("Run {RunId}: allure generate exited {Code}", spec.RunId, genExit);
                return FallbackReportPath(runDir);
            }

            // Copy fresh history back to server-side store
            CopyHistoryBack(resultsDir, spec);

            // Single-file is typically index.html; fall back to any .html in the report dir
            var singleFile = Path.Combine(reportDir, "index.html");
            if (!File.Exists(singleFile))
                singleFile = Directory.EnumerateFiles(reportDir, "*.html").FirstOrDefault();

            if (singleFile is null)
            {
                _log.LogWarning("Run {RunId}: allure generate produced no .html", spec.RunId);
                return FallbackReportPath(runDir);
            }

            Pub(eventKey, EventKinds.System, "run", "Allure report ready.", level: EventLevels.Success);
            return singleFile;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Run {RunId}: allure generate failed", spec.RunId);
            return FallbackReportPath(runDir);
        }
    }

    private static string? FallbackReportPath(string runDir)
    {
        var pwHtml = Path.Combine(runDir, "html", "index.html");
        return File.Exists(pwHtml) ? pwHtml : null;
    }

    private void SeedHistory(string resultsDir, PlaywrightRunSpec spec)
    {
        var storeDir = HistoryStoreDir(spec);
        if (!Directory.Exists(storeDir)) return;
        var histDest = Path.Combine(resultsDir, "history");
        Directory.CreateDirectory(histDest);
        foreach (var f in Directory.EnumerateFiles(storeDir))
            File.Copy(f, Path.Combine(histDest, Path.GetFileName(f)), overwrite: true);
    }

    private void CopyHistoryBack(string resultsDir, PlaywrightRunSpec spec)
    {
        var histSrc = Path.Combine(resultsDir, "history");
        if (!Directory.Exists(histSrc)) return;
        var storeDir = HistoryStoreDir(spec);
        Directory.CreateDirectory(storeDir);
        foreach (var f in Directory.EnumerateFiles(histSrc))
            File.Copy(f, Path.Combine(storeDir, Path.GetFileName(f)), overwrite: true);
    }

    private string HistoryStoreDir(PlaywrightRunSpec spec)
    {
        var moduleKey   = string.IsNullOrEmpty(spec.Module) ? "_all" : spec.Module;
        var pipelineKey = spec.TriggerPipelineId?.ToString() ?? "manual";
        return Path.Combine(_historyDir, moduleKey, pipelineKey);
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
        // "allure" (unscoped) is the real published Node-native Allure 3 CLI (no JRE) — confirmed
        // against the npm registry; "@allurereport/cli" never existed (404) and silently broke every
        // provisioning attempt. allure-playwright is the Playwright reporter.
        // ponytail: if "allure" generate --single-file is ever unsupported, fallback = allure-commandline + bundled JRE
        const string json = """
            {
              "name": "qa-playwright-runtime",
              "private": true,
              "devDependencies": {
                "@playwright/test": "latest",
                "allure-playwright": "latest",
                "allure": "latest"
              }
            }
            """;
        File.WriteAllText(Path.Combine(dir, "package.json"), json);
    }

    // ── Process spawn ────────────────────────────────────────────────────────

    private async Task<int> SpawnAsync(
        string exe, string args, string workDir, string? profile, string eventKey, CancellationToken ct,
        bool suppressOutput = false, string? baseUrl = null, string? bearerToken = null)
    {
        // npm/npx are .cmd shims on Windows — bare ProcessStartInfo (UseShellExecute=false) can't
        // resolve them via PATHEXT the way a real shell does, and fails with "cannot find the file
        // specified". Resolve the real path first, same PATHEXT-aware lookup the Claude CLI uses.
        var resolvedExe = ClaudeCli.Which(exe) ?? exe;
        var psi = new ProcessStartInfo(resolvedExe, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ApplyAutomationEnv(psi.Environment, profile, baseUrl, bearerToken);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void RelayLine(string? line, bool isErr)
        {
            if (string.IsNullOrEmpty(line) || suppressOutput) return;
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

    /// <summary>Sets AUTOMATION_PROFILE/AUTOMATION_BASE_URL/AUTOMATION_BEARER_TOKEN on the child
    /// process environment (QAL-068). Extracted from <see cref="SpawnAsync"/> so the mapping is
    /// unit-testable against a plain dictionary without spawning a real process.</summary>
    internal static void ApplyAutomationEnv(
        IDictionary<string, string?> env, string? profile, string? baseUrl, string? bearerToken)
    {
        if (!string.IsNullOrEmpty(profile))
            env["AUTOMATION_PROFILE"] = profile;
        if (!string.IsNullOrEmpty(baseUrl))
            env["AUTOMATION_BASE_URL"] = baseUrl;
        if (!string.IsNullOrEmpty(bearerToken))
            env["AUTOMATION_BEARER_TOKEN"] = bearerToken;
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
