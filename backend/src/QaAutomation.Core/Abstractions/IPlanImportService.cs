using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>Imports ADO Test Plan cases into QA Loop drafts for one automation unit (INT-203).</summary>
public interface IPlanImportService
{
    /// <summary>
    /// Filter Plan map to <paramref name="selectedUnit"/> (or session FrsSlug/Module), fetch TCs,
    /// create thin/rich drafts with <c>AdoTestCaseId</c>, gap drafts without id; skip duplicate ADO ids.
    /// </summary>
    Task<PlanImportMeta> ImportAsync(
        int sessionId, string? selectedUnit = null, bool remapIfMissing = true, CancellationToken ct = default);
}
