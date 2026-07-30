using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Spawns the local Claude CLI in agentic mode with Playwright MCP loaded to generate and heal
/// Playwright <c>.ui.spec.ts</c> files. The cwd is set to <see cref="QaLoopOptions.PlaywrightRoot"/>
/// so the CLI auto-discovers <c>.mcp.json</c> and <c>.claude/agents/</c>. Scoped.
/// </summary>
public sealed class PlaywrightAgentService : IPlaywrightAgentService
{
    private readonly QaLoopOptions _opts;
    private readonly ClaudeCliOptions _cli;
    private readonly ILogger<PlaywrightAgentService> _log;

    public PlaywrightAgentService(
        IOptions<QaLoopOptions> opts, IOptions<ClaudeCliOptions> cli,
        ILogger<PlaywrightAgentService> log)
    {
        _opts = opts.Value;
        _cli = cli.Value;
        _log = log;
    }

    public async Task<string> GenerateScriptAsync(
        string caseTitle, string stepsJson, string bucket, int tcIdPending,
        IProgress<string>? progress, CancellationToken ct, string? moduleRoute = null)
    {
        var root = RequireRoot();
        var outDir = Path.Combine(root, "generated", "ui", bucket);
        Directory.CreateDirectory(outDir);
        var slug = Slug(caseTitle);
        var specPath = Path.Combine(outDir, $"{slug}-{tcIdPending}.ui.spec.ts");
        var prompt = BuildGeneratePrompt(caseTitle, stepsJson, bucket, specPath, _opts.AppUrl, moduleRoute);
        await RunAgentAsync(root, prompt, progress, ct);
        return specPath;
    }

    public async Task HealAsync(
        string specFilePath, string failureMessage,
        IProgress<string>? progress, CancellationToken ct)
    {
        var root = RequireRoot();
        var prompt = BuildHealPrompt(specFilePath, failureMessage);
        await RunAgentAsync(root, prompt, progress, ct);
    }

    // ── Internal helpers (exposed for unit testing) ──────────────────────────

    /// <summary>Returns the CLI argument list used for every agent invocation.
    /// Exposed as <c>internal</c> so the unit test can verify the args without spawning a process.</summary>
    internal IReadOnlyList<string> BuildAgentArgs()
    {
        var args = new List<string> { "-p", "--output-format", "stream-json" };
        var mcpConfig = Path.Combine(_opts.PlaywrightRoot, ".mcp.json");
        if (File.Exists(mcpConfig))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfig);
        }
        if (!string.IsNullOrEmpty(_cli.Model))
        {
            args.Add("--model");
            args.Add(_cli.Model);
        }
        return args;
    }

    private string RequireRoot()
    {
        var root = _opts.PlaywrightRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "PlaywrightRoot is not configured. Set AUTOMATION_PLAYWRIGHT_ROOT in the .env.");
        if (!File.Exists(Path.Combine(root, ".mcp.json")))
            throw new InvalidOperationException(
                $"Playwright MCP is not initialized at '{root}'. Run 'npx playwright init-agents --loop=claude' from the framework root.");
        return root;
    }

    private async Task RunAgentAsync(string cwd, string prompt, IProgress<string>? progress, CancellationToken ct)
    {
        var cli = ClaudeCli.ResolvePath(_cli.Path)
            ?? throw new InvalidOperationException(
                $"Claude CLI not found (configured path: '{_cli.Path}'). " +
                "Install Claude Code and ensure the claude executable is on PATH or set CLAUDE_CLI_PATH.");

        var args = BuildAgentArgs();
        _log.LogInformation("Spawning Playwright agent — cli={Cli} cwd={Cwd} model={Model}",
            cli, cwd, _cli.Model);

        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Strip API key env vars so the CLI uses the subscription login (mirrors ClaudeCliLlmClient).
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Feed prompt on a background task to avoid stdin/stdout deadlock.
        _ = Task.Run(async () =>
        {
            try
            {
                await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                proc.StandardInput.Close();
            }
            catch { /* best-effort stdin write */ }
        }, CancellationToken.None);

        var idleTimeout = Math.Max(60, _cli.TimeoutSeconds);
        var idleSw = Stopwatch.StartNew();
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            idleSw.Restart();
            if (idleSw.Elapsed.TotalSeconds > idleTimeout)
            {
                proc.Kill(entireProcessTree: true);
                throw new OperationCanceledException("Playwright agent idle timeout exceeded.");
            }
            ParseAndReport(line, progress);
        }

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            _log.LogWarning("Playwright agent exited {Code}: {Err}", proc.ExitCode, err);
        }
    }

    private static void ParseAndReport(string jsonLine, IProgress<string>? progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(jsonLine)) return;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() is "assistant")
            {
                if (root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() is "text" &&
                            part.TryGetProperty("text", out var txt))
                            progress.Report(txt.GetString() ?? "");
                    }
                }
            }
        }
        catch { /* non-JSON lines or unknown shapes — ignore */ }
    }

    private static string BuildGeneratePrompt(
        string title, string stepsJson, string bucket, string specPath, string appUrl, string? moduleRoute)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate a Playwright TypeScript test spec for the following manual test case.");
        sb.AppendLine($"Write the spec to: {specPath}");
        sb.AppendLine($"Bucket: {bucket}");
        sb.AppendLine($"App URL: {(string.IsNullOrEmpty(appUrl) ? "<configure AUTOMATION_APP_URL>" : appUrl)}");
        if (!string.IsNullOrWhiteSpace(moduleRoute))
            sb.AppendLine($"Module route to explore first: {moduleRoute}");
        sb.AppendLine();
        sb.AppendLine($"Test case title: {title}");
        sb.AppendLine();
        sb.AppendLine("Steps (action → expected):");
        try
        {
            using var doc = JsonDocument.Parse(stepsJson);
            var i = 1;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var action   = s.TryGetProperty("action",   out var a) ? a.GetString() : "";
                var expected = s.TryGetProperty("expected", out var e) ? e.GetString() : "";
                sb.AppendLine($"  {i++}. Action: {action} | Expected: {expected}");
            }
        }
        catch { sb.AppendLine(stepsJson); }
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Use @playwright/test.");
        sb.AppendLine("- Use the storageState from fixtures/storageState.json for authentication (relative to playwright.config.ts).");
        sb.AppendLine("- Use the Playwright MCP planner to navigate the module route and inspect live controls before choosing locators.");
        sb.AppendLine("- Include allure-playwright labels (@allure.label.story, @allure.label.tag).");
        sb.AppendLine($"- Tag the test: @ui @{bucket}.");
        sb.AppendLine("- The spec file must compile with TypeScript strict mode.");
        sb.AppendLine("- Write ONLY the spec file — no explanation.");
        return sb.ToString();
    }

    private static string BuildHealPrompt(string specFilePath, string failureMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The following Playwright spec has a test failure. Read the file, diagnose the issue, and rewrite it in place to fix the failure.");
        sb.AppendLine($"Spec file: {specFilePath}");
        sb.AppendLine();
        sb.AppendLine("Failure message:");
        sb.AppendLine(failureMessage);
        sb.AppendLine();
        sb.AppendLine("Fix the spec by:");
        sb.AppendLine("- Correcting any selector mismatches using the Playwright inspector or page structure.");
        sb.AppendLine("- Adjusting any timing issues (add explicit waits instead of arbitrary sleeps).");
        sb.AppendLine("- Preserving the original test intent and @allure labels.");
        sb.AppendLine("- Overwriting the same file path with the fixed spec.");
        return sb.ToString();
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
        .Trim('-');
}
