using System.ClientModel;
using System.Text;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Azure OpenAI provider via the official <c>Azure.AI.OpenAI</c> SDK (non-streaming). On a non-2xx
/// status it raises the frozen message <c>"Azure OpenAI returned {status}: {sanitized body}"</c>
/// (spec §5.6). Token cap is the shared Anthropic max-tokens (passed by the factory).
/// </summary>
public sealed class AzureOpenAiLlmClient : ILlmClient
{
    public string ProviderName => "AzureOpenAI";

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _deployment;
    private readonly int _maxTokens;

    public AzureOpenAiLlmClient(string endpoint, string apiKey, string deployment, int maxTokens)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _deployment = deployment;
        _maxTokens = maxTokens;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, string? model = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var deployment = string.IsNullOrEmpty(model) ? _deployment : model;
        try
        {
            var client = new AzureOpenAIClient(new Uri(_endpoint), new ApiKeyCredential(_apiKey));
            var chat = client.GetChatClient(deployment);
            ChatCompletion completion = await chat.CompleteChatAsync(
                [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
                new ChatCompletionOptions { MaxOutputTokenCount = _maxTokens },
                cancellationToken);
            return JoinText(completion);
        }
        catch (ClientResultException e)
        {
            throw new LlmException($"Azure OpenAI returned {e.Status}: {Sanitizer.Sanitize(e.Message, _apiKey)}");
        }
    }

    internal static string JoinText(ChatCompletion completion)
    {
        var sb = new StringBuilder();
        foreach (var part in completion.Content)
            if (part.Kind == ChatMessageContentPartKind.Text)
                sb.Append(part.Text);
        return sb.ToString();
    }
}
