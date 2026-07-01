namespace QaAutomation.Core.Configuration;

public enum LlmProvider
{
    Anthropic,
    AzureOpenAI,
    /// <summary>Any OpenAI-compatible endpoint: GitHub Models (free), Groq, OpenRouter, OpenAI, Ollama.</summary>
    OpenAICompatible,
    /// <summary>Local <c>claude</c> CLI (Pro/Max subscription, no API key) — streams token deltas.</summary>
    ClaudeCli,
    /// <summary>Local GitHub <c>copilot</c> CLI (Copilot subscription, no API key) — mirrors ClaudeCli.</summary>
    CopilotCli,
}

/// <summary>Default provider at startup. Live switching is handled by ILlmProviderState.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;
}
