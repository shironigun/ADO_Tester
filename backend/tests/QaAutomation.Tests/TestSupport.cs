using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Tests;

/// <summary>An ILlmClient that returns a queued sequence of canned responses and records the system
/// prompt of each call — for testing the agents' parsing + complete_json retry/nudge.</summary>
internal sealed class StubLlmClient : ILlmClient
{
    private readonly Queue<string> _responses;
    public List<string> SystemPrompts { get; } = [];
    public string ProviderName => "Stub";

    public StubLlmClient(params string[] responses) => _responses = new Queue<string>(responses);

    public Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, string? model = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        SystemPrompts.Add(systemPrompt);
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "");
    }
}

/// <summary>Captures the last outgoing request body (and method/url/content-type), returning a canned
/// 200 so the client's parse path completes. Used to golden-diff the built request bodies.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    public string? LastContent { get; private set; }
    public string? LastContentType { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastUrl { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method.Method;
        LastUrl = request.RequestUri?.ToString();
        if (request.Content is not null)
        {
            LastContent = await request.Content.ReadAsStringAsync(cancellationToken);
            LastContentType = request.Content.Headers.ContentType?.MediaType;
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":123}", Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Returns a caller-configured response and captures the request body/headers — for
/// golden-diffing LLM/HTTP request shapes and error mapping.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
    public string? LastBody { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        var (status, body) = _responder(request);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Minimal IOptionsMonitor returning a fixed value (the real one needs DI plumbing).</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;
    public StaticOptionsMonitor(T value) => _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
