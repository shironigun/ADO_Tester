using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.AzureDevOps;

namespace QaAutomation.Tests;

/// <summary>INT-205 — UpdateTestCase patch body + PushDraft patches when AdoTestCaseId set.</summary>
public sealed class UpdateTestCasePushTests : IDisposable
{
    private readonly CapturingHandler _handler = new();
    private readonly AzureDevOpsClient _client;

    public UpdateTestCasePushTests()
    {
        var opts = new AzureDevOpsOptions
        {
            Organization = "TestOrg",
            Project = "Test Project",
            ApiVersion = "7.1",
            PersonalAccessToken = "dummy",
        };
        _client = new AzureDevOpsClient(
            new HttpClient(_handler) { BaseAddress = new Uri("https://dev.azure.com/") },
            new StaticOptionsMonitor<AzureDevOpsOptions>(opts),
            NullLogger<AzureDevOpsClient>.Instance);
        _handler.ResponseJson = """{"id":100002}""";
    }

    [Fact]
    public async Task UpdateTestCase_uses_replace_ops_and_patch()
    {
        var steps = new List<TestCaseStep>
        {
            new() { Action = "Open deals", Expected = "List loads" },
        };
        await _client.UpdateTestCaseAsync(100002, "Improved title", "Logged in", steps);

        Assert.Equal("PATCH", _handler.LastMethod);
        Assert.Contains("/wit/workitems/100002", _handler.LastUrl, StringComparison.Ordinal);
        Assert.Equal("application/json-patch+json", _handler.LastContentType);

        using var doc = JsonDocument.Parse(_handler.LastContent!);
        var ops = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("op").GetString()).ToList();
        Assert.All(ops, op => Assert.Equal("replace", op));
        var paths = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("/fields/System.Title", paths);
        Assert.Contains("/fields/Microsoft.VSTS.TCM.Steps", paths);
        Assert.Contains("/fields/System.Description", paths);
    }

    public void Dispose() => _handler.Dispose();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastMethod { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastContent { get; private set; }
        public string? LastContentType { get; private set; }
        public string ResponseJson { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method.Method;
            LastUrl = request.RequestUri?.ToString();
            if (request.Content is not null)
            {
                LastContent = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentType = request.Content.Headers.ContentType?.MediaType;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
