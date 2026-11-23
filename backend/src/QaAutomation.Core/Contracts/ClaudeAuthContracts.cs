namespace QaAutomation.Core.Contracts;

/// <summary>Result of <c>claude auth status --json</c> (plus CLI availability).</summary>
public sealed class ClaudeAuthStatus
{
    /// <summary>Whether the <c>claude</c> CLI binary was found.</summary>
    public bool Available { get; set; }

    /// <summary>Whether a Claude subscription/console session is currently signed in.</summary>
    public bool LoggedIn { get; set; }

    public string? AuthMethod { get; set; }
    public string? Email { get; set; }
    public string? OrgName { get; set; }
    public string? SubscriptionType { get; set; }

    /// <summary>Populated when status/login couldn't be determined.</summary>
    public string? Error { get; set; }
}

/// <summary>Response to starting a login: the OAuth URL the user opens + the id used to submit the code.</summary>
public sealed class ClaudeLoginStart
{
    public string LoginId { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>POST body: the authorization code pasted back from the Anthropic sign-in page.</summary>
public sealed class SubmitClaudeCodeRequest
{
    public string LoginId { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>Outcome of completing a login (submitting the code), with the refreshed status.</summary>
public sealed class ClaudeLoginResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public ClaudeAuthStatus? Status { get; set; }
}
