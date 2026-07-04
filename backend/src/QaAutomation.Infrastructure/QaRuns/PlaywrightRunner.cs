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
    private readonly IAutomationAuthenticator _authenticator;
    private readonly AllureEnricher _enricher;
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
        IEventBus bus, PipelineConcurrency concurrency, IAutomationAuthenticator authenticator,
        AllureEnricher enricher,
        IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop,
        IOptions<AzureDevOpsOptions> ado,
        ILogger<PlaywrightRunner> log)
    {
        _bus = bus;
        _concurrency = concurrency;
        _authenticator = authenticator;
        _enricher = enricher;
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

        // Need either spec file paths OR numeric grep tags to select tests
        if (spec.SpecFilePaths.Count == 0 && spec.GrepTags.Count == 0)
        {
            _log.LogWarning("Run {RunId}: no spec files or grep tags — aborting", spec.RunId);
            return new PlaywrightRunResult { ExitCode = 1, Failed = 0 };
        }

        var runDir = Path.Combine(_runsDir, spec.RunId.ToString());
        Directory.CreateDirectory(runDir);

        var junitPath   = Path.Combine(runDir, "junit.xml");
        var resultsPath = Path.Combine(runDir, "results.json");

        WriteRunConfig(runDir, spec);

        var testOutputDir = Path.Combine(runDir, "test-output");

        // Prefer spec file paths (clean titles, no [tcId] in name) over grep-by-title fallback.
        // Selection itself lives in playwright.config.ts's testMatch (see WriteRunConfig) — NOT as
        // CLI positional args, which blow past Windows' ~8191-char cmd.exe command-line limit for
        // any run selecting more than a couple dozen specs ("The command line is too long").
        string args;
        int testCount;
        if (spec.SpecFilePaths.Count > 0)
        {
            args = $"test --output={EscPath(testOutputDir)}";
            testCount = spec.SpecFilePaths.Count;
        }
        else
        {
            var grep = BuildGrep(spec.GrepTags);
            args = $"test --grep \"{grep}\" --output={EscPath(testOutputDir)}";
            testCount = spec.GrepTags.Count;
        }

        Pub(eventKey, EventKinds.Phase, "run", $"Playwright run {spec.RunId} starting · {testCount} test case(s) selected (profile: {spec.Profile})");

        var sw = Stopwatch.StartNew();
        int exitCode;

        // Pre-run auth (QAL-072): the API base URL is the SAME for every DMS (one APIM gateway), and
        // the bearer is minted once per run session from the shared QA-admin creds — mirrors the CRM
        // automation project's global-setup token mint. Falls back to the per-profile registry values
        // only when the shared automation auth isn't configured (keeps older setups working).
        var profileEntry = DmsProfileOptions.All.FirstOrDefault(p => p.Key == spec.Profile);
        var baseUrl = _authenticator.ApiBaseUrl ?? profileEntry?.BaseUrl;
        var bearerToken = await _authenticator.GetBearerTokenAsync(ct: ct) ?? profileEntry?.BearerToken;
        if (string.IsNullOrEmpty(baseUrl))
            Pub(eventKey, EventKinds.System, "run",
                "No API base URL configured (set LEAD_API_BASE_URL in .env) — tests will hit localhost.",
                EventLevels.Warn);
        else
            Pub(eventKey, EventKinds.System, "run",
                $"Auth ready · target {baseUrl}" + (bearerToken is null ? " · no bearer (unauthenticated)" : " · bearer minted"));

        await _concurrency.Gate.WaitAsync(ct);
        try
        {
            exitCode = await SpawnAsync("npx", $"playwright {args}", runDir, spec.Profile, eventKey, ct,
                suppressOutput: !spec.ActivityEnabled,
                baseUrl: baseUrl, bearerToken: bearerToken);
        }
        finally
        {
            _concurrency.Gate.Release();
        }

        sw.Stop();
        WriteAllureContext(runDir, spec);
        var allureResultsDir = Path.Combine(runDir, "allure-results");
        if (Directory.Exists(allureResultsDir))
            await _enricher.EnrichAsync(allureResultsDir, spec, eventKey, ct);
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

    /// <summary>Re-enrich + regenerate the Allure report for an already-executed run from its
    /// preserved allure-results — no Playwright execution, no ADO writes. Returns the new report
    /// index.html path, or null when the run left no allure-results behind.</summary>
    public async Task<string?> RebuildReportAsync(PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
    {
        await EnsureRuntimeAsync(eventKey, ct);
        var runDir = Path.Combine(_runsDir, spec.RunId.ToString());
        var resultsDir = Path.Combine(runDir, "allure-results");
        if (!Directory.Exists(resultsDir)) return null;

        WriteAllureContext(runDir, spec);
        await _enricher.EnrichAsync(resultsDir, spec, eventKey, ct);
        return await GenerateAllureReportAsync(runDir, spec, eventKey, ct);
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

        // Test selection lives in the config's testMatch, NOT as CLI positional args: Windows
        // invokes npx.cmd through cmd.exe, whose command-line buffer caps out at ~8191 chars — a
        // run selecting ~90+ specs (typical "whole module" run) blows past that with "The command
        // line is too long" and the process never even starts Node, so it looks exactly like a
        // silent 0/0/0 collection crash. A config file has no such limit.
        var testMatchLine = spec.SpecFilePaths.Count > 0
            ? $"\n  testMatch: [{string.Join(", ", spec.SpecFilePaths.Select(p => $"'{EscPath(p)}'"))}],"
            : "";

        var config = $$"""
            import { defineConfig } from '@playwright/test';
            import path from 'path';
            export default defineConfig({
              testDir: '{{testDir}}',{{testMatchLine}}
              // Cap every layer so a run can never hang indefinitely against a live QA API:
              // per-test 30s, per-assertion 10s, and a whole-run globalTimeout (15m default).
              timeout: 30_000,
              globalTimeout: Number(process.env.AUTOMATION_GLOBAL_TIMEOUT_MS ?? '900000'),
              expect: { timeout: 10_000 },
              // Parallelise across spec files (workers) for speed; keep tests within a file ordered.
              fullyParallel: false,
              workers: Number(process.env.AUTOMATION_WORKERS ?? '6'),
              retries: 0,
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

        try
        {
            Pub(eventKey, EventKinds.System, "run", "Generating Allure report…");
            // `allure awesome` (not `generate`) because it exposes the two things the report needs
            // (confirmed against the installed 3.14 CLI source):
            //  - --history-path: Allure 3 keeps history in a single .jsonl the generator both reads
            //    AND appends — the Allure-2-style history/ folder copying never did anything here.
            //  - default --group-by is parentSuite,suite,subSuite — exactly the labels
            //    AllureEnricher rewrites, so the tree groups by Contract/Endpoint/Workflow level.
            // No --clean flag exists; delete reportDir ourselves. The report is unavoidably a
            // multi-file directory, served via the report/{subPath} catch-all in QaRunsController.
            if (Directory.Exists(reportDir))
                Directory.Delete(reportDir, recursive: true);
            var historyFile = HistoryFile(spec);
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            // ASCII-only name, and NO --history-limit: `allure awesome` doesn't define that option
            // (only `generate`/`history` do) and clipanion fails the whole command with
            // "Unknown Syntax Error: Command not found" on any unknown flag.
            var reportName = $"QA Loop Run {spec.RunId} - {spec.Module}";
            var genExit = await SpawnAsync(
                "npx",
                $"allure awesome {EscPath(resultsDir)} -o {EscPath(reportDir)} " +
                $"--history-path {EscPath(historyFile)} --report-name \"{reportName}\"",
                runDir, profile: null, eventKey, ct);

            if (genExit != 0)
            {
                _log.LogWarning("Run {RunId}: allure awesome exited {Code}", spec.RunId, genExit);
                return FallbackReportPath(runDir);
            }

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

    /// <summary>Allure 3 history file (.jsonl, read + appended by `allure awesome`), kept per
    /// module+pipeline so trend charts compare like-for-like runs.</summary>
    private string HistoryFile(PlaywrightRunSpec spec)
    {
        var moduleKey   = string.IsNullOrEmpty(spec.Module) ? "_all" : spec.Module;
        var pipelineKey = spec.TriggerPipelineId?.ToString() ?? "manual";
        return Path.Combine(_historyDir, moduleKey, pipelineKey, "history.jsonl");
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
            env.AppendLine($"run.id={spec.RunId}");
            env.AppendLine($"profile={spec.Profile}");
            env.AppendLine($"module={spec.Module}");
            if (!string.IsNullOrEmpty(spec.LevelFilter)) env.AppendLine($"level.filter={spec.LevelFilter}");
            env.AppendLine($"trigger.source={spec.TriggerSourceWire}");
            if (spec.TriggerBuildId.HasValue)  env.AppendLine($"build.id={spec.TriggerBuildId}");
            if (!string.IsNullOrEmpty(spec.TriggerCommitSha)) env.AppendLine($"commit={spec.TriggerCommitSha}");
            if (spec.AdoTestRunId.HasValue)    env.AppendLine($"ado.test.run.id={spec.AdoTestRunId}");

            // Impact analysis (pipeline-triggered runs): why this run happened and what changed.
            if (!string.IsNullOrEmpty(spec.ImpactAnalysisJson))
            {
                try
                {
                    var impact = JsonSerializer.Deserialize<ImpactAnalysis>(spec.ImpactAnalysisJson);
                    if (impact is not null)
                    {
                        env.AppendLine($"impact.impacted.tests={impact.ImpactedDraftIds.Count}");
                        env.AppendLine($"impact.confidence={impact.Confidence}");
                        var reasoning = impact.Reasoning.ReplaceLineEndings(" ").Trim();
                        if (reasoning.Length > 400) reasoning = reasoning[..400] + "…";
                        if (reasoning.Length > 0) env.AppendLine($"impact.reasoning={reasoning}");
                    }
                }
                catch (JsonException) { /* malformed impact JSON — report still generates */ }
            }
            File.WriteAllText(Path.Combine(resultsDir, "environment.properties"), env.ToString());

            // No categories.json: Allure 3 ignores the Allure-2 file and ships a built-in
            // "Product errors" view that groups failures by assertion message — which already
            // separates "Expected 200 / Received 500" from "Expected 500 / Received [400,422]" etc.

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
        // "zod" is required at spec load time: generated contract specs `import { z } from 'zod'`
        // to validate response shapes. Without it every contract spec throws "Cannot find module
        // 'zod'" while Playwright is collecting tests, so the whole run silently reports 0 tests /
        // exit 1 (the load errors are swallowed when ActivityEnabled is off).
        const string json = """
            {
              "name": "qa-playwright-runtime",
              "private": true,
              "devDependencies": {
                "@playwright/test": "latest",
                "allure-playwright": "latest",
                "allure": "latest",
                "zod": "latest"
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

        using var proc = new Process { StartInfo = psi };

        // Bounded tail buffer of raw lines, kept regardless of suppressOutput, so a failure can be
        // diagnosed even when live relay is off — otherwise a fast crash (bad import, missing module)
        // surfaces only as "exit 1 / 0 passed" with no clue why (see QAL-071b).
        var tail = new List<string>();
        const int MaxTailLines = 40;

        void RelayLine(string? line, bool isErr)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (tail) { if (tail.Count < MaxTailLines) tail.Add(line); }
            if (suppressOutput) return;
            Pub(eventKey, EventKinds.Stat, "run", line, isErr ? EventLevels.Warn : EventLevels.Info);
        }

        proc.OutputDataReceived += (_, e) => RelayLine(e.Data, false);
        proc.ErrorDataReceived  += (_, e) => RelayLine(e.Data, true);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        // Heartbeat: Playwright/Node buffers stdout when piped (not a TTY), so the live stat lines
        // arrive in a burst near the very end — without this the console looks frozen for minutes
        // between "starting" and the flush. Emit a periodic "still running" event so the UI shows
        // liveness while the child process works.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = DateTime.UtcNow;
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
                    if (heartbeatCts.IsCancellationRequested) break;
                    var secs = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                    Pub(eventKey, EventKinds.System, "run",
                        $"Still running… {secs}s elapsed (Playwright buffers output; it appears when the run finishes)");
                }
            }
            catch (OperationCanceledException) { /* process exited */ }
        }, heartbeatCts.Token);

        // WaitForExitAsync (not the Exited event) is required here: Process.Exited can fire before the
        // redirected output pipes finish draining into OutputDataReceived/ErrorDataReceived, especially
        // for a process that crashes in milliseconds — losing exactly the error text needed to diagnose
        // a fast collection-time crash. The parameterless overload also blocks until buffered async
        // reads are flushed, so `tail` is complete once this returns.
        await proc.WaitForExitAsync(ct);
        heartbeatCts.Cancel();
        try { await heartbeat; } catch { /* ignore */ }
        var exitCode = proc.ExitCode;

        if (exitCode != 0)
        {
            List<string> snapshot;
            lock (tail) { snapshot = [.. tail]; }
            if (snapshot.Count > 0)
            {
                var summary = string.Join('\n', snapshot);
                _log.LogWarning("{Exe} {Args} exited {Code} in {Dir}:\n{Output}", exe, args, exitCode, workDir, summary);
                if (suppressOutput)
                    Pub(eventKey, EventKinds.Stat, "run", summary, EventLevels.Warn);
            }
        }

        return exitCode;
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

    /// <summary>Pull the meaningful assertion out of a Playwright JUnit failure/error body. The raw
    /// text starts with the "[chromium] › file.spec.ts:line › …" locator; we skip that and surface the
    /// Error/Expected/Received lines so the failure reason + bug draft show WHY it failed.</summary>
    private static string? ExtractErrorMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        static bool IsLocator(string l) => l.StartsWith('[') && (l.Contains('\u203a') || l.Contains(".spec.ts"));
        var meaningful = lines.Where(l => !IsLocator(l)).ToList();
        if (meaningful.Count == 0) meaningful = lines;
        var errorLine = meaningful.FirstOrDefault(l =>
            l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) || l.Contains("expect(", StringComparison.OrdinalIgnoreCase));
        var expected = meaningful.FirstOrDefault(l => l.StartsWith("Expected:", StringComparison.OrdinalIgnoreCase));
        var received = meaningful.FirstOrDefault(l => l.StartsWith("Received:", StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (errorLine is not null) parts.Add(errorLine);
        if (expected is not null) parts.Add(expected);
        if (received is not null) parts.Add(received);
        return parts.Count > 0 ? string.Join("  ", parts) : meaningful[0];
    }

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
                .Select(tc =>
                {
                    // Extract spec file path from parent testsuite — Playwright's JUnit reporter
                    // sets the 'file' attribute to the absolute path (v1.42+). Fall back to the
                    // 'name' attribute which includes the relative path (e.g. "contracts/foo.spec.ts [chromium]").
                    var suiteFile = tc.Parent?.Attribute("file")?.Value;
                    if (suiteFile == null)
                    {
                        var suiteName = tc.Parent?.Attribute("name")?.Value ?? "";
                        var bracketIdx = suiteName.LastIndexOf(" [");
                        suiteFile = bracketIdx > 0 ? suiteName[..bracketIdx] : suiteName;
                    }
                    return new FailedTestResult
                    {
                        TestTitle    = tc.Attribute("name")?.Value ?? "",
                        ErrorMessage = ExtractErrorMessage((tc.Element("failure") ?? tc.Element("error"))?.Value),
                        SpecFilePath = string.IsNullOrEmpty(suiteFile) ? null : suiteFile,
                    };
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
