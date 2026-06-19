using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>
/// Anthropic (Claude) provider via a raw POST to <c>/v1/messages</c> (non-streaming — ignores
/// progress/emit, like the Python AnthropicLlmClient). Joins the text content blocks. On a non-2xx
/// status it raises the frozen message <c>"Anthropic API returned {status}: {sanitized body}"</c>
/// (spec §5.6). Transport errors propagate (Python only wraps status errors).
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private const string AnthropicVersion = "2023-06-01";

    public string ProviderName => "Anthropic";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicLlmClient(HttpClient http, string apiKey, string baseUrl, string model, int maxTokens)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        _model = model;
        _maxTokens = maxTokens;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, string? model = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(model) ? _model : model,
            ["max_tokens"] = _maxTokens,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = userPrompt } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

        using var resp = await _http.SendAsync(req, cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new LlmException($"Anthropic API returned {(int)resp.StatusCode}: {Sanitizer.Sanitize(text, _apiKey)}");

        using var doc = JsonDocument.Parse(text);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    sb.Append(txt.GetString());
            }
        }
        return sb.ToString();
    }
}
