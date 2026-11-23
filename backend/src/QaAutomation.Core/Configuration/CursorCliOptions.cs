namespace QaAutomation.Core.Configuration;

/// <summary>
/// Local Cursor <c>agent</c> CLI provider (Cursor subscription / API key). Headless <c>-p</c> print mode.
/// </summary>
public sealed class CursorCliOptions
{
    public const string SectionName = "CursorCli";

    /// <summary>Blank = auto-detect <c>agent</c> / <c>cursor-agent</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>Blank = CLI default (<c>auto</c>). Model ids from <c>agent --list-models</c>.</summary>
    public string Model { get; set; } = "";

    /// <summary>Optional effort override composed into <c>--model id[effort=…]</c> when set.</summary>
    public string Effort { get; set; } = "";

    /// <summary>Optional context override composed into <c>--model id[context=…]</c> (e.g. <c>1m</c>).</summary>
    public string Context { get; set; } = "";

    /// <summary>Idle-timeout floor: effective idle is <c>max(30, this)</c>.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
