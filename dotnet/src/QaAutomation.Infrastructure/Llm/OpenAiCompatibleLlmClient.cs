using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Any OpenAI-compatible endpoint (GitHub Models, Groq, OpenRouter, OpenAI, Ollama) via the official
/// <c>OpenAI</c> SDK pointed at a custom base URL (non-streaming). Blank key falls back to the literal
/// <c>"missing-key"</c> so construction never fails (auth surfaces at call time) — matches Python. On a
/// non-2xx status: <c>"OpenAI-compatible endpoint returned {status}: {sanitized body}"</c> (spec §5.6).
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    public string ProviderName => "OpenAICompatible";

    private readonly string _baseUrl;
    private readonly string _apiKey;     // post-fallback value sent to the SDK
    private readonly string _rawApiKey;  // original config value — the sanitize secret (Python parity)
    private readonly string _model;
    private readonly int _maxTokens;

    public OpenAiCompatibleLlmClient(string baseUrl, string apiKey, string model, int maxTokens)
    {
        _baseUrl = baseUrl;
        _rawApiKey = apiKey ?? "";
        _apiKey = string.IsNullOrEmpty(apiKey) ? "missing-key" : apiKey;
        _model = model;
        _maxTokens = maxTokens;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, string? model = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var chosenModel = string.IsNullOrEmpty(model) ? _model : model;
        try
        {
            var client = new OpenAIClient(new ApiKeyCredential(_apiKey), new OpenAIClientOptions { Endpoint = new Uri(_baseUrl) });
            var chat = client.GetChatClient(chosenModel);
            ChatCompletion completion = await chat.CompleteChatAsync(
                [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
                new ChatCompletionOptions { MaxOutputTokenCount = _maxTokens },
                cancellationToken);
            return AzureOpenAiLlmClient.JoinText(completion);
        }
        catch (ClientResultException e)
        {
            // Sanitize against the ORIGINAL configured key (blank → nothing redacted), like Python —
            // not the post-fallback "missing-key", which would otherwise get redacted from the body.
            throw new LlmException($"OpenAI-compatible endpoint returned {e.Status}: {Sanitizer.Sanitize(e.Message, _rawApiKey)}");
        }
    }
}
