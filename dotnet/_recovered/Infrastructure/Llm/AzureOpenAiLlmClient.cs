using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over Azure OpenAI chat completions:
/// POST {Endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...
/// </summary>
public sealed class AzureOpenAiLlmClient : ILlmClient
{
    public const string HttpClientName = "azureopenai";

    private readonly HttpClient _http;
    private readonly AzureOpenAiOptions _options;

    public AzureOpenAiLlmClient(HttpClient http, IOptions<AzureOpenAiOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string ProviderName => "AzureOpenAI";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var deployment = model ?? _options.Deployment;
        var url = $"/openai/deployments/{deployment}/chat/completions?api-version={_options.ApiVersion}";

        var request = new ChatRequest
        {
            MaxTokens = _options.MaxTokens,
            Messages =
            [
                new Message { Role = "system", Content = systemPrompt },
                new Message { Role = "user",   Content = userPrompt },
            ],
        };

        using var response = await _http.PostAsJsonAsync(url, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Azure OpenAI returned {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from Azure OpenAI.");

        return result.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    private sealed class ChatRequest
    {
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = [];
    }

    private sealed class Message
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = [];
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public Message? Message { get; set; }
    }
}
