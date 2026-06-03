using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

public interface IAzureDevOpsClient
{
    // ---- Read ----
    Task<AdoWorkItem> GetWorkItemAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(
        string query, int top = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(
        int planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tickets in the date range. Includes work items created OR changed-to-closed in the window.
    /// If <paramref name="boards"/> is non-empty, restricts by AreaPath prefix.
    /// </summary>
    Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
        CancellationToken cancellationToken = default);

    // ---- Write (Test Plan REST — the ADO MCP gap) ----

    /// <summary>Creates a Test Case work item with steps and returns its ID.</summary>
    Task<int> CreateTestCaseAsync(
        string title,
        string preconditions,
        IReadOnlyList<TestCaseStep> steps,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an existing Test Case work item to a Test Suite.</summary>
    Task AddTestCaseToSuiteAsync(
        int planId, int suiteId, int testCaseId,
        CancellationToken cancellationToken = default);

    /// <summary>Links a Test Case to a user story as "Tested By".</summary>
    Task LinkTestedByAsync(
        int storyId, int testCaseId,
        CancellationToken cancellationToken = default);
}

// ---- Lightweight ADO DTOs ----

public sealed class AdoTestPlan
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class AdoTestSuite
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
