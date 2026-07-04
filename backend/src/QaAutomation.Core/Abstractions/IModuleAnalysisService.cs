using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Analyzes a whole module (Track A module run): reads its source and the existing test cases of a
/// plan/suite, discovers every flow, maps coverage (covered vs gap), stores a whole-module KB doc, and
/// returns the coverage map. Specs are generated on demand per flow afterwards.
/// </summary>
public interface IModuleAnalysisService
{
    Task<ModuleAnalyzeResult> AnalyzeAsync(int projectId, ModuleAnalyzeRequest request, CancellationToken ct = default);

    /// <summary>Generate a Playwright spec for one analyzed flow on demand: build the flow's detailed KB,
    /// then emit the spec (covered flow tags its existing test case; a gap creates + tags a new ADO Test Case).</summary>
    Task<ModuleFlowSpecResult> GenerateFlowSpecAsync(int projectId, ModuleFlowSpecRequest request, CancellationToken ct = default);
}
