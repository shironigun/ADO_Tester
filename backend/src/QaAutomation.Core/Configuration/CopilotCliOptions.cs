namespace QaAutomation.Core.Configuration;

/// <summary>
/// Local GitHub <c>copilot</c> CLI provider (Copilot subscription via the logged-in CLI, no API key).
/// Mirrors <see cref="ClaudeCliOptions"/>.
/// </summary>
public sealed class CopilotCliOptions
{
    public const string SectionName = "CopilotCli";

    /// <summary>Blank = full auto-detect. Otherwise a bare binary name (PATH lookup) or an absolute path.</summary>
    public string Path { get; set; } = "";

    /// <summary>Blank = whatever the CLI defaults to.</summary>
    public string Model { get; set; } = "";

    /// <summary>Context window tier: blank = CLI default, "long_context" = maximum context window.
    /// Passed as <c>--context &lt;tier&gt;</c>. Choices: "default", "long_context".</summary>
    public string Context { get; set; } = "";

    /// <summary>Idle-timeout floor: the effective idle timeout is <c>max(30, this)</c>. The settings
    /// layer clamps writes to <c>max(10, value)</c>.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
