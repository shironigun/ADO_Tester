namespace QaAutomation.Core.Models;

/// <summary>One indexed test case from a <see cref="TestPlanScan"/>. Used for similarity
/// checks (title signature + step overlap) in the post-gen dedup pass.
/// Table: ExistingTestCaseIndexEntries.</summary>
public sealed class ExistingTestCaseIndexEntry
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    public int AdoTestCaseId { get; set; }
    /// <summary>Suite path within the plan (empty when ADO doesn't surface it in the batch response).</summary>
    public string SuitePath { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Normalized lowercase title (stopwords stripped, punctuation removed) for fast
    /// equality and Jaccard-similarity dedup.</summary>
    public string TitleSignature { get; set; } = "";
    /// <summary>Concatenated step actions (≤ 1000 chars), mirrored into the PostgreSQL test_case_index table.</summary>
    public string? StepsSummary { get; set; }
    public string? StepsHash { get; set; }
}
