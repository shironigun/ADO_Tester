using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.AzureDevOps;

/// <summary>
/// Azure DevOps REST client (PAT / Basic auth). Covers:
///  - Work item reads (S0)
///  - Test Plan / Suite / Test Case management REST (the ADO MCP gap, S2)
/// </summary>
public sealed class AzureDevOpsClient : IAzureDevOpsClient
{
    public const string HttpClientName = "ado";

    private static readonly string[] WorkItemFields =
    [
        "System.Id", "System.Title", "System.Description",
        "System.WorkItemType", "System.AssignedTo", "System.State",
        "Microsoft.VSTS.Common.AcceptanceCriteria",
    ];

    private readonly HttpClient _http;
    private readonly AzureDevOpsOptions _opts;

    public AzureDevOpsClient(HttpClient http, IOptions<AzureDevOpsOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<AdoWorkItem> GetWorkItemAsync(int id, CancellationToken ct = default)
    {
        var fields = string.Join(",", WorkItemFields);
        var url = $"{_opts.BaseUrl}/_apis/wit/workitems/{id}?fields={fields}&api-version={_opts.ApiVersion}";
        using var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        using var doc = await r.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException($"Empty response for WI {id}");
        return MapWorkItem(id, doc.RootElement);
    }

    public async Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(
        string query, int top = 20, CancellationToken ct = default)
    {
        // If the user typed a numeric ID, fetch that work item directly.
        var trimmed = query.Trim();
        if (int.TryParse(trimmed, out var id))
        {
            try { return [await GetWorkItemAsync(id, ct)]; }
            catch { return []; }
        }

        var wiql = new
        {
            query = $"SELECT [System.Id] FROM WorkItems " +
                    $"WHERE [System.TeamProject] = '{_opts.Project}' " +
                    $"AND [System.Title] CONTAINS '{query.Replace("'", "''")}' " +
                    $"AND [System.WorkItemType] IN ('User Story','Bug','Task','Feature') " +
                    $"ORDER BY [System.ChangedDate] DESC"
        };
        var url = $"{_opts.BaseUrl}/_apis/wit/wiql?$top={top}&api-version={_opts.ApiVersion}";
        using var r = await _http.PostAsJsonAsync(url, wiql, ct);
        r.EnsureSuccessStatusCode();
        using var doc = await r.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException("Empty WIQL response");

        var ids = doc.RootElement
            .GetProperty("workItems")
            .EnumerateArray()
            .Select(x => x.GetProperty("id").GetInt32())
            .ToArray();

        if (ids.Length == 0) return [];

        var fields = string.Join(",", WorkItemFields);
        var idList = string.Join(",", ids);
        var batchUrl = $"{_opts.BaseUrl}/_apis/wit/workitems?ids={idList}&fields={fields}&api-version={_opts.ApiVersion}";
        using var br = await _http.GetAsync(batchUrl, ct);
        br.EnsureSuccessStatusCode();
        using var bdoc = await br.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException("Empty batch response");

        return bdoc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => MapWorkItem(e.GetProperty("id").GetInt32(), e))
            .ToList();
    }

    public async Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default)
    {
        var url = $"{_opts.BaseUrl}/_apis/testplan/plans?api-version={_opts.ApiVersion}";
        using var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        using var doc = await r.Content.ReadFromJsonAsync<JsonDocument>(ct)!;
        return doc!.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => new AdoTestPlan
            {
                Id = e.GetProperty("id").GetInt32(),
                Name = e.GetProperty("name").GetString() ?? string.Empty,
            }).ToList();
    }

    public async Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(
        int planId, CancellationToken ct = default)
    {
        var url = $"{_opts.BaseUrl}/_apis/testplan/plans/{planId}/suites?api-version={_opts.ApiVersion}";
        using var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        using var doc = await r.Content.ReadFromJsonAsync<JsonDocument>(ct)!;
        return doc!.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => new AdoTestSuite
            {
                Id = e.GetProperty("id").GetInt32(),
                Name = e.GetProperty("name").GetString() ?? string.Empty,
            }).ToList();
    }

    // ── Write: Test Case / Suite / Link ───────────────────────────────────────

    public async Task<int> CreateTestCaseAsync(
        string title, string preconditions, IReadOnlyList<TestCaseStep> steps,
        CancellationToken ct = default)
    {
        var stepsXml = BuildStepsXml(steps);
        var body = new JsonArray
        {
            Op("add", "/fields/System.Title", title),
            Op("add", "/fields/Microsoft.VSTS.TCM.Steps", stepsXml),
        };
        if (!string.IsNullOrWhiteSpace(preconditions))
        {
            // Test Case has no dedicated Preconditions field by default — store in Description.
            body.Add(Op("add", "/fields/System.Description",
                $"<div><b>Preconditions:</b> {HttpUtility.HtmlEncode(preconditions)}</div>"));
        }

        var url = $"{_opts.BaseUrl}/_apis/wit/workitems/$Test%20Case?api-version={_opts.ApiVersion}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json-patch+json");
        using var r = await _http.PostAsync(url, content, ct);
        await ThrowIfFailed(r, "Create Test Case", ct);
        using var doc = await r.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException("Empty response creating Test Case");
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task AddTestCaseToSuiteAsync(
        int planId, int suiteId, int testCaseId, CancellationToken ct = default)
    {
        // Modern `testplan` REST API expects:  [{ "workItem": { "id": "<id>" } }]
        // (The older `test` REST area used { "testCase": ... } — that was the original bug.)
        var body = new JsonArray
        {
            new JsonObject
            {
                ["workItem"] = new JsonObject { ["id"] = testCaseId.ToString() },
                ["pointAssignments"] = new JsonArray(),
            }
        };
        var url = $"{_opts.BaseUrl}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version={_opts.ApiVersion}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync(url, content, ct);
        await ThrowIfFailed(r, $"Add TC {testCaseId} to suite {suiteId}", ct);
    }

    public async Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default)
    {
        var baseUrl = $"https://dev.azure.com/{_opts.Organization}";
        var tcUrl = $"{baseUrl}/{Uri.EscapeDataString(_opts.Project)}/_apis/wit/workItems/{testCaseId}";
        var body = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/relations/-",
                ["value"] = new JsonObject
                {
                    ["rel"] = "Microsoft.VSTS.Common.TestedBy-Reverse",
                    ["url"] = tcUrl,
                },
            }
        };
        var url = $"{_opts.BaseUrl}/_apis/wit/workitems/{storyId}?api-version={_opts.ApiVersion}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json-patch+json");
        using var r = await _http.PatchAsync(url, content, ct);
        await ThrowIfFailed(r, $"Link TC {testCaseId} to story {storyId}", ct);
    }

    private static async Task ThrowIfFailed(HttpResponseMessage r, string op, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        var body = await r.Content.ReadAsStringAsync(ct);
        // Try pull ADO error message if JSON
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                throw new HttpRequestException($"ADO [{op}] {(int)r.StatusCode}: {msg.GetString()}");
        }
        catch (JsonException) { /* not JSON, fall through */ }
        throw new HttpRequestException($"ADO [{op}] {(int)r.StatusCode}: {body}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AdoWorkItem MapWorkItem(int id, JsonElement root)
    {
        var f = root.TryGetProperty("fields", out var fld) ? fld : root;
        return new AdoWorkItem
        {
            Id = id,
            Title = GetStr(f, "System.Title") ?? string.Empty,
            Description = GetStr(f, "System.Description"),
            AcceptanceCriteria = GetStr(f, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            WorkItemType = GetStr(f, "System.WorkItemType") ?? string.Empty,
            State = GetStr(f, "System.State"),
            AssignedTo = GetIdentity(f, "System.AssignedTo"),
            Url = root.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
        };
    }

    private static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetIdentity(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

    private static JsonObject Op(string op, string path, string value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

    /// <summary>Builds the XML blob ADO expects for Microsoft.VSTS.TCM.Steps.</summary>
    private static string BuildStepsXml(IReadOnlyList<TestCaseStep> steps)
    {
        var sb = new StringBuilder($"<steps id=\"0\" last=\"{steps.Count + 1}\">");
        for (int i = 0; i < steps.Count; i++)
        {
            var id = i + 2;
            var action = HttpUtility.HtmlEncode(steps[i].Action);
            var expected = HttpUtility.HtmlEncode(steps[i].Expected);
            sb.Append($"<step id=\"{id}\" type=\"ActionStep\">" +
                      $"<parameterizedString isformatted=\"true\">&lt;DIV&gt;{action}&lt;/DIV&gt;</parameterizedString>" +
                      $"<parameterizedString isformatted=\"true\">&lt;DIV&gt;{expected}&lt;/DIV&gt;</parameterizedString>" +
                      $"<description/></step>");
        }
        sb.Append("</steps>");
        return sb.ToString();
    }
}
