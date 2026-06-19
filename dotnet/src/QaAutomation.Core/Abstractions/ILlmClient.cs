namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Provider-agnostic single-turn chat completion. Implementations: Anthropic, AzureOpenAI,
/// OpenAICompatible, ClaudeCli. Returns the concatenated text content of the reply (never null).
/// <para>
/// <paramref name="progress"/> and <paramref name="emit"/> are updated live by streaming providers
/// (currently only ClaudeCli); non-streaming providers ignore them — they never throw (spec §5.6).
/// </para>
/// </summary>
public interface ILlmClient
{
    /// <summary>Exact provider name: "Anthropic" | "AzureOpenAI" | "OpenAICompatible" | "ClaudeCli".</summary>
    string ProviderName { get; }

    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        StreamProgress? progress = null,
        Action<LlmEmit>? emit = null,
        CancellationToken cancellationToken = default);
}
