namespace QaAutomation.Core;

/// <summary>
/// Raised when Azure DevOps returns a non-success status. Carries the SANITIZED body plus the HTTP
/// status so callers can branch (404 → 404, otherwise → 502; spec §5.4/§5.5). The message format is
/// frozen: <c>ADO [{op}] {status}: {body[:512]}</c> (body truncated to 512 chars in the message only;
/// <see cref="Body"/> keeps the full sanitized text).
/// </summary>
public sealed class AdoApiError : Exception
{
    public string Op { get; }
    public int Status { get; }
    public string Body { get; }

    public AdoApiError(string op, int status, string body)
        : base($"ADO [{op}] {status}: {(body.Length > 512 ? body[..512] : body)}")
    {
        Op = op;
        Status = status;
        Body = body;
    }
}
