namespace QaAutomation.Core.Contracts;

/// <summary>
/// Request to generate a Playwright E2E spec from a KB flow (Track A Generator). The spec is written into
/// the greenfield <c>/automation</c> folder (guarded: writes only inside an "automation" folder).
/// </summary>
public sealed record GenerateSpecRequest
{
    /// <summary>Module key, e.g. "deals" — used for the output subfolder and the prompt.</summary>
    public string Module { get; init; } = "";

    /// <summary>Scenario/case title WITHOUT the id/tag wrapper, e.g. "Create a deal with a customer".</summary>
    public string ScenarioTitle { get; init; } = "";

    /// <summary>Optional one-line intent to focus the generation.</summary>
    public string ScenarioIntent { get; init; } = "";

    /// <summary>KB doc file name to ground on (e.g. "deals-create-deal.md"); loaded from the KB store.</summary>
    public string? KbFileName { get; init; }

    /// <summary>Or pass KB flow markdown directly — takes precedence over <see cref="KbFileName"/>.</summary>
    public string? KbFlowMarkdown { get; init; }

    /// <summary>ADO Test Case id to embed as <c>[id]</c> in the title. Null → either created (see
    /// <see cref="CreateAdoTestCase"/>) or a <c>[TC-TODO]</c> placeholder.</summary>
    public int? AdoTestCaseId { get; init; }

    /// <summary>When true and <see cref="AdoTestCaseId"/> is null: author a Test Case from the KB flow,
    /// create it in ADO (deterministic client), capture its id, and tag the spec with it.</summary>
    public bool CreateAdoTestCase { get; init; }

    /// <summary>Add the created Test Case to this plan/suite (both required for the suite add). Optional.</summary>
    public int? TestPlanId { get; init; }
    public int? TestSuiteId { get; init; }

    /// <summary>Link the created Test Case to this story via the Tested-By relation. Optional.</summary>
    public int? StoryId { get; init; }

    /// <summary>Append the <c>@smoke</c> tag to the title.</summary>
    public bool Smoke { get; init; } = true;

    /// <summary>Absolute path to the <c>/automation</c> folder to write into (takes precedence over SourceRoot).</summary>
    public string? OutputRoot { get; init; }

    /// <summary>Repo root; the automation path is derived as &lt;root&gt;/&lt;configured-repo-root&gt;/automation.</summary>
    public string? SourceRoot { get; init; }

    /// <summary>Optional repo-relative path to an existing spec used only as a house-style reference.</summary>
    public string? StyleReferencePath { get; init; }

    /// <summary>If false, return the generated code without writing a file (dry run / preview).</summary>
    public bool Write { get; init; } = true;
}

/// <summary>Result of a spec generation — the code, where it was written, and any ADO Test Case created.</summary>
public sealed record GenerateSpecResult
{
    public string TestTitle { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? FilePath { get; init; }   // null if Write=false
    public bool Written { get; init; }
    public int CharCount { get; init; }
    public string Code { get; init; } = "";

    /// <summary>The ADO Test Case id embedded in the title (created this run, supplied, or null for TC-TODO).</summary>
    public int? AdoTestCaseId { get; init; }

    /// <summary>True when this run created the Test Case in ADO (vs. a supplied/absent id).</summary>
    public bool AdoTestCaseCreated { get; init; }
}
