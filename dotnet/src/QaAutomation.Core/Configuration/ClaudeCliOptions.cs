namespace QaAutomation.Core.Configuration;

/// <summary>
/// Local <c>claude</c> CLI provider (Pro/Max subscription via the logged-in CLI, no API key).
/// Mirrors config.py <c>claude_cli_*</c> (spec §5.6).
/// </summary>
public sealed class ClaudeCliOptions
{
    public const string SectionName = "ClaudeCli";

    /// <summary>PATH lookup by default ("claude"); or an absolute path to claude(.exe).</summary>
    public string Path { get; set; } = "claude";

    /// <summary>Blank = whatever the CLI defaults to.</summary>
    public string Model { get; set; } = "";

    /// <summary>Idle-timeout floor: the effective idle timeout is <c>max(30, this)</c>. The settings
    /// layer (B9) clamps writes to <c>max(10, value)</c>.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
