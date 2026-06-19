namespace QaAutomation.Core;

/// <summary>
/// Raised when an LLM provider call fails. Carries the provider-prefixed, secret-sanitized message
/// (e.g. <c>"Anthropic API returned 401: ..."</c>). Maps to HTTP 502 (spec §5.4).
/// </summary>
public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
    public LlmException(string message, Exception inner) : base(message, inner) { }
}
