using System.Diagnostics;

using System.Text;

using Microsoft.Extensions.Logging;

using QaAutomation.Core;

using QaAutomation.Core.Abstractions;

using QaAutomation.Core.Configuration;



namespace QaAutomation.Infrastructure.Llm;



/// <summary>

/// Shells out to the local GitHub <c>copilot</c> CLI in print mode (Copilot subscription, no API key).

///

/// IMPORTANT: Copilot CLI does NOT support reading the prompt from stdin with <c>-p -</c> â€” unlike

/// Claude CLI which streams JSON on stdout, Copilot CLI treats <c>-</c> as a literal 1-char prompt.

/// Instead the prompt is written to a temp file in <c>llm-scratch/</c> and passed as <c>-p @filepath</c>,

/// which the CLI natively supports (same <c>@path</c> convention used by <c>--additional-mcp-config</c>).

/// The temp file is deleted in a finally block regardless of success or failure.

///

/// Exit-code contract: Copilot CLI exits 1 on a successful non-interactive run (verified empirically).

/// Both 0 and 1 are treated as success; anything higher is an error.

///

/// Non-streaming: plain text on stdout, collected to completion. <see cref="StreamProgress"/> is

/// ticked per line so the UI activity indicator stays alive.

/// </summary>

public sealed class CopilotCliLlmClient : ILlmClient

{

    private const int AbsoluteCapSeconds = 1800;



    public string ProviderName => "CopilotCli";



    private readonly CopilotCliOptions _options;

    private readonly ILogger<CopilotCliLlmClient> _log;

    private readonly string _scratchDir;



    public CopilotCliLlmClient(CopilotCliOptions options, StorageOptions storage, ILogger<CopilotCliLlmClient> log)

    {

        _options = options;

        _log = log;

        _scratchDir = Path.GetFullPath(Path.Combine(storage.DataDirectory, "llm-scratch"));

        Directory.CreateDirectory(_scratchDir);

    }



    public async Task<string> CompleteAsync(

        string systemPrompt, string userPrompt, string? model = null,

        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)

    {

        var cli = CopilotCli.ResolvePath(_options.Path);

        if (cli is null)

            throw new LlmException(

                $"GitHub Copilot CLI not found (configured path: '{(string.IsNullOrEmpty(_options.Path) ? "<auto-detect>" : _options.Path)}'). " +

                "Install with `npm install -g @github/copilot` (or set copilot_cli.path in Settings to an absolute path), " +

                "then run `copilot` once to sign in.");



        var combined =

            $"<system_instructions>\n{systemPrompt.Trim()}\n</system_instructions>\n\n{userPrompt}";



        // Write prompt to a temp file. The CLI's @filepath notation reads the file contents as the

        // prompt â€” this is the only reliable way to pass large prompts (stdin with -p - is not

        // supported; the CLI would treat "-" as the literal 1-char prompt).

        var promptFile = Path.Combine(_scratchDir, $"copilot_prompt_{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(promptFile, combined, Encoding.UTF8, cancellationToken);



        var args = new List<string>
        {
            "-p", $"@{promptFile}",
            // --available-tools (no value) = zero tools available to the model → pure LLM completion.
            // Without this the CLI enters agent mode and tries to search the filesystem to find source
            // code it thinks is missing from the prompt, causing "Permission denied" errors.
            // --no-ask-user disables the ask_user tool so the process never stalls waiting for input.
            "--available-tools", "--no-ask-user", "--no-color",
        };

        var chosenModel = (string.IsNullOrEmpty(model) ? _options.Model : model).Trim();

        if (chosenModel.Length > 0)

        {

            args.Add("--model");

            args.Add(chosenModel);

        }

        var chosenContext = _options.Context.Trim();

        if (chosenContext.Length > 0)

        {

            args.Add("--context");

            args.Add(chosenContext);

        }

        var chosenEffort = _options.Effort.Trim();

        if (chosenEffort.Length > 0)

        {

            args.Add("--effort");

            args.Add(chosenEffort);

        }



        var idleTimeout = Math.Max(30, _options.TimeoutSeconds);

        _log.LogInformation("Running copilot CLI {Cli} (model={Model}, idle={Idle}s, prompt={Chars} chars)",

            cli, chosenModel.Length > 0 ? chosenModel : "default", idleTimeout, combined.Length);



        try

        {

            return await RunAsync(cli, args, idleTimeout, progress, emit, cancellationToken);

        }

        finally

        {

            try { File.Delete(promptFile); } catch { /* best effort */ }

        }

    }



    private async Task<string> RunAsync(

        string cli, List<string> args, int idleTimeout,

        StreamProgress? progress, Action<LlmEmit>? emit, CancellationToken ct)

    {

        var psi = new ProcessStartInfo(cli)

        {

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            StandardOutputEncoding = Encoding.UTF8,

            StandardErrorEncoding = Encoding.UTF8,

            // Neutral cwd so the CLI cannot auto-discover this app's own repo config/memory.

            WorkingDirectory = _scratchDir,

        };

        foreach (var a in args)

            psi.ArgumentList.Add(a);



        // Strip only the Copilot API key (not used); keep COPILOT_GITHUB_TOKEN, GH_TOKEN, and
        // GITHUB_TOKEN so the CLI can authenticate via the GitHub token set in the environment.
        psi.Environment.Remove("COPILOT_API_KEY");



        using var proc = new Process { StartInfo = psi };

        try

        {

            proc.Start();

        }

        catch (Exception e)

        {

            throw new LlmException(

                $"Could not launch Copilot CLI '{cli}': {e.Message}. " +

                "Confirm the GitHub Copilot CLI is installed and `copilot --version` works in a terminal.", e);

        }



        var outBuf = new StringBuilder();

        long chars = 0;

        var overall = Stopwatch.StartNew();

        var stdout = proc.StandardOutput;



        while (true)

        {

            if (overall.Elapsed.TotalSeconds > AbsoluteCapSeconds)

            {

                Kill(proc);

                throw new LlmException($"Copilot CLI exceeded the absolute cap of {AbsoluteCapSeconds}s.");

            }



            var readTask = stdout.ReadLineAsync();

            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(idleTimeout), ct));

            if (completed != readTask)

            {

                Kill(proc);

                ct.ThrowIfCancellationRequested();

                throw new LlmException(

                    $"Copilot CLI produced no output for {idleTimeout}s (stalled). " +

                    "Increase copilot_cli.timeout_seconds in Settings if the model needs longer.");

            }



            var line = await readTask;

            if (line is null)

                break; // EOF

            if (outBuf.Length > 0)

                outBuf.Append('\n');

            outBuf.Append(line);

            chars += line.Length;

            progress?.Update(chars);

            if (emit is not null && line.Length > 0)

            {

                try { emit(new LlmEmit("token", line)); }

                catch { /* never let the console break the run */ }

            }

        }



        await proc.WaitForExitAsync(ct);

        var stderr = (await proc.StandardError.ReadToEndAsync(ct)).Trim();

        var rc = proc.ExitCode;



        // Copilot CLI exits 1 on a normal successful non-interactive run (verified empirically).

        // Treat both 0 and 1 as success; anything higher is a real error.

        if (rc > 1)

        {

            var reason = stderr.Length > 0 ? Truncate(stderr, 500)

                : outBuf.Length > 0 ? Truncate(outBuf.ToString(), 500)

                : "(no output)";

            throw new LlmException($"Copilot CLI exited {rc}: {reason}");

        }

        var final = outBuf.ToString().Trim();

        if (final.Length == 0)

            throw new LlmException("Copilot CLI produced no output." + (stderr.Length > 0 ? $" stderr: {Truncate(stderr, 300)}" : ""));

        return final;

    }



    private static void Kill(Process p)

    {

        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }

    }



    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

}

