using System.Net;
using System.Text.Json;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.Llm;

namespace QaAutomation.Tests;

/// <summary>Frozen-contract tests for the LLM layer (spec §5.6): the Anthropic raw request shape +
/// error format, StreamProgress, ClaudeCli stream-json classification + path resolution.</summary>
public sealed class LlmTests
{
    [Fact]
    public async Task Anthropic_builds_messages_request_and_joins_text_blocks()
    {
        var handler = new StubHttpHandler(_ => (HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"Hello "},{"type":"tool_use","id":"x"},{"type":"text","text":"world"}]}"""));
        var client = new AnthropicLlmClient(new HttpClient(handler), "sk-secret-key-123", "https://api.anthropic.com", "claude-sonnet-4-6", 4096);

        var result = await client.CompleteAsync("SYS", "USER");

        Assert.Equal("Hello world", result); // only text blocks joined; tool_use skipped

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claude-sonnet-4-6", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYS", body.RootElement.GetProperty("system").GetString());
        var msg = body.RootElement.GetProperty("messages")[0];
        Assert.Equal("user", msg.GetProperty("role").GetString());
        Assert.Equal("USER", msg.GetProperty("content").GetString());

        Assert.Equal("sk-secret-key-123", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest!.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Anthropic_error_uses_frozen_prefix_and_sanitizes_key()
    {
        const string key = "sk-supersecretkey";
        var handler = new StubHttpHandler(_ => (HttpStatusCode.Unauthorized, $"{{\"error\":\"invalid key {key}\"}}"));
        var client = new AnthropicLlmClient(new HttpClient(handler), key, "https://api.anthropic.com", "m", 4096);

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u"));
        Assert.StartsWith("Anthropic API returned 401: ", ex.Message);
        Assert.DoesNotContain(key, ex.Message);
        Assert.Contains("***REDACTED***", ex.Message);
    }

    [Fact]
    public void StreamProgress_snapshot_tracks_chars_and_ticks()
    {
        var p = new StreamProgress();
        p.Update(100);
        p.Tick();
        var s = p.Snapshot();
        Assert.Equal(100, s.Chars);
        Assert.Equal(2, s.Ticks);
        Assert.True(s.ElapsedSeconds >= 0);
    }

    [Theory]
    [InlineData("""{"type":"text_delta","text":"hi"}""", "token", "hi")]
    [InlineData("""{"type":"thinking_delta","thinking":"hmm"}""", "thinking", "hmm")]
    [InlineData("""{"delta":{"type":"text_delta","text":"x"}}""", "token", "x")]
    [InlineData("""{"event":{"delta":{"type":"text_delta","text":"y"}}}""", "token", "y")]
    [InlineData("""{"type":"thinking_delta","thinking":"","text":"fallback"}""", "thinking", "fallback")] // empty thinking → text (Python `or`)
    public void ClassifyDelta_maps_stream_json_events(string json, string kind, string text)
    {
        using var d = JsonDocument.Parse(json);
        var cls = ClaudeCliLlmClient.ClassifyDelta(d.RootElement);
        Assert.NotNull(cls);
        Assert.Equal(kind, cls!.Value.Kind);
        Assert.Equal(text, cls.Value.Text);
    }

    [Fact]
    public void ClassifyDelta_returns_null_for_non_delta_event()
    {
        using var d = JsonDocument.Parse("""{"type":"message_start"}""");
        Assert.Null(ClaudeCliLlmClient.ClassifyDelta(d.RootElement));
    }

    [Fact]
    public void ResolveClaudeCliPath_finds_configured_absolute_path()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"claude_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "");
        try { Assert.Equal(tmp, ClaudeCli.ResolvePath(tmp)); }
        finally { File.Delete(tmp); }
    }

    // QAL-062: PlaywrightRunner reuses ClaudeCli.Which to resolve npm/npx — on Windows a bare
    // ProcessStartInfo("npm", ...) can't find the .cmd shim, so this PATHEXT-aware lookup is load-bearing.
    [Fact]
    public void Which_resolves_a_pathext_shim_via_path_lookup()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"which_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var shimExt = OperatingSystem.IsWindows() ? ".CMD" : "";
        var shim = Path.Combine(dir, $"fakenpm{shimExt}");
        File.WriteAllText(shim, "");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);
            Assert.Equal(shim, ClaudeCli.Which("fakenpm"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Which_returns_null_for_an_unresolvable_name()
        => Assert.Null(ClaudeCli.Which($"definitely-not-a-real-binary-{Guid.NewGuid():N}"));

    // QAL-062 regression: node.js on Windows ships a bare POSIX shell-script "npm" (not a valid
    // Win32 executable) alongside the real "npm.cmd" shim in the same directory. A bare-name-first
    // search resolves to the unusable decoy and every spawn fails with "not a valid application for
    // this OS platform" — extensions must be tried first.
    [Fact]
    public void Which_prefers_a_pathext_shim_over_a_same_named_bare_decoy()
    {
        if (!OperatingSystem.IsWindows()) return; // decoy-vs-shim ordering is a Windows-only concern

        var dir = Path.Combine(Path.GetTempPath(), $"which_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var bareDecoy = Path.Combine(dir, "fakenpm");
        var cmdShim = Path.Combine(dir, "fakenpm.cmd");
        File.WriteAllText(bareDecoy, "#!/bin/sh\n"); // exists, but not a Win32 executable
        File.WriteAllText(cmdShim, "");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);
            // Windows paths are case-insensitive; Which()'s result casing follows PATHEXT (".CMD"),
            // not necessarily the on-disk file's casing.
            Assert.Equal(cmdShim, ClaudeCli.Which("fakenpm"), ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(dir, recursive: true);
        }
    }
}
