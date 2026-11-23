using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;

namespace QaAutomation.Infrastructure.AzureDevOps;

/// <summary>
/// Attaches PAT Basic auth to every ADO request, re-read from options on each call. Satisfies spec
/// §5.5 ("re-read the PAT per request") — IHttpClientFactory pools the underlying handler, so a
/// settings change applies on the next request with no client rebuild. Header is
/// <c>Basic base64(":" + pat)</c> (empty username), matching the Python <c>_basic_auth_header</c>.
/// </summary>
public sealed class AdoAuthHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<AzureDevOpsOptions> _options;

    public AdoAuthHandler(IOptionsMonitor<AzureDevOpsOptions> options) => _options = options;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pat = _options.CurrentValue.PersonalAccessToken;
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return base.SendAsync(request, cancellationToken);
    }
}
