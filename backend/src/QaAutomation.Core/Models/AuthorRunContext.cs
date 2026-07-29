namespace QaAutomation.Core.Models;

/// <summary>Optional per-run guidance for the Author-Tests pipeline: free-text context that steers the
/// Analyst/Tester, plus an optional reference Test Plan the Tester should not duplicate. All fields
/// optional so a null context reproduces the pre-enhancement behavior.</summary>
public sealed record AuthorRunContext(
    string? AdditionalContext = null,
    int? ReferenceTestPlanId = null,
    int? ReferenceTestSuiteId = null,
    string? PlanNavigationHint = null);
