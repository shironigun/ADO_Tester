using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Llm;
using QaAutomation.Infrastructure.QaRuns;

namespace QaAutomation.Infrastructure.ScriptsBoard;

/// <summary>
/// Scripts Board's raw terminal execution path. It shares PlaywrightRunner's runtime, configuration,
/// report, and parsing logic but never changes that runner's line-buffered QA Runs spawn path.
/// </summary>
public sealed class ScriptsBoardRunner : IScriptsBoardRunner
{
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly PlaywrightRunner _playwrightRunner;
    private readonly IEventBus _bus;
    private readonly ILogger<ScriptsBoardRunner> _log;

    public ScriptsBoardRunner(
        PlaywrightRunner playwrightRunner,
        IEventBus bus,
        ILogger<ScriptsBoardRunner> log)
    {
        _playwrightRunner = playwrightRunner;
        _bus = bus;
        _log = log;
    }

    public async Task<PlaywrightRunResult> RunWithLiveTermAsync(
        PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
    {
        var (baseUrl, bearerToken) = await _playwrightRunner.PrepareExternalRunAsync(spec, eventKey, ct);
        if (spec.SpecFilePaths.Count == 0 && spec.GrepTags.Count == 0)
            return new PlaywrightRunResult { ExitCode = 1 };

        var runDir = Path.Combine(_playwrightRunner.RunsDirectory, spec.RunId.ToString());
        Directory.CreateDirectory(runDir);
        var junitPath = Path.Combine(runDir, "junit.xml");
        var resultsPath = Path.Combine(runDir, "results.json");
        PlaywrightConfigWriter.WriteRunConfig(
            runDir, spec, _playwrightRunner.ScriptsDirectory, _playwrightRunner.RuntimeDirectory,
            _playwrightRunner.ReportProvider,
            ex => _log.LogWarning(ex, "Could not link node_modules; Playwright may re-install"));

        var testOutputDir = Path.Combine(runDir, "test-output");
        var args = spec.SpecFilePaths.Count > 0
            ? $"playwright test --output={PlaywrightConfigWriter.EscPath(testOutputDir)}"
            : $"playwright test --grep \"{BuildGrep(spec.GrepTags)}\" --output={PlaywrightConfigWriter.EscPath(testOutputDir)}";

        Publish(eventKey, EventKinds.Phase, "run",
            $"Scripts Board run {spec.RunId} starting · raw terminal stream enabled");

        var stopwatch = Stopwatch.StartNew();
        await _playwrightRunner.Concurrency.Gate.WaitAsync(ct);
        int exitCode;
        try
        {
            exitCode = await SpawnWithRawTerminalAsync("npx", args, runDir, spec.Profile, baseUrl, bearerToken, eventKey, ct);
        }
        finally
        {
            _playwrightRunner.Concurrency.Gate.Release();
        }

        stopwatch.Stop();
        var reportPath = await _playwrightRunner.FinalizeExternalRunAsync(runDir, spec, eventKey, ct);
        var junit = PlaywrightResultParser.ParseJUnit(
            junitPath, ex => _log.LogWarning(ex, "JUnit parse failed for {Path}", junitPath));

        Publish(eventKey, EventKinds.Result, "run",
            $"Run {spec.RunId} done — exit {exitCode} · {junit.Passed} passed · {junit.Failed} failed · {junit.Skipped} skipped",
            exitCode == 0 ? EventLevels.Success : EventLevels.Warn);

        return new PlaywrightRunResult
        {
            ExitCode = exitCode,
            Passed = junit.Passed,
            Failed = junit.Failed,
            Skipped = junit.Skipped,
            DurationMs = stopwatch.ElapsedMilliseconds,
            JunitPath = File.Exists(junitPath) ? junitPath : null,
            ResultsJsonPath = File.Exists(resultsPath) ? resultsPath : null,
            ReportPath = reportPath,
            FailedTests = junit.FailedTests,
        };
    }

    private async Task<int> SpawnWithRawTerminalAsync(
        string exe,
        string args,
        string workDir,
        string? profile,
        string? baseUrl,
        string? bearerToken,
        string eventKey,
        CancellationToken ct)
    {
        var resolvedExe = ClaudeCli.Which(exe) ?? exe;
        var psi = new ProcessStartInfo(resolvedExe, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        PlaywrightRunner.ApplyAutomationEnv(psi.Environment, profile, baseUrl, bearerToken);
        psi.Environment["FORCE_COLOR"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process already exited */ }
        });

        var tail = new StringBuilder();
        var stdout = RelayStreamAsync(process.StandardOutput.BaseStream, eventKey, EventLevels.Info, tail, ct);
        var stderr = RelayStreamAsync(process.StandardError.BaseStream, eventKey, EventLevels.Warn, tail, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0 && tail.Length > 0)
            _log.LogWarning("{Exe} {Args} exited {Code} in {Dir}:\n{Output}",
                exe, args, process.ExitCode, workDir, tail.ToString());

        return process.ExitCode;
    }

    private async Task RelayStreamAsync(
        Stream stream, string eventKey, string level, StringBuilder tail, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();

        void RelayChars(int count)
        {
            if (count == 0) return;
            var chunk = new string(chars, 0, count);
            Publish(eventKey, EventKinds.Term, "run", chunk, level);
            var plain = AnsiEscape.Replace(chunk, string.Empty);
            lock (tail)
            {
                const int maxTailLength = 8_000;
                if (tail.Length < maxTailLength)
                    tail.Append(plain.AsSpan(0, Math.Min(plain.Length, maxTailLength - tail.Length)));
            }
        }

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            RelayChars(decoder.GetChars(buffer, 0, read, chars, 0, flush: false));
        }

        RelayChars(decoder.GetChars([], 0, 0, chars, 0, flush: true));
    }

    private void Publish(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level });

    private static string BuildGrep(IReadOnlyList<int> ids) =>
        string.Join("|", ids.Select(id => $@"\[{id}\]"));
}
