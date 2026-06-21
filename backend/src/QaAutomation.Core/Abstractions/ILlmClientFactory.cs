namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Builds a concrete <see cref="ILlmClient"/> for the currently-active provider PER CALL (no cache),
/// reading settings at construction — so a runtime provider switch or a settings edit applies to the
/// next call. This is NOT a singleton <see cref="ILlmClient"/> (spec §5.6).
/// </summary>
public interface ILlmClientFactory
{
    Task<ILlmClient> CreateAsync(CancellationToken cancellationToken = default);
}
