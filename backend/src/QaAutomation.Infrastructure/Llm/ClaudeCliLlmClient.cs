using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Shells out to the local `claude` CLI in print mode (Pro/Max subscription, no API key). Streams
/// stream-json so a long-but-active run is never killed: the timeout is IDLE-based (no output for N
/// seconds) with an absolute 1800s safety cap (spec §5.6). Prompt is fed via STDIN (avoids the Windows
/// ~32KB argv limit). Token/thinking deltas are coalesced before emit (channel switch / ≥256 chars /
/// ≥80ms). Faithful to the Python ClaudeCliLlmClient — .NET async process I/O replaces the Python
/// thread+queue workaround.
///
/// The child process's WorkingDirectory is pinned to a neutral scratch folder (never the app's own
/// repo checkout), because the CLI auto-discovers `.claude/`, `CLAUDE.md`, and project memory from its
/// cwd — inherited cwd would otherwise leak THIS repo's own dev config/memory into every agent prompt
/// (QAL-069). `--setting-sources ""` does NOT suppress this (verified empirically); only cwd redirect does.
/// </summary>
public sealed class ClaudeCliLlmClient : ILlmClient
{
    private const int AbsoluteCapSeconds = 1800;

    public string ProviderName => "ClaudeCli";

    private readonly ClaudeCliOptions _options;
    private readonly ILogger<ClaudeCliLlmClient> _log;
    private readonly string _scratchDir;

    public ClaudeCliLlmClient(ClaudeCliOptions options, StorageOptions storage, ILogger<ClaudeCliLlmClient> log)
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
        var cli = ClaudeCli.ResolvePath(_options.Path);
        if (cli is null)
            throw new LlmException(
                $"Claude CLI not found (configured path: '{_options.Path}'). Install Claude Code and log in, " +
                "or set CLAUDE_CLI_PATH in the .env to an absolute path to claude.exe.");

        var combined =
            $"<system_instructions>\n{systemPrompt.Trim()}\n</system_instructions>\n\n{userPrompt}";

        var args = new List<string> { "-p", "--output-format", "stream-json", "--include-partial-messages", "--verbose" };
        var chosenModel = (string.IsNullOrEmpty(model) ? _options.Model : model).Trim();
        if (chosenModel.Length > 0)
        {
            args.Add("--model");
            args.Add(chosenModel);
        }

        var idleTimeout = Math.Max(30, _options.TimeoutSeconds);
        _log.LogInformation("Streaming claude CLI {Cli} (model={Model}, idle={Idle}s, prompt={Chars} chars)",
            cli, chosenModel.Length > 0 ? chosenModel : "default", idleTimeout, combined.Length);

        return await RunStreamAsync(cli, args, idleTimeout, combined, progress, emit, cancellationToken);
    }

    private async Task<string> RunStreamAsync(
        string cli, List<string> args, int idleTimeout, string stdinText,
        StreamProgress? progress, Action<LlmEmit>? emit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Neutral cwd so the CLI can't auto-discover this app's OWN .claude/CLAUDE.md/memory (QAL-069).
            WorkingDirectory = _scratchDir,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // The ClaudeCli provider authenticates via the local subscription login (Pro/Max), NEVER an API
        // key. Any ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN in the environment — e.g. set in .env for the
        // Anthropic provider, even the unfilled "your-anthropic-key-here" placeholder — would push the CLI
        // into external-API-key mode and fail with "Invalid API key". Strip them from the child so it
        // always uses the subscription login. (A top-level CLI honors the key; only a nested Claude Code
        // child session ignores it, which is why this never reproduces from inside Claude Code.)
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception e)
        {
            throw new LlmException(
                $"Could not launch Claude CLI '{cli}': {e.Message}. " +
                "Confirm Claude Code is installed and `claude --version` works in a terminal.", e);
        }

        // Feed the prompt on a background task so a large stdin never deadlocks against stdout.
        _ = Task.Run(async () =>
        {
            try
            {
                await proc.StandardInput.WriteAsync(stdinText.AsMemory(), ct);
                proc.StandardInput.Close();
            }
            catch { /* writer best-effort */ }
        }, CancellationToken.None);

        string? finalText = null;
        string? resultSubtype = null;
        bool resultIsError = false;
        long chars = 0;
        var overall = Stopwatch.StartNew();

        // Coalesce token/thinking deltas before emitting (≤ ~12 events/s).
        string? bufChannel = null;
        var bufText = new StringBuilder();
        var sinceFlush = Stopwatch.StartNew();

        void Flush()
        {
            if (emit is not null && bufChannel is not null && bufText.Length > 0)
            {
                try { emit(new LlmEmit(bufChannel, bufText.ToString())); }
                catch { /* never let the console break the run */ }
            }
            bufText.Clear();
            bufChannel = null;
            sinceFlush.Restart();
        }

        var stdout = proc.StandardOutput;
        while (true)
        {
            if (overall.Elapsed.TotalSeconds > AbsoluteCapSeconds)
            {
                Kill(proc);
                throw new LlmException($"Claude CLI exceeded the absolute cap of {AbsoluteCapSeconds}s.");
            }

            var readTask = stdout.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(idleTimeout), ct));
            if (completed != readTask)
            {
                Kill(proc);
                ct.ThrowIfCancellationRequested();
                throw new LlmException(
                    $"Claude CLI produced no output for {idleTimeout}s (stalled). " +
                    "Increase claude_cli.timeout_seconds in Settings if the model needs longer.");
            }

            var line = await readTask;
            if (line is null)
                break; // EOF
            line = line.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
                if (type == "result")
                {
                    if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                        finalText = r.GetString();
                    // `claude -p` reports failures (usage limits, auth, model errors) in the result event
                    // itself — capture is_error/subtype so a non-zero exit isn't an opaque "(no stderr)".
                    resultSubtype = StrOf(root, "subtype");
                    resultIsError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    continue;
                }

                var cls = ClassifyDelta(root);
                if (cls is null)
                {
                    progress?.Tick();
                    continue;
                }
                var (channel, text) = cls.Value;
                if (channel == "token")
                {
                    chars += text.Length;
                    progress?.Update(chars);
                }
                else
                {
                    progress?.Tick();
                }

                if (emit is not null && text.Length > 0)
                {
                    if (bufChannel is not null && bufChannel != channel)
                        Flush();
                    bufChannel = channel;
                    bufText.Append(text);
                    if (bufText.Length >= 256 || sinceFlush.Elapsed.TotalMilliseconds >= 80)
                        Flush();
                }
            }
            catch (JsonException)
            {
                continue; // ignore non-JSON lines
            }
        }

        Flush();
        await proc.WaitForExitAsync(ct);
        var stderr = (await proc.StandardError.ReadToEndAsync(ct)).Trim();
        var rc = proc.ExitCode;

        if (rc != 0 || resultIsError)
        {
            // Prefer stderr; otherwise surface the CLI's own result-event error text/subtype (where
            // `claude -p` actually puts usage-limit / auth / model failures); only then fall back to "(no stderr)".
            var subtypeTag = resultSubtype is { Length: > 0 } ? $"[{resultSubtype}] " : "";
            var reason =
                stderr.Length > 0 ? Truncate(stderr, 500)
                : !string.IsNullOrEmpty(finalText) ? $"{subtypeTag}{Truncate(finalText!, 500)}"
                : subtypeTag.Length > 0 ? subtypeTag.TrimEnd()
                : "(no stderr)";
            throw new LlmException($"Claude CLI exited {rc}: {reason}");
        }
        if (string.IsNullOrEmpty(finalText))
            throw new LlmException("Claude CLI stream ended without a result." + (stderr.Length > 0 ? $" stderr: {Truncate(stderr, 300)}" : ""));
        return finalText;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>Classify a stream-json partial event into ("token"|"thinking", text), or null.
    /// Handles top-level deltas and <c>content_block_delta</c> / <c>stream_event</c> wrappers — faithful
    /// to the Python <c>_classify_delta</c>.</summary>
    internal static (string Kind, string Text)? ClassifyDelta(JsonElement obj)
    {
        var candidates = new List<JsonElement>();
        if (obj.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object)
            candidates.Add(d);
        if (obj.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object
            && ev.TryGetProperty("delta", out var ed) && ed.ValueKind == JsonValueKind.Object)
            candidates.Add(ed);
        var topType = StrOf(obj, "type");
        if (topType is "text_delta" or "thinking_delta")
            candidates.Add(obj);

        foreach (var c in candidates)
        {
            var ctype = StrOf(c, "type");
            if (ctype == "thinking_delta")
            {
                // Mirror Python's `c.get("thinking") or c.get("text")`: an empty "thinking" falls
                // through to "text" (`??` alone would keep the empty string).
                var thinking = StrOf(c, "thinking");
                var t = string.IsNullOrEmpty(thinking) ? StrOf(c, "text") : thinking;
                if (t is not null)
                    return ("thinking", t);
            }
            else if (ctype == "text_delta")
            {
                var t = StrOf(c, "text");
                if (t is not null)
                    return ("token", t);
            }
            else if (ctype is null)
            {
                var tx = StrOf(c, "text");
                if (tx is not null)
                    return ("token", tx);
                var th = StrOf(c, "thinking");
                if (th is not null)
                    return ("thinking", th);
            }
        }
        return null;
    }

    private static string? StrOf(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
