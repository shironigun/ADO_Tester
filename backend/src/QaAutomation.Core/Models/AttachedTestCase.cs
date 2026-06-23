namespace QaAutomation.Core.Models;

/// <summary>
/// A test case attached to a story for Find-Bugs verification (spec B5). <see cref="Source"/> is
/// <c>"plan"</c> (an ADO-linked Test Case work item — source of truth) or <c>"draft"</c> (a local
/// Author-Tests draft not yet linked). <see cref="Key"/> is a stable, unique selection id
/// (<c>plan#{adoId}</c> / <c>draft#{localId}</c>).
/// </summary>
public sealed record AttachedTestCase(
    string Key,
    string Title,
    string Preconditions,
    string Source,
    int? AdoTestCaseId,
    IReadOnlyList<TestCaseStep> Steps);
