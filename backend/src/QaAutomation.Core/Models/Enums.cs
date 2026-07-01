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

// ── QA Loop enums ────────────────────────────────────────────────────────────

// Exact-name enums (HasConversion<string>() — no [EnumMember] needed)
public enum ApiSessionStatus { Analyzing, AwaitingReview, Drafting, Drafted, Pushing, Done, Failed, Cancelled }

public enum ApiDraftStatus { Draft, Approved, Pushed, Rejected }

public enum TestRunStatus { Queued, Running, Passed, Failed, Cancelled, Error }

// Wire-string enums — stored/serialized as the [EnumMember] Value, not the C# member name.
public enum TestLevel
{
    [EnumMember(Value = "contract")]  Contract,
    [EnumMember(Value = "endpoint")]  Endpoint,
    [EnumMember(Value = "workflow")]  Workflow,
    [EnumMember(Value = "ui")]        Ui,  // reserved — not built in MVP
}

public enum FailureClassification
{
    [EnumMember(Value = "real_bug")]    RealBug,
    [EnumMember(Value = "flaky")]       Flaky,
    [EnumMember(Value = "test_issue")]  TestIssue,
    [EnumMember(Value = "stale_test")]  StaleTest,
}

public enum TriggerSource
{
    [EnumMember(Value = "manual")]           Manual,
    [EnumMember(Value = "pipeline-watcher")] PipelineWatcher,
}

public enum DmsProfile
{
    [EnumMember(Value = "ideal")]    Ideal,
    [EnumMember(Value = "infinity")] Infinity,
    [EnumMember(Value = "aspen")]    Aspen,
    [EnumMember(Value = "quantum")]  Quantum,
}

public static class QaLoopEnumExtensions
{
    public static string ToWire(this TestLevel v) => v switch
    {
        TestLevel.Contract => "contract", TestLevel.Endpoint => "endpoint",
        TestLevel.Workflow => "workflow", _ => "ui",
    };
    /// <summary>The on-disk scripts subfolder for a level (plural of <see cref="ToWire"/>) — single
    /// source of truth shared by where generated specs are written and where the runner looks for them.</summary>
    public static string ToDirName(this TestLevel v) => v.ToWire() + "s";
    public static TestLevel ToTestLevel(string s) => s switch
    {
        "contract" => TestLevel.Contract, "endpoint" => TestLevel.Endpoint,
        "workflow" => TestLevel.Workflow, _ => TestLevel.Ui,
    };

    public static string ToWire(this FailureClassification v) => v switch
    {
        FailureClassification.RealBug   => "real_bug",  FailureClassification.Flaky     => "flaky",
        FailureClassification.TestIssue => "test_issue", _ => "stale_test",
    };
    public static FailureClassification ToFailureClassification(string s) => s switch
    {
        "real_bug" => FailureClassification.RealBug, "flaky" => FailureClassification.Flaky,
        "test_issue" => FailureClassification.TestIssue, _ => FailureClassification.StaleTest,
    };

    public static string ToWire(this TriggerSource v) =>
        v == TriggerSource.PipelineWatcher ? "pipeline-watcher" : "manual";
    public static TriggerSource ToTriggerSource(string s) =>
        s == "pipeline-watcher" ? TriggerSource.PipelineWatcher : TriggerSource.Manual;

    public static string ToWire(this DmsProfile v) => v switch
    {
        DmsProfile.Ideal => "ideal", DmsProfile.Infinity => "infinity",
        DmsProfile.Aspen => "aspen", _ => "quantum",
    };
    public static DmsProfile ToDmsProfile(string s) => s switch
    {
        "ideal" => DmsProfile.Ideal, "infinity" => DmsProfile.Infinity,
        "aspen" => DmsProfile.Aspen, _ => DmsProfile.Quantum,
    };
}
