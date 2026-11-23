using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Headless Cursor <c>agent -p</c> client. Uses a scratch <c>--workspace</c> so prompts do not edit
/// this app's checkout. Auth via <c>agent login</c> or <c>CURSOR_API_KEY</c>.
/// </summary>
public sealed class CursorCliLlmClient : ILlmClient
{
    private const int AbsoluteCapSeconds = 1800;

    public string ProviderName => "CursorCli";

    private readonly CursorCliOptions _options;
    private readonly ILogger<CursorCliLlmClient> _log;
    private readonly string _scratchDir;

    public CursorCliLlmClient(CursorCliOptions options, StorageOptions storage, ILogger<CursorCliLlmClient> log)
    {
        _options = options;
        _log = log;
        _scratchDir = Path.GetFullPath(Path.Combine(storage.DataDirectory, "llm-scratch", "cursor"));
        Directory.CreateDirectory(_scratchDir);
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, string? model = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var cli = CursorCli.ResolvePath(_options.Path);
        if (cli is null)
            throw new LlmException(
                $"Cursor agent CLI not found (configured path: '{(string.IsNullOrEmpty(_options.Path) ? "<auto-detect>" : _options.Path)}'). " +
                "Install Cursor Agent (`agent`) and run `agent login`, or set cursor_cli.path / CURSOR_CLI_PATH.");

        var combined =
            $"<system_instructions>\n{systemPrompt.Trim()}\n</system_instructions>\n\n{userPrompt}";

        var chosenModel = CliModelCatalogs.ComposeCursorModel(
            string.IsNullOrEmpty(model) ? _options.Model : model,
            _options.Effort,
            _options.Context);

        var args = new List<string>
        {
            "-p",
            "--output-format", "text",
            "--force",
            "--trust",
            "--workspace", _scratchDir,
            "--model", chosenModel,
            combined,
        };

        var idleTimeout = Math.Max(30, _options.TimeoutSeconds);
        _log.LogInformation("Running cursor agent CLI {Cli} (model={Model}, idle={Idle}s, prompt={Chars} chars)",
            cli, chosenModel, idleTimeout, combined.Length);

        return await RunAsync(cli, args, idleTimeout, progress, cancellationToken);
    }

    private async Task<string> RunAsync(
        string cli, List<string> args, int idleTimeout,
        StreamProgress? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _scratchDir,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception e)
        {
            throw new LlmException($"Could not launch Cursor agent CLI '{cli}': {e.Message}", e);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lastOutput = DateTime.UtcNow;
        var started = DateTime.UtcNow;

        var readOut = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                lastOutput = DateTime.UtcNow;
                stdout.AppendLine(line);
                progress?.Update(stdout.Length);
            }
        }, ct);
        var readErr = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(ct) is { } line)
            {
                lastOutput = DateTime.UtcNow;
                stderr.AppendLine(line);
            }
        }, ct);

        while (!proc.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            if ((DateTime.UtcNow - lastOutput).TotalSeconds > idleTimeout)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new LlmException($"Cursor agent CLI idle timeout after {idleTimeout}s with no output.");
            }
            if ((DateTime.UtcNow - started).TotalSeconds > AbsoluteCapSeconds)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new LlmException($"Cursor agent CLI absolute timeout after {AbsoluteCapSeconds}s.");
            }
            await Task.Delay(200, ct);
        }

        await Task.WhenAll(readOut, readErr);
        var text = stdout.ToString().Trim();
        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(text))
        {
            var err = stderr.ToString().Trim();
            throw new LlmException(
                $"Cursor agent CLI exited {proc.ExitCode}" + (err.Length > 0 ? $": {err}" : "."));
        }
        return text;
    }
}
