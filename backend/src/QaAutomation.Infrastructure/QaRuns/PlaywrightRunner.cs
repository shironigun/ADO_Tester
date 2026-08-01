using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    private readonly IOptionsMonitor<QaLoopOptions> _qaLoop;
    private readonly string _scriptsDir;
    private readonly string _runsDir;
    private readonly string _runtimeDir;
    private readonly ILogger<PlaywrightRunner> _log;
    private readonly string _reportProvider;
    private readonly string _adoBaseUrl;
    private readonly string _historyDir;
    private readonly string? _fixturesDir;

    // Provisioned exactly once; guarded by _runtimeLock.
    private volatile bool _runtimeReady;
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);

    public PlaywrightRunner(
        IEventBus bus, PipelineConcurrency concurrency, IAutomationAuthenticator authenticator,
        AllureEnricher enricher,
        IOptions<StorageOptions> storage, IOptionsMonitor<QaLoopOptions> qaLoop,
        IOptions<AzureDevOpsOptions> ado,
        ILogger<PlaywrightRunner> log)
    {
        _bus = bus;
        _concurrency = concurrency;
        _authenticator = authenticator;
        _enricher = enricher;
        _qaLoop = qaLoop;
        var dataDir = storage.Value.DataDirectory;
        var qa = qaLoop.CurrentValue;
        _scriptsDir      = Path.GetFullPath(Path.Combine(dataDir, qa.ScriptsFolderName));
        _runsDir         = Path.GetFullPath(Path.Combine(dataDir, qa.RunsFolderName));
        _runtimeDir      = Path.GetFullPath(Path.Combine(dataDir, "playwright-runtime"));
        _historyDir      = Path.GetFullPath(Path.Combine(dataDir, "allure-history"));
        _reportProvider  = qa.ReportProvider;
        _adoBaseUrl      = ado.Value.BaseUrl;
        _fixturesDir     = ResolveFixturesDir(dataDir);
        _log = log;
    }

    public async Task<PlaywrightRunResult> RunAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
    {
        string? baseUrl;
        string? bearerToken;
        try
        {
            (baseUrl, bearerToken) = await PrepareExternalRunAsync(spec, eventKey, ct);
        }
        catch (InvalidOperationException ex)
        {
            Pub(eventKey, EventKinds.Error, "run", ex.Message, EventLevels.Error);
            return new PlaywrightRunResult { ExitCode = 1, Failed = 0 };
        }

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

        await _concurrency.Gate.WaitAsync(ct);
        try
        {
            exitCode = await SpawnAsync("npx", $"playwright {args}", runDir, spec, eventKey, ct,
                suppressOutput: !spec.ActivityEnabled,
                baseUrl: baseUrl, bearerToken: bearerToken);
        }
        finally
        {
            _concurrency.Gate.Release();
        }

        sw.Stop();
        var reportPath = await FinalizeExternalRunAsync(runDir, spec, eventKey, ct);
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

    /// <summary>
    /// Prepares shared runtime and target authentication for the separate Scripts Board raw-terminal
    /// process path. Internal so the public QA Runs interface remains unchanged.
    /// </summary>
    internal async Task<(string? BaseUrl, string? BearerToken)> PrepareExternalRunAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct)
    {
        await EnsureRuntimeAsync(eventKey, ct);

        if (spec.IsUiRun)
        {
            var qa = _qaLoop.CurrentValue;
            if (string.IsNullOrWhiteSpace(qa.AppUrl)
                || string.IsNullOrWhiteSpace(qa.AppUsername)
                || string.IsNullOrWhiteSpace(qa.AppPassword))
            {
                throw new InvalidOperationException(
                    "UI run requires AUTOMATION_APP_URL / AUTOMATION_APP_USERNAME / AUTOMATION_APP_PASSWORD (or qaloop.app_* settings).");
            }
            if (string.IsNullOrWhiteSpace(_fixturesDir))
            {
                throw new InvalidOperationException(
                    "UI run could not locate scripts/fixtures (auth.setup.ts). Ensure the repo scripts/fixtures folder is present.");
            }
            Pub(eventKey, EventKinds.System, "run",
                $"UI auth ready · app {qa.AppUrl} · storageState via {_fixturesDir}");
            return (qa.AppUrl.TrimEnd('/'), null);
        }

        // Shared API host for all DMS — never fall back to profile BaseUrl / static bearer.
        var baseUrl = _authenticator.ApiBaseUrl;
        var bearerToken = await _authenticator.GetBearerTokenAsync(ct: ct);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                "No API base URL configured — set AUTOMATION_API_BASE_URL in .env.");
        }
        if (_authenticator.IsConfigured && string.IsNullOrEmpty(bearerToken))
        {
            throw new InvalidOperationException(
                "Automation auth is configured but bearer mint failed (WEB_API_BASE_URL + AUTH_*).");
        }
        Pub(eventKey, EventKinds.System, "run",
            $"Auth ready · target {baseUrl}" + (bearerToken is null ? " · no bearer (unauthenticated)" : " · bearer minted"));
        return (baseUrl, bearerToken);
    }

    internal string RunsDirectory => _runsDir;
    internal string ScriptsDirectory => _scriptsDir;
    internal string RuntimeDirectory => _runtimeDir;
    internal string ReportProvider => _reportProvider;
    internal string? FixturesDirectory => _fixturesDir;
    internal PipelineConcurrency Concurrency => _concurrency;
    internal IOptionsMonitor<QaLoopOptions> QaLoopOptions => _qaLoop;

    /// <summary>Runs the existing Allure enrichment/report pipeline after an external process path exits.</summary>
    internal async Task<string?> FinalizeExternalRunAsync(
        string runDir, PlaywrightRunSpec spec, string eventKey, CancellationToken ct)
    {
        WriteAllureContext(runDir, spec);
        var allureResultsDir = Path.Combine(runDir, "allure-results");
        if (Directory.Exists(allureResultsDir))
            await _enricher.EnrichAsync(allureResultsDir, spec, eventKey, ct);
        return await GenerateAllureReportAsync(runDir, spec, eventKey, ct);
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

    /// <summary>Best-effort recursive delete of the run's artifact directory (<c>{runsDir}/{runId}</c>).
    /// Swallows all IO errors — a run row must still be deletable even if its files are gone or locked.</summary>
    public void DeleteRunArtifacts(int runId)
    {
        try
        {
            var runDir = Path.Combine(_runsDir, runId.ToString());
            if (Directory.Exists(runDir))
                Directory.Delete(runDir, recursive: true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to delete artifact directory for run {RunId}", runId);
        }
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
            var installExit = await SpawnAsync("npm", "install", _runtimeDir, new PlaywrightRunSpec(), eventKey, ct);
            if (installExit != 0)
                throw new InvalidOperationException($"npm install exited {installExit} — Playwright runtime not provisioned.");

            var pwExit = await SpawnAsync("npx", "playwright install --with-deps chromium", _runtimeDir, new PlaywrightRunSpec(), eventKey, ct);
            if (pwExit != 0)
                throw new InvalidOperationException($"playwright install exited {pwExit} — browser binaries not provisioned.");

            // Allure capability check — log version; a non-zero exit here only warns (generate handled later)
            // ponytail: fallback if @allurereport/cli lacks --single-file: ship allure-commandline + bundled JRE
            var allureExit = await SpawnAsync("npx", "allure --version", _runtimeDir, new PlaywrightRunSpec(), eventKey, ct);
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
                try { PlaywrightConfigWriter.LinkDirectory(scriptsNm, Path.Combine(_runtimeDir, "node_modules")); }
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
        PlaywrightConfigWriter.WriteRunConfig(
            runDir, spec, _scriptsDir, _runtimeDir, _reportProvider,
            ex => _log.LogWarning(ex, "Could not link node_modules; Playwright may re-install"),
            fixturesDir: spec.IsUiRun ? _fixturesDir : null);
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
                runDir, spec, eventKey, ct);

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
        string exe, string args, string workDir, PlaywrightRunSpec spec, string eventKey, CancellationToken ct,
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
        var qa = _qaLoop.CurrentValue;
        ApplyAutomationEnv(psi.Environment, spec.Profile, baseUrl, bearerToken,
            appUrl: spec.IsUiRun ? qa.AppUrl : null,
            appUsername: spec.IsUiRun ? qa.AppUsername : null,
            appPassword: spec.IsUiRun ? qa.AppPassword : null);

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

    /// <summary>Sets AUTOMATION_PROFILE/AUTOMATION_BASE_URL/AUTOMATION_BEARER_TOKEN/BASE_URL (and
    /// optional AUTOMATION_APP_* for UI) on the child process environment (QAL-068). Extracted from
    /// <see cref="SpawnAsync"/> so the mapping is unit-testable against a plain dictionary without
    /// spawning a real process.</summary>
    internal static void ApplyAutomationEnv(
        IDictionary<string, string?> env, string? profile, string? baseUrl, string? bearerToken,
        string? appUrl = null, string? appUsername = null, string? appPassword = null)
    {
        if (!string.IsNullOrEmpty(profile))
            env["AUTOMATION_PROFILE"] = profile;
        if (!string.IsNullOrEmpty(baseUrl))
        {
            env["AUTOMATION_BASE_URL"] = baseUrl;
            env["BASE_URL"] = baseUrl;
        }
        if (!string.IsNullOrEmpty(bearerToken))
            env["AUTOMATION_BEARER_TOKEN"] = bearerToken;
        if (!string.IsNullOrEmpty(appUrl))
            env["AUTOMATION_APP_URL"] = appUrl;
        if (!string.IsNullOrEmpty(appUsername))
            env["AUTOMATION_APP_USERNAME"] = appUsername;
        if (!string.IsNullOrEmpty(appPassword))
            env["AUTOMATION_APP_PASSWORD"] = appPassword;
    }

    /// <summary>Walk up from known roots looking for scripts/fixtures/auth.setup.ts.</summary>
    internal static string? ResolveFixturesDir(string? dataDirectory = null)
    {
        var starts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            starts.Add(Path.GetFullPath(dataDirectory));
        try { starts.Add(Directory.GetCurrentDirectory()); } catch { /* ignore */ }
        try { starts.Add(AppContext.BaseDirectory); } catch { /* ignore */ }

        foreach (var start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = start;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "scripts", "fixtures");
                if (File.Exists(Path.Combine(candidate, "auth.setup.ts")))
                    return Path.GetFullPath(candidate);
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        return null;
    }

    // ── Grep builder ────────────────────────────────────────────────────────

    private static string BuildGrep(IReadOnlyList<int> ids) =>
        string.Join("|", ids.Select(id => $@"\[{id}\]"));

    private (int Passed, int Failed, int Skipped, IReadOnlyList<FailedTestResult> FailedTests)
        ParseJUnit(string junitPath)
        => PlaywrightResultParser.ParseJUnit(
            junitPath, ex => _log.LogWarning(ex, "JUnit parse failed for {Path}", junitPath));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private static string EscPath(string path) => path.Replace('\\', '/');
}
