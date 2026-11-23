using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Drives the local <c>claude</c> CLI's subscription auth from the web UI (no shell/`docker exec`
/// needed). <c>claude auth login --claudeai</c> prints an OAuth URL and then blocks on STDIN for the
/// code the user pastes back from the Anthropic callback page — so login is a two-step flow: start
/// (spawn the process, capture the URL, keep the process alive) then submit-code (write it to STDIN,
/// wait for the exchange, verify via <c>claude auth status --json</c>). Credentials land in the CLI's
/// $HOME (mounted volume in Docker), the same one the LLM provider reads. Singleton — it holds the one
/// pending login process between the two HTTP calls; ANTHROPIC_API_KEY is stripped so the subscription
/// login is used (never API-key mode), mirroring <see cref="ClaudeCliLlmClient"/>.
/// </summary>
public sealed class ClaudeAuthService : IClaudeAuthService, IDisposable
{
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    private readonly IOptionsMonitor<ClaudeCliOptions> _options;
    private readonly ILogger<ClaudeAuthService> _log;
    private readonly string _scratchDir;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _lock = new();
    private LoginSession? _active;

    public ClaudeAuthService(IOptionsMonitor<ClaudeCliOptions> options, ILogger<ClaudeAuthService> log)
    {
        _options = options;
        _log = log;
        // Neutral cwd so the CLI can't auto-discover this app's own .claude/CLAUDE.md (QAL-069 parity).
        _scratchDir = Path.Combine(Path.GetTempPath(), "ado-tester-claude-auth");
        Directory.CreateDirectory(_scratchDir);
    }

    public async Task<ClaudeAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var cli = ClaudeCli.ResolvePath(_options.CurrentValue.Path);
        if (cli is null)
            return new ClaudeAuthStatus { Available = false, LoggedIn = false, Error = "Claude CLI not found." };

        var (_, stdout, stderr) = await RunOnceAsync(cli, ["auth", "status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(FirstJson(stdout));
            var root = doc.RootElement;
            return new ClaudeAuthStatus
            {
                Available = true,
                LoggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True,
                AuthMethod = GetStr(root, "authMethod"),
                Email = GetStr(root, "email"),
                OrgName = GetStr(root, "orgName"),
                SubscriptionType = GetStr(root, "subscriptionType"),
            };
        }
        catch
        {
            // No parseable JSON → treat as signed out; surface stderr if the CLI complained.
            return new ClaudeAuthStatus
            {
                Available = true,
                LoggedIn = false,
                Error = string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim(),
            };
        }
    }

    public async Task<ClaudeLoginStart> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        var cli = ClaudeCli.ResolvePath(_options.CurrentValue.Path)
            ?? throw new InvalidOperationException(
                "Claude CLI not found — cannot start sign-in. Confirm the backend image includes the CLI.");

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            KillActive();   // drop any stale/abandoned pending login

            var psi = new ProcessStartInfo(cli)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _scratchDir,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--claudeai");   // subscription login (not API-key/console)
            psi.Environment.Remove("ANTHROPIC_API_KEY");
            psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

            var proc = new Process { StartInfo = psi };
            var session = new LoginSession(Guid.NewGuid().ToString("N"), proc);
            proc.Start();
            lock (_lock) _active = session;

            // Drain both streams; the OAuth URL may print to either. Fail the URL wait if the process dies.
            _ = Task.Run(() => PumpAsync(session, proc.StandardOutput));
            _ = Task.Run(() => PumpAsync(session, proc.StandardError));
            _ = proc.WaitForExitAsync().ContinueWith(_ => session.UrlReady.TrySetResult(null), TaskScheduler.Default);

            var url = await WaitWithTimeoutAsync(session.UrlReady.Task, TimeSpan.FromSeconds(25), cancellationToken);
            if (string.IsNullOrEmpty(url))
            {
                var tail = Tail(session);
                KillActive();
                throw new InvalidOperationException(
                    "Claude sign-in did not return an authorization URL. " +
                    (string.IsNullOrWhiteSpace(tail) ? "" : $"CLI said: {tail}"));
            }

            _log.LogInformation("Claude auth login started (loginId={LoginId}).", session.Id);
            return new ClaudeLoginStart { LoginId = session.Id, Url = url };
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<ClaudeLoginResult> SubmitCodeAsync(string loginId, string code, CancellationToken cancellationToken = default)
    {
        LoginSession? session;
        lock (_lock) session = _active;

        if (session is null || session.Id != loginId)
            return new ClaudeLoginResult { Success = false, Error = "No pending sign-in — start again." };

        if (string.IsNullOrWhiteSpace(code))
            return new ClaudeLoginResult { Success = false, Error = "Authorization code is required." };

        try
        {
            await session.Process.StandardInput.WriteLineAsync(code.Trim());
            await session.Process.StandardInput.FlushAsync(cancellationToken);
            session.Process.StandardInput.Close();
        }
        catch (Exception e)
        {
            KillActive();
            return new ClaudeLoginResult { Success = false, Error = $"Could not submit the code: {e.Message}" };
        }

        var exited = await WaitForExitAsync(session.Process, TimeSpan.FromSeconds(40));
        var tail = Tail(session);
        KillActive();

        var status = await GetStatusAsync(cancellationToken);
        if (status.LoggedIn)
            return new ClaudeLoginResult { Success = true, Status = status };

        var reason = !exited
            ? "Sign-in timed out waiting for Claude."
            : $"Sign-in failed. {tail}".Trim();
        return new ClaudeLoginResult { Success = false, Error = reason, Status = status };
    }

    public async Task<ClaudeAuthStatus> LogoutAsync(CancellationToken cancellationToken = default)
    {
        KillActive();
        var cli = ClaudeCli.ResolvePath(_options.CurrentValue.Path);
        if (cli is not null)
            await RunOnceAsync(cli, ["auth", "logout"], TimeSpan.FromSeconds(20), cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task PumpAsync(LoginSession session, StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (session.Output) session.Output.AppendLine(line);
                if (session.UrlReady.Task.IsCompleted)
                    continue;
                var m = UrlRegex.Match(line);
                if (m.Success && (m.Value.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                                  || m.Value.Contains("authorize", StringComparison.OrdinalIgnoreCase)))
                    session.UrlReady.TrySetResult(m.Value);
            }
        }
        catch { /* stream closed on process exit */ }
    }

    private async Task<(int Exit, string Out, string Err)> RunOnceAsync(
        string cli, string[] args, TimeSpan timeout, CancellationToken ct)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception e) { return (-1, "", e.Message); }

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ } }

        var o = await outTask.ConfigureAwait(false);
        var e2 = await errTask.ConfigureAwait(false);
        return (proc.HasExited ? proc.ExitCode : -1, o, e2);
    }

    private static async Task<string?> WaitWithTimeoutAsync(Task<string?> task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(task, Task.Delay(timeout, cts.Token));
        if (completed == task)
        {
            cts.Cancel();
            return await task;
        }
        return null;
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void KillActive()
    {
        LoginSession? s;
        lock (_lock) { s = _active; _active = null; }
        if (s is null) return;
        try { if (!s.Process.HasExited) s.Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { s.Process.Dispose(); } catch { /* ignore */ }
    }

    private static string FirstJson(string s)
    {
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');
        return i >= 0 && j > i ? s.Substring(i, j - i + 1) : s;
    }

    private static string? GetStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Tail(LoginSession s)
    {
        string text;
        lock (s.Output) text = s.Output.ToString();
        text = text.Trim();
        return text.Length <= 400 ? text : text[^400..];
    }

    public void Dispose() => KillActive();

    private sealed class LoginSession(string id, Process process)
    {
        public string Id { get; } = id;
        public Process Process { get; } = process;
        public StringBuilder Output { get; } = new();
        public TaskCompletionSource<string?> UrlReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
