using System.Diagnostics;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Shared `claude` CLI location + probe (feeds both the provider and the diagnostics endpoint, so the
/// UI status and the provider agree). Faithful to the Python <c>resolve_claude_cli_path</c> /
/// <c>probe_claude_cli</c> (spec §5.6): identical candidate search order.
/// </summary>
public static class ClaudeCli
{
    /// <summary>Locate a runnable `claude` executable, or null. Search order: configured path →
    /// PATH lookup → per-user install locations.</summary>
    public static string? ResolvePath(string? configuredPath)
    {
        // null/empty → "claude" (PATH default); whitespace-only → "" (disables PATH discovery), matching
        // Python's `(claude_cli_path or "claude").strip()` exactly.
        var configured = string.IsNullOrEmpty(configuredPath) ? "claude" : configuredPath.Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new[]
        {
            configured,
            Which(configured) ?? "",
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, "AppData", "Roaming", "npm", "claude.cmd"),
            Path.Combine(home, "AppData", "Roaming", "npm", "claude"),
            Path.Combine(home, "AppData", "Local", "Programs", "Claude", "claude.exe"),
            "/usr/local/bin/claude",
            "/opt/homebrew/bin/claude",
        };

        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c) || !seen.Add(c))
                continue;
            if (Path.IsPathRooted(c))
            {
                if (File.Exists(c))
                    return c;
            }
            else
            {
                var w = Which(c);
                if (w is not null)
                    return w;
            }
        }
        return null;
    }

    /// <summary>Run `claude --version`. Returns (available, versionString). Synchronous (10s cap).</summary>
    public static (bool Available, string? Version) Probe(string? configuredPath)
    {
        var cli = ResolvePath(configuredPath);
        if (cli is null)
            return (false, null);
        try
        {
            var psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null)
                return (false, null);
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, null);
            }
            if (p.ExitCode == 0)
            {
                var outp = p.StandardOutput.ReadToEnd().Trim();
                return (true, string.IsNullOrEmpty(outp) ? "ok" : outp);
            }
        }
        catch
        {
            return (false, null);
        }
        return (false, null);
    }

    /// <summary>PATH lookup honoring PATHEXT on Windows (mirrors Python's shutil.which).
    /// Internal (not just private) so other Infrastructure spawners — e.g. <c>PlaywrightRunner</c>
    /// resolving npm/npx — can reuse the same PATHEXT-aware resolution instead of relying on
    /// bare <c>ProcessStartInfo</c>, which can't find .cmd shims on Windows.</summary>
    internal static string? Which(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // PATHEXT extensions must be tried BEFORE the bare name on Windows: node.js's install dir
        // ships both a bare POSIX shell-script "npm" (not a valid Win32 executable) and the real
        // "npm.cmd" shim side by side, and a bare-name-first search picks the unusable decoy.
        List<string> exts;
        if (OperatingSystem.IsWindows())
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            exts = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            exts.Add(""); // bare name only as a last resort
        }
        else
        {
            exts = [""];
        }

        foreach (var dir in dirs)
        {
            foreach (var ext in exts)
            {
                string full;
                try { full = Path.Combine(dir, name + ext); }
                catch { continue; } // invalid chars in a PATH entry
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }
}
