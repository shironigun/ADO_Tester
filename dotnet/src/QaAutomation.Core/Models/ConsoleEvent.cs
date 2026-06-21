namespace QaAutomation.Core.Models;

/// <summary>
/// One live event in a pipeline's activity stream (spec B5). Produced by the orchestrators
/// (phase/system/result/error/stat) and by the streaming Claude CLI (token/thinking deltas),
/// fanned out to SSE subscribers by the EventBus; a narrative subset is persisted to
/// <c>EventLogJson</c> for replay of finished runs.
/// <para><see cref="Seq"/>/<see cref="Ts"/>/<see cref="SessionKey"/> are assigned by the bus on
/// publish (NOT the producer) — <see cref="Seq"/> is the authoritative ordering / SSE replay cursor.</para>
/// <para>The wire strings (<see cref="Kind"/>, <see cref="Phase"/>, <see cref="Level"/>) are frozen —
/// the React console keys off them. Use the constants in <see cref="EventKinds"/>/<see cref="EventPhases"/>/
/// <see cref="EventLevels"/>.</para>
/// </summary>
public sealed class ConsoleEvent
{
    public required string Kind { get; init; }
    public string Text { get; set; } = "";
    public string? Phase { get; init; }
    public string Level { get; init; } = EventLevels.Info;
    public Dictionary<string, object?>? Data { get; set; }

    // Assigned by the bus on publish:
    public long Seq { get; set; }
    public double Ts { get; set; }
    public string SessionKey { get; set; } = "";

    public bool IsNarrative => EventKinds.Narrative.Contains(Kind);

    public bool IsTerminal =>
        Data is not null && Data.TryGetValue("terminal", out var t) && t is true;

    /// <summary>camelCase frame for the SSE/JSON payload (matches the rest of the API), in a fixed key order.</summary>
    public Dictionary<string, object?> ToCamel() => new()
    {
        ["seq"] = Seq,
        ["ts"] = Ts,
        ["sessionKey"] = SessionKey,
        ["kind"] = Kind,
        ["phase"] = Phase,
        ["level"] = Level,
        ["text"] = Text,
        ["data"] = Data,
    };
}

/// <summary>Frozen <c>kind</c> wire values + the narrative set persisted to <c>EventLogJson</c>.</summary>
public static class EventKinds
{
    public const string Phase = "phase";
    public const string Token = "token";
    public const string Thinking = "thinking";
    public const string System = "system";
    public const string Stat = "stat";
    public const string Result = "result";
    public const string Error = "error";

    /// <summary>Durable kinds persisted to EventLogJson. Token/thinking deltas are live-only.</summary>
    public static readonly IReadOnlySet<string> Narrative =
        new HashSet<string> { Phase, System, Result, Error, Stat };
}

/// <summary>Frozen <c>phase</c> wire values.</summary>
public static class EventPhases
{
    public const string Fetch = "fetch";
    public const string Retrieve = "retrieve";
    public const string Analyst = "analyst";
    public const string Tester = "tester";
    public const string BugWriter = "bugwriter";
    public const string Push = "push";
    public const string Summarize = "summarize";
    public const string Discover = "discover";
    public const string CodeVerify = "code-verify";
    public const string Enrich = "enrich";
    /// <summary>Emitted by the Find-Bugs triage pass (used at runtime by the Python orchestrator
    /// though absent from its declared phase literal).</summary>
    public const string Triage = "triage";
}

/// <summary>Frozen <c>level</c> wire values.</summary>
public static class EventLevels
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warn = "warn";
    public const string Error = "error";
}
