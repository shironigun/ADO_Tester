using QaAutomation.Core.Configuration;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Tracks the active LLM provider, persisted in the Settings table (key <c>llm.provider</c>) so the
/// choice survives restarts (spec §5.6). Process singleton; loads lazily on first read; a malformed
/// stored value falls back to the configured default.
/// </summary>
public interface ILlmProviderState
{
    Task<LlmProvider> CurrentAsync(CancellationToken cancellationToken = default);
    Task SetAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    IReadOnlyList<LlmProvider> Available { get; }
}
