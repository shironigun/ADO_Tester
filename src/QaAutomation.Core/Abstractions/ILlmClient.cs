namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Provider-agnostic chat completion client. Implementations: Anthropic (Claude) and
/// Azure OpenAI. The active provider is chosen by configuration (<c>Llm:Provider</c>).
/// Agent tool-use loops build on this.
/// </summary>
public interface ILlmClient
{
    /// <summary>Identifier of the active provider, e.g. "Anthropic" or "AzureOpenAI".</summary>
    string ProviderName { get; }

    /// <summary>Single-turn completion. Returns the concatenated text content of the reply.</summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken cancellationToken = default);
}
