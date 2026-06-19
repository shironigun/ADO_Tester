using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over any OpenAI-compatible endpoint (GitHub Models, Groq, OpenAI,
/// Ollama). POST {BaseUrl}/chat/completions with Bearer auth.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    public const string HttpClientName = "openaicompatible";

    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;

    public OpenAiCompatibleLlmClient(HttpClient http, IOptions<OpenAiCompatibleOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string ProviderName => "OpenAICompatible";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest
        {
            Model = model ?? _options.Model,
            MaxTokens = _options.MaxTokens,
            Messages =
            [
                new Message { Role = "system", Content = systemPrompt },
                new Message { Role = "user",   Content = userPrompt },
            ],
        };

        using var response = await _http.PostAsJsonAsync("/chat/completions", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenAI-compatible endpoint returned {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from OpenAI-compatible endpoint.");

        return result.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
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
