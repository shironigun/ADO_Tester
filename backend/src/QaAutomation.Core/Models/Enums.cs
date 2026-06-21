using System.Runtime.Serialization;

namespace QaAutomation.Core.Models;

// Status/severity/theme enums serialize as their EXACT name — the React UI keys off these strings
// (spec §5.2). Stored in SQLite as that string via EF `.HasConversion<string>()`.

public enum SessionStatus { Analyzing, AwaitingTester, Drafting, Drafted, Pushing, Done, Failed, Cancelled }

public enum DraftStatus { Draft, Approved, Rejected, Pushed }

public enum BugSessionStatus { Analyzing, AwaitingActual, Verifying, Drafting, Drafted, Pushing, Done, Failed, Cancelled }

/// <summary>Distinct from <see cref="DraftStatus"/> even though the members coincide (spec §5.2 — do not unify).</summary>
public enum BugDraftStatus { Draft, Approved, Rejected, Pushed }

public enum BugSeverity { Critical, High, Medium, Low }

public enum ReportStatus { Generating, Done, Failed }

public enum ReportTheme { Executive, Standup, Branded }

// Batch module/mode use kebab / lowercase WIRE + DB values (NOT the C# member name). EF stores them
// via a custom value converter (see QaDbContext); JSON (B8) will need a custom converter too — net8's
// JsonStringEnumConverter does not honor [EnumMember]. The attributes mark the canonical wire value.
public enum BatchModule
{
    [EnumMember(Value = "author-tests")] AuthorTests,
    [EnumMember(Value = "find-bugs")] FindBugs,
}

public enum BatchMode
{
    [EnumMember(Value = "auto")] Auto,
    [EnumMember(Value = "manual")] Manual,
}

/// <summary>Maps the Batch enums to/from their kebab/lowercase wire+DB strings (used by the EF
/// converter today; reused by the JSON converter in B8).</summary>
public static class BatchEnumExtensions
{
    public static string ToWire(this BatchModule m) => m == BatchModule.FindBugs ? "find-bugs" : "author-tests";
    public static BatchModule ToBatchModule(string s) => s == "find-bugs" ? BatchModule.FindBugs : BatchModule.AuthorTests;

    public static string ToWire(this BatchMode m) => m == BatchMode.Manual ? "manual" : "auto";
    public static BatchMode ToBatchMode(string s) => s == "manual" ? BatchMode.Manual : BatchMode.Auto;
}
