using System.Collections.Concurrent;
using System.Diagnostics;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Shared GitHub <c>copilot</c> CLI location + probe. Mirrors <see cref="ClaudeCli"/> but with a
/// broader auto-discovery net — the GitHub Copilot CLI ships under a few possible binary names
/// (<c>copilot</c>, <c>github-copilot-cli</c>, <c>gh-copilot</c>) and lands in whichever npm/pnpm/Volta
/// global-bin the user's Node installer picked. Result: the CLI-path field can stay blank and it just
/// works.
/// </summary>
public static class CopilotCli
{
    // The binary names auto-tried when the user leaves the path blank. If the user configures an
    // explicit name/path, we honor THAT verbatim first (never rename it out from under them).
    private static readonly string[] AutoNames = ["copilot", "github-copilot-cli", "gh-copilot"];

    // Cache the npm-prefix lookup — shelling out to npm is expensive and rarely changes at runtime.
    private static string? _cachedNpmBin;
    private static readonly ConcurrentDictionary<string, string?> ResolveCache = new(StringComparer.Ordinal);

    /// <summary>Locate a runnable GitHub Copilot CLI, or null. Order: configured value (exact) →
    /// PATH lookup on configured value → PATH lookup on the auto-names → common per-user install
    /// directories (npm/pnpm/Volta/nvm/homebrew) × auto-names × PATHEXT variants.</summary>
    public static string? ResolvePath(string? configuredPath)
    {
        var key = configuredPath ?? "";
        return ResolveCache.GetOrAdd(key, DoResolve);
    }

    private static string? DoResolve(string configuredPath)
    {
        // Blank = full auto-detect. Configured = try that exact value first, THEN fall back to
        // auto-detect (so a stale explicit value doesn't lock the user out).
        var configuredIsBlank = string.IsNullOrWhiteSpace(configuredPath);
        var configured = configuredIsBlank ? "" : configuredPath.Trim();

        // 1) Absolute path exactly as configured wins.
        if (!configuredIsBlank && Path.IsPathRooted(configured) && File.Exists(configured))
            return configured;

        // 2) Configured bare name via PATH (PATHEXT-aware).
        if (!configuredIsBlank)
        {
            var w = ClaudeCli.Which(configured);
            if (w is not null) return w;
        }

        // 3) Auto-names via PATH.
        foreach (var n in AutoNames)
        {
            var w = ClaudeCli.Which(n);
            if (w is not null) return w;
        }

        // 4) Well-known per-user install dirs × auto-names × PATHEXT variants.
        var dirs = KnownGlobalBinDirs();
        var exts = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".ps1", "" }
            : new[] { "" };
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
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

    private static IEnumerable<string> KnownGlobalBinDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // VS Code / VS Code Insiders / VSCodium install Copilot Chat's `copilot.bat` shim under
        // globalStorage\github.copilot-chat\copilotCli. The extension prepends this dir to PATH for
        // its own shells, but the API's own process (started before the extension modified the parent
        // shell) may not see it — so we probe the location directly.
        var vscodeCliDirs = new[] { "Code", "Code - Insiders", "Code - OSS", "VSCodium", "VSCodium - Insiders" }
            .SelectMany(name => new[]
            {
                Path.Combine(appdata, name, "User", "globalStorage", "github.copilot-chat", "copilotCli"),
                Path.Combine(home, ".config", name, "User", "globalStorage", "github.copilot-chat", "copilotCli"),
                Path.Combine(home, "Library", "Application Support", name, "User", "globalStorage", "github.copilot-chat", "copilotCli"),
            });

        var list = new List<string?>
        {
            // Standalone GitHub Copilot CLI installer (Windows: %LOCALAPPDATA%\GitHubCopilotCLI).
            Path.Combine(localAppData, "GitHubCopilotCLI"),
            Path.Combine(progFiles, "GitHubCopilotCLI"),
            Path.Combine(home, ".githubcopilot", "bin"),
            // npm global (Windows default is %APPDATA%\npm; some setups use %LOCALAPPDATA%\npm).
            Path.Combine(appdata, "npm"),
            Path.Combine(localAppData, "npm"),
            // Custom global override (`npm config set prefix ~/.npm-global`) is common on macOS/Linux.
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".npm-global"),
            // pnpm.
            Path.Combine(localAppData, "pnpm"),
            Path.Combine(home, "Library", "pnpm"),
            Path.Combine(home, ".local", "share", "pnpm"),
            // Volta.
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(localAppData, "Volta", "bin"),
            // fnm / nvm-for-windows current shell shims.
            Path.Combine(localAppData, "fnm_multishells"),
            // GitHub CLI Copilot extension (installed under gh's data dir).
            Path.Combine(localAppData, "GitHub CLI"),
            Path.Combine(progFiles, "GitHub CLI"),
            // Generic per-user + system bins.
            Path.Combine(home, ".local", "bin"),
            "/usr/local/bin",
            "/opt/homebrew/bin",
            // npm's actual global bin — cheapest reliable fallback (one shell-out, cached).
            NpmGlobalBin(),
        };
        list.AddRange(vscodeCliDirs);
        return list.Where(d => !string.IsNullOrEmpty(d))!;
    }

    private static string? NpmGlobalBin()
    {
        if (_cachedNpmBin is not null)
            return _cachedNpmBin.Length == 0 ? null : _cachedNpmBin;

        try
        {
            var npm = ClaudeCli.Which("npm");
            if (npm is null) { _cachedNpmBin = ""; return null; }
            var psi = new ProcessStartInfo(npm)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("get");
            psi.ArgumentList.Add("prefix");
            using var p = Process.Start(psi);
            if (p is null) { _cachedNpmBin = ""; return null; }
            if (!p.WaitForExit(3_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _cachedNpmBin = "";
                return null;
            }
            if (p.ExitCode != 0) { _cachedNpmBin = ""; return null; }
            var prefix = p.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(prefix)) { _cachedNpmBin = ""; return null; }
            // Windows: <prefix> IS the bin dir. POSIX: <prefix>/bin.
            _cachedNpmBin = OperatingSystem.IsWindows() ? prefix : Path.Combine(prefix, "bin");
            return _cachedNpmBin;
        }
        catch
        {
            _cachedNpmBin = "";
            return null;
        }
    }

    /// <summary>Run <c>&lt;cli&gt; --version</c>. Returns (available, versionString). Synchronous (10s cap).</summary>
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
}
