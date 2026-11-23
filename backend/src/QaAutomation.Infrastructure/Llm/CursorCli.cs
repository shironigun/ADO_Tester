using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Locate + probe the Cursor <c>agent</c> / <c>cursor-agent</c> CLI
/// (typically under <c>%LOCALAPPDATA%\cursor-agent</c>).
/// </summary>
public static class CursorCli
{
    private static readonly string[] AutoNames = ["agent", "cursor-agent"];
    private static readonly ConcurrentDictionary<string, string?> ResolveCache = new(StringComparer.Ordinal);

    public static string? ResolvePath(string? configuredPath)
    {
        var key = configuredPath ?? "";
        if (ResolveCache.TryGetValue(key, out var cached))
            return cached;
        var result = DoResolve(key);
        if (result is not null)
            ResolveCache[key] = result;
        return result;
    }

    private static string? DoResolve(string configuredPath)
    {
        var configuredIsBlank = string.IsNullOrWhiteSpace(configuredPath);
        var configured = configuredIsBlank ? "" : configuredPath.Trim();

        if (!configuredIsBlank && Path.IsPathRooted(configured) && File.Exists(configured))
            return configured;

        if (!configuredIsBlank)
        {
            var w = ClaudeCli.Which(configured);
            if (w is not null) return w;
        }

        foreach (var n in AutoNames)
        {
            var w = ClaudeCli.Which(n);
            if (w is not null) return w;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new[]
        {
            Path.Combine(local, "cursor-agent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "bin"),
        };
        var exts = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".ps1", "" }
            : new[] { "" };
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in AutoNames)
            foreach (var ext in exts)
            {
                string full;
                try { full = Path.Combine(dir, name + ext); }
                catch { continue; }
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>Returns (available, version-or-null).</summary>
    public static (bool Available, string? Version) Probe(string? configuredPath)
    {
        var cli = ResolvePath(configuredPath);
        if (cli is null) return (false, null);
        try
        {
            var psi = new ProcessStartInfo(cli)
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, null);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            var text = (stdout + " " + stderr).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return (p.ExitCode == 0, null);
            var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? text;
            return (true, first.Length > 80 ? first[..80] : first);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Runs <c>agent --list-models</c> and returns raw stdout (empty on failure).</summary>
    public static string ListModelsRaw(string? configuredPath)
    {
        var cli = ResolvePath(configuredPath);
        if (cli is null) return "";
        try
        {
            var psi = new ProcessStartInfo(cli)
            {
                ArgumentList = { "--list-models" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return stdout ?? "";
        }
        catch
        {
            return "";
        }
    }
}
