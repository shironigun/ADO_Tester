namespace QaAutomation.Core.Configuration;

public enum LlmProvider
{
    Anthropic,
    AzureOpenAI,
    /// <summary>Any OpenAI-compatible endpoint: GitHub Models (free), Groq, OpenRouter, OpenAI, Ollama.</summary>
    OpenAICompatible,
}

/// <summary>Default provider at startup. Live switching is handled by ILlmProviderState.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;
}
