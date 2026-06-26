using QaAutomation.Core.Contracts;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Generates runnable Playwright specs from KB flows and writes them into the greenfield <c>/automation</c>
/// folder (Track A Generator — the "Tester → Generator" evolution). Honors the constitution's
/// "write only inside /automation" rule.
/// </summary>
public interface IGenerationService
{
    Task<GenerateSpecResult> GenerateE2eSpecAsync(int projectId, GenerateSpecRequest request, CancellationToken ct = default);
}
