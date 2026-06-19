namespace QaAutomation.Core;

/// <summary>Raised when an agent's LLM output cannot be parsed as JSON after all retry attempts
/// (mirrors the Python <c>complete_json</c> RuntimeError). Maps to a pipeline failure.</summary>
public sealed class AgentException : Exception
{
    public AgentException(string message) : base(message) { }
}
