using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Native Ollama <c>/api/chat</c> provider. Uses the stable native API for both local Ollama and
/// Ollama Cloud rather than the optional OpenAI-compatibility surface.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    public string ProviderName => "Ollama";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;

    public OllamaLlmClient(HttpClient http, string baseUrl, string apiKey, string model, int maxTokens)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _apiKey = apiKey ?? "";
        _model = model;
        _maxTokens = maxTokens;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        StreamProgress? progress = null,
        Action<LlmEmit>? emit = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(model) ? _model : model,
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            },
            ["options"] = new JsonObject { ["num_predict"] = _maxTokens },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new LlmException($"Ollama API returned {(int)response.StatusCode}: {Sanitizer.Sanitize(text, _apiKey)}");

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";
        }
        catch (JsonException exception)
        {
            throw new LlmException($"Ollama API returned invalid JSON: {exception.Message}");
        }

        throw new LlmException("Ollama API response did not contain message.content.");
    }

    internal static string NormalizeBaseUrl(string? baseUrl)
    {
        var normalized = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            normalized = "http://localhost:11434";
        return normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}/api";
    }
}
