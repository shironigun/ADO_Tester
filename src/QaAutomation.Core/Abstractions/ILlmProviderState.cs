using QaAutomation.Core.Configuration;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Holds the currently-active LLM provider so it can be switched at runtime from the UI.
/// Singleton; initialized from <c>Llm:Provider</c> config. (Not persisted in v1 — resets on restart.)
/// </summary>
public interface ILlmProviderState
{
    LlmProvider Current { get; }
    void Set(LlmProvider provider);
    IReadOnlyList<LlmProvider> Available { get; }
}
