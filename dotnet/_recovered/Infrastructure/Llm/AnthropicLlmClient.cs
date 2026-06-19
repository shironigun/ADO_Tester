using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over the Anthropic Messages API. Single-turn for S0; the agent
/// tool-use loops will extend this with tool definitions and multi-turn history.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string HttpClientName = "anthropic";

    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;

    public AnthropicLlmClient(HttpClient http, IOptions<AnthropicOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string ProviderName => "Anthropic";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var request = new MessagesRequest
        {
            Model = model ?? _options.Model,
            MaxTokens = _options.MaxTokens,
            System = systemPrompt,
            Messages = [new Message { Role = "user", Content = userPrompt }],
        };

        using var response = await _http.PostAsJsonAsync("/v1/messages", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from Anthropic API.");

        return string.Concat(result.Content.Where(b => b.Type == "text").Select(b => b.Text));
    }

    private sealed class MessagesRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public string System { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = [];
    }

    private sealed class Message
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class MessagesResponse
    {
        [JsonPropertyName("content")] public List<ContentBlock> Content { get; set; } = [];
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}
