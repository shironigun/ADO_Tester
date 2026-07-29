using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Common;

namespace QaAutomation.Infrastructure.AzureDevOps;

/// <summary>
/// Azure DevOps REST client (PAT / Basic auth). Faithful port of the live Python <c>ado_client.py</c>
/// (spec §5.5). One instance per process (singleton); the pooled <see cref="HttpClient"/> carries auth
/// via <see cref="AdoAuthHandler"/> and a 30s timeout. Options are read per-call from
/// <see cref="IOptionsMonitor{T}"/> so a settings change applies without rebuilding the client.
/// </summary>
public sealed partial class AzureDevOpsClient : IAzureDevOpsClient
{
    public const string HttpClientName = "ado";

    private static readonly string[] WorkItemFields =
    [
        "System.Id", "System.Title", "System.Description", "System.WorkItemType",
        "System.AssignedTo", "System.State", "Microsoft.VSTS.Common.AcceptanceCriteria",
    ];

    // System.ClosedDate is omitted (TF51535 on some ADO projects); the sort tolerates closedAt=null.
    private static readonly string[] ReportFields =
    [
        "System.Id", "System.Title", "System.WorkItemType", "System.AssignedTo",
        "System.State", "System.Description", "System.History", "System.AreaPath",
        "System.CreatedBy", "System.CreatedDate",
        "Microsoft.VSTS.TCM.ReproSteps", "Microsoft.VSTS.Common.ResolvedReason",
        "Microsoft.VSTS.Common.AcceptanceCriteria", "Custom.RootCauseDetails",
    ];

    private static readonly string[] DefaultReportTypes =
        ["User Story", "Bug", "Defect", "Task", "Issue", "Feature"];

    private static readonly HashSet<string> ContainerWorkItemTypes = ["Epic", "Feature"];
    private static readonly HashSet<string> NonTestableWorkItemTypes =
        ["Test Case", "Test Plan", "Test Suite", "Shared Steps", "Shared Parameter"];

    // json-patch + ADO request bodies are serialized raw (NOT through the global camelCase options).
    // UnsafeRelaxed leaves <>&'" unescaped, matching Python's json.dumps for the HTML-bearing values.
    // The explicit reflection resolver lets ToJsonString serialize node trees that contain POCO-backed
    // JsonValues (e.g. collection-initializer arrays like `new JsonArray { str }` → JsonValueCustomized);
    // without it those throw "must specify a TypeInfoResolver" (the JsonNode writer doesn't auto-populate
    // one like JsonSerializer.Serialize does). Encoder + naming are unchanged, so wire output is identical.
    private static readonly JsonSerializerOptions RawJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private const double WiCacheTtlSeconds = 60.0;

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AzureDevOpsOptions> _options;
    private readonly ILogger<AzureDevOpsClient> _log;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<int, (double At, AdoWorkItem Wi)> _wiCache = new();
    private readonly ConcurrentDictionary<string, string> _repoIdCache = new();

    public AzureDevOpsClient(HttpClient http, IOptionsMonitor<AzureDevOpsOptions> options, ILogger<AzureDevOpsClient> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    private AzureDevOpsOptions Opts => _options.CurrentValue;

    // ── Discovery (PAT → orgs / projects / area paths) ──────────────────────

    public async Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default)
    {
        // vssps profile/me → member id, then accounts?memberId. Versions are HARDCODED preview (spec §5.5).
        using var profile = await GetDocAsync(
            "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1-preview.3",
            "Profile /me", ct);
        var memberId = GetStr(profile.RootElement, "id");
        if (string.IsNullOrEmpty(memberId))
            return [];

        using var accounts = await GetDocAsync(
            $"https://app.vssps.visualstudio.com/_apis/accounts?memberId={memberId}&api-version=7.1-preview.1",
            "Accounts (orgs)", ct);
        return Array(accounts.RootElement, "value")
            .Select(a => GetStr(a, "accountName"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    public async Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default)
    {
        // api-version is HARDCODED 7.1 here (not the configurable option) — matches Python.
        using var doc = await GetDocAsync(
            $"https://dev.azure.com/{organization}/_apis/projects?api-version=7.1&$top=200",
            $"List projects ({organization})", ct);
        return Array(doc.RootElement, "value")
            .Where(p => !string.IsNullOrEmpty(GetStr(p, "name")))
            .Select(p => new AdoNamedRef(GetStrRequired(p, "id"), GetStr(p, "name") ?? ""))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default)
    {
        // Builds the URL from scratch (org-level), api-version HARDCODED 7.1 (spec §5.5).
        using var doc = await GetDocAsync(
            $"https://dev.azure.com/{organization}/{Quote(project)}/_apis/wit/classificationnodes/Areas?$depth=10&api-version=7.1",
            $"Area paths ({project})", ct);

        var outPaths = new List<string>();
        void Walk(JsonElement node, string prefix)
        {
            var name = GetStr(node, "name") ?? "";
            var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}\\{name}";
            outPaths.Add(path);
            foreach (var child in Array(node, "children"))
                Walk(child, path);
        }
        Walk(doc.RootElement, "");
        return outPaths;
    }

    // ── reads ───────────────────────────────────────────────────────────────

    public async Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _wiCache.TryGetValue(workItemId, out var hit)
            && (_clock.Elapsed.TotalSeconds - hit.At) < WiCacheTtlSeconds)
            return hit.Wi;

        var fields = string.Join(",", WorkItemFields);
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{workItemId}?fields={fields}&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Get work item {workItemId}", ct);
        var data = doc.RootElement;
        var f = data.TryGetProperty("fields", out var fld) ? fld : data;
        var wi = new AdoWorkItem
        {
            Id = data.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : workItemId,
            Title = GetStr(f, "System.Title") ?? "",
            Description = GetStr(f, "System.Description"),
            AcceptanceCriteria = GetStr(f, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            WorkItemType = GetStr(f, "System.WorkItemType") ?? "",
            State = GetStr(f, "System.State"),
            AssignedTo = GetIdentity(f, "System.AssignedTo"),
            Url = GetStr(data, "url") ?? "",
        };
        _wiCache[workItemId] = (_clock.Elapsed.TotalSeconds, wi);
        return wi;
    }

    public async Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default)
    {
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0)
            return [];
        if (trimmed.All(char.IsDigit))
        {
            // Guard >Int32 / non-ASCII-digit inputs (int.Parse would throw uncaught) — yield [] like
            // Python's net result (an over-long numeric id 404s → AdoApiError → []).
            if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var wid))
                return [];
            try { return [await GetWorkItemAsync(wid, false, ct)]; }
            catch (AdoApiError) { return []; }
        }

        var wiql = new JsonObject
        {
            ["query"] =
                $"SELECT [System.Id] FROM WorkItems WHERE " +
                $"[System.TeamProject] = '{Opts.Project}' " +
                $"AND [System.Title] CONTAINS '{trimmed.Replace("'", "''")}' " +
                $"ORDER BY [System.ChangedDate] DESC",
        };
        var url = $"{Opts.BaseUrl}/_apis/wit/wiql?$top={top}&api-version={Opts.ApiVersion}";
        using var doc = await PostDocAsync(url, wiql, "WIQL search", ct);
        var ids = Array(doc.RootElement, "workItems")
            .Select(x => x.GetProperty("id").GetInt32()).Take(top).ToList();
        if (ids.Count == 0)
            return [];
        var result = new List<AdoWorkItem>(ids.Count);
        foreach (var id in ids)
            result.Add(await GetWorkItemAsync(id, false, ct));
        return result;
    }

    public async Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default)
    {
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{storyId}?$expand=relations&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Get work item relations {storyId}", ct);

        var ids = RelationIds(doc.RootElement, "TestedBy");
        if (ids.Count == 0)
            return [];

        var outList = new List<LinkedTestCase>();
        const string fieldsCsv = "System.Title,System.WorkItemType,Microsoft.VSTS.TCM.Steps";
        foreach (var chunk in Chunk(ids, 200))
        {
            var batchUrl = $"{Opts.BaseUrl}/_apis/wit/workitems?ids={string.Join(",", chunk)}&fields={fieldsCsv}&api-version={Opts.ApiVersion}";
            using var br = await GetDocAsync(batchUrl, "Linked test case batch fetch", ct);
            foreach (var e in Array(br.RootElement, "value"))
            {
                var f = e.GetProperty("fields");
                if ((GetStr(f, "System.WorkItemType") ?? "") != "Test Case")
                    continue;
                outList.Add(new LinkedTestCase(
                    e.GetProperty("id").GetInt32(),
                    GetStr(f, "System.Title") ?? "",
                    "",
                    ParseStepsXml(GetStr(f, "Microsoft.VSTS.TCM.Steps") ?? "")));
            }
        }
        return outList;
    }

    // ── Work-item hierarchy (Epic/Feature → child stories) ──────────────────

    private async Task<List<AdoWorkItem>> DirectChildrenAsync(int parentId, CancellationToken ct)
    {
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{parentId}?$expand=relations&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Get child relations {parentId}", ct);
        var ids = RelationIds(doc.RootElement, "Hierarchy-Forward");
        if (ids.Count == 0)
            return [];
        var outList = new List<AdoWorkItem>();
        const string fieldsCsv = "System.Title,System.WorkItemType,System.State";
        foreach (var chunk in Chunk(ids, 200))
        {
            var batchUrl = $"{Opts.BaseUrl}/_apis/wit/workitems?ids={string.Join(",", chunk)}&fields={fieldsCsv}&api-version={Opts.ApiVersion}";
            using var br = await GetDocAsync(batchUrl, "Child work item batch fetch", ct);
            foreach (var e in Array(br.RootElement, "value"))
            {
                var f = e.GetProperty("fields");
                outList.Add(new AdoWorkItem
                {
                    Id = e.GetProperty("id").GetInt32(),
                    Title = GetStr(f, "System.Title") ?? "",
                    WorkItemType = GetStr(f, "System.WorkItemType") ?? "",
                    State = GetStr(f, "System.State"),
                });
            }
        }
        return outList;
    }

    public async Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(
        int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200,
        CancellationToken ct = default)
    {
        if (!recursive)
            return (await DirectChildrenAsync(parentId, ct), false);

        var leaves = new List<AdoWorkItem>();
        var seen = new HashSet<int> { parentId };
        var truncated = false;
        var nodesFetched = 0;
        var frontier = new List<(int Id, int Depth)> { (parentId, 0) };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<(int Id, int Depth)>();
            foreach (var (pid, depth) in frontier)
            {
                var children = await DirectChildrenAsync(pid, ct);
                nodesFetched += children.Count;
                foreach (var c in children)
                {
                    if (!seen.Add(c.Id))
                        continue;
                    if (ContainerWorkItemTypes.Contains(c.WorkItemType))
                    {
                        // Descend into containers; a container is never itself a batch target.
                        if (depth + 1 < maxDepth)
                            nextFrontier.Add((c.Id, depth + 1));
                        else
                            truncated = true; // hit the depth cap with an unexpanded container
                        continue;
                    }
                    if (!NonTestableWorkItemTypes.Contains(c.WorkItemType))
                    {
                        if (leaves.Count >= maxLeaves)
                        {
                            truncated = true;
                            continue;
                        }
                        leaves.Add(c);
                    }
                    // else: a pure test artifact (Test Case/Plan/Suite) — never a batch target.
                }
                if (nodesFetched >= maxNodes)
                {
                    truncated = true;
                    nextFrontier = [];
                    break;
                }
            }
            frontier = nextFrontier;
        }
        return (leaves, truncated);
    }

    // ── Test Plans / Suites ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default)
    {
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/testplan/plans?api-version={Opts.ApiVersion}", "Get test plans", ct);
        return Array(doc.RootElement, "value")
            .Select(e => new AdoTestPlan(e.GetProperty("id").GetInt32(), GetStr(e, "name") ?? ""))
            .ToList();
    }

    public async Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default)
    {
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/testplan/plans/{planId}/suites?api-version={Opts.ApiVersion}",
            $"Get suites for plan {planId}", ct);
        return Array(doc.RootElement, "value")
            .Select(e => new AdoTestSuite(
                e.GetProperty("id").GetInt32(),
                GetStr(e, "name") ?? "",
                e.TryGetProperty("parentSuite", out var ps) && ps.ValueKind == JsonValueKind.Object
                    && ps.TryGetProperty("id", out var pid) && pid.TryGetInt32(out var p) ? p : null))
            .ToList();
    }

    /// <summary>List the Test Cases in a suite (optionally recursing into sub-suites), as parsed
    /// <see cref="LinkedTestCase"/>s. Used to read the existing coverage of a module's plan.</summary>
    public async Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(
        int planId, int suiteId, bool recurse = true, CancellationToken ct = default)
    {
        // Resolve the set of suites to read: the chosen one, plus all descendants when recursing.
        var suiteIds = new List<int> { suiteId };
        if (recurse)
        {
            var all = await GetTestSuitesAsync(planId, ct);
            var childrenByParent = all.Where(s => s.ParentId is not null)
                .GroupBy(s => s.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToList());
            var queue = new Queue<int>(new[] { suiteId });
            var seen = new HashSet<int> { suiteId };
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!childrenByParent.TryGetValue(cur, out var kids)) continue;
                foreach (var k in kids)
                    if (seen.Add(k)) { suiteIds.Add(k); queue.Enqueue(k); }
            }
        }

        // Collect distinct test-case work-item ids across those suites.
        var caseIds = new List<int>();
        var seenCase = new HashSet<int>();
        foreach (var sid in suiteIds)
        {
            using var doc = await GetDocAsync(
                $"{Opts.BaseUrl}/_apis/testplan/Plans/{planId}/Suites/{sid}/TestCase?api-version={Opts.ApiVersion}",
                $"Get test cases for suite {sid}", ct);
            foreach (var e in Array(doc.RootElement, "value"))
            {
                // testplan shape: { workItem: { id }, testCase: { id } } — accept either.
                int? id = null;
                if (e.TryGetProperty("workItem", out var wi) && wi.TryGetProperty("id", out var wid) && wid.TryGetInt32(out var w)) id = w;
                else if (e.TryGetProperty("testCase", out var tc) && tc.TryGetProperty("id", out var tcid) && tcid.TryGetInt32(out var t)) id = t;
                if (id is int cid && seenCase.Add(cid)) caseIds.Add(cid);
            }
        }
        if (caseIds.Count == 0)
            return [];

        // Batch-fetch titles + steps.
        var outList = new List<LinkedTestCase>();
        const string fieldsCsv = "System.Title,System.WorkItemType,Microsoft.VSTS.TCM.Steps";
        foreach (var chunk in Chunk(caseIds, 200))
        {
            var batchUrl = $"{Opts.BaseUrl}/_apis/wit/workitems?ids={string.Join(",", chunk)}&fields={fieldsCsv}&api-version={Opts.ApiVersion}";
            using var br = await GetDocAsync(batchUrl, "Suite test case batch fetch", ct);
            foreach (var e in Array(br.RootElement, "value"))
            {
                var f = e.GetProperty("fields");
                outList.Add(new LinkedTestCase(
                    e.GetProperty("id").GetInt32(),
                    GetStr(f, "System.Title") ?? "",
                    "",
                    ParseStepsXml(GetStr(f, "Microsoft.VSTS.TCM.Steps") ?? "")));
            }
        }
        return outList;
    }

    // ── Test Case create + suite add + Tested-By link ───────────────────────

    public async Task<int> CreateTestCaseAsync(string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default)
    {
        var body = new JsonArray
        {
            PatchOp("add", "/fields/System.Title", title),
            PatchOp("add", "/fields/Microsoft.VSTS.TCM.Steps", BuildStepsXml(steps)),
        };
        if (!string.IsNullOrWhiteSpace(preconditions))
            body.Add(PatchOp("add", "/fields/System.Description",
                $"<div><b>Preconditions:</b> {HtmlEscape(preconditions)}</div>"));

        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/$Test%20Case?api-version={Opts.ApiVersion}";
        using var doc = await SendDocAsync(HttpMethod.Post, url, body, "application/json-patch+json", "Create Test Case", ct);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task AddTestCaseToSuiteAsync(int planId, int suiteId, int testCaseId, CancellationToken ct = default)
    {
        var body = new JsonArray
        {
            new JsonObject
            {
                ["workItem"] = new JsonObject { ["id"] = testCaseId.ToString(CultureInfo.InvariantCulture) },
                ["pointAssignments"] = new JsonArray(),
            },
        };
        var url = $"{Opts.BaseUrl}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase?api-version={Opts.ApiVersion}";
        using var _ = await SendDocAsync(HttpMethod.Post, url, body, "application/json", $"Add TC {testCaseId} to suite {suiteId}", ct);
    }

    public async Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["suiteType"]   = "staticTestSuite",
            ["name"]        = name,
            ["parentSuite"] = new JsonObject { ["id"] = parentSuiteId },
        };
        var url = $"{Opts.BaseUrl}/_apis/testplan/Plans/{planId}/suites?api-version={Opts.ApiVersion}";
        using var doc = await SendDocAsync(HttpMethod.Post, url, body, "application/json", $"Create suite '{name}' under {parentSuiteId}", ct);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task LinkTestedByAsync(int storyId, int testCaseId, CancellationToken ct = default)
    {
        var tcUrl = $"https://dev.azure.com/{Opts.Organization}/{Quote(Opts.Project)}/_apis/wit/workItems/{testCaseId}";
        var body = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/relations/-",
                ["value"] = new JsonObject { ["rel"] = "Microsoft.VSTS.Common.TestedBy-Reverse", ["url"] = tcUrl },
            },
        };
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{storyId}?api-version={Opts.ApiVersion}";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, body, "application/json-patch+json", $"Link TC {testCaseId} to story {storyId}", ct);
    }

    // ── Bug create + Related link ───────────────────────────────────────────

    public async Task<int> CreateBugAsync(
        string title, string severity, string preconditions, IReadOnlyList<string> reproSteps,
        string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "",
        string foundWith = "Manual Test", CancellationToken ct = default)
    {
        // Repro field: a caller-composed HTML override (auto-filed bugs — multi-bullet observations +
        // inline attachments) wins; otherwise the frozen house anatomy: optional preconditions, env-first
        // <ul> steps, bold OBSERVATION, then a screenshot placeholder (spec §5.5 — frozen order).
        string reproField;
        if (!string.IsNullOrWhiteSpace(reproHtml))
        {
            reproField = reproHtml;
        }
        else
        {
            var repro = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(preconditions))
                repro.Append($"<div><b>Preconditions:</b> {HtmlEscape(preconditions)}</div>");
            repro.Append("<ul>");
            foreach (var s in reproSteps)
                repro.Append($"<li>{HtmlEscape(s)}</li>");
            repro.Append("</ul>");
            if (!string.IsNullOrWhiteSpace(actual))
                repro.Append("<div><b>OBSERVATION:</b></div>")
                     .Append($"<div><ul><li>{HtmlEscape(actual)}</li></ul></div>");
            repro.Append("<div><i>[Attach screenshot/GIF here]</i></div>");
            reproField = repro.ToString();
        }

        var acceptanceHtml = "";
        if (!string.IsNullOrWhiteSpace(expected))
            acceptanceHtml = "<div>I know this bug is resolved when:</div>" +
                             $"<div><ul><li>{HtmlEscape(expected)}</li></ul></div>";

        var sysInfo = "";
        if (!string.IsNullOrWhiteSpace(suggestedFix))
            sysInfo = $"<div><b>Suggested fix:</b> {HtmlEscape(suggestedFix)}</div>";
        // Optional pre-built HTML block (e.g. automation/triage traceability) — the caller escapes it.
        // Empty for manual bugs, so their SystemInfo stays byte-identical (golden parity preserved).
        if (!string.IsNullOrWhiteSpace(systemInfo))
            sysInfo += systemInfo;

        var sevValue = severity switch
        {
            "Critical" => "1 - Critical",
            "High" => "2 - High",
            "Medium" => "3 - Medium",
            "Low" => "4 - Low",
            _ => "3 - Medium",
        };

        // Frozen json-patch op ORDER (spec §5.5).
        var body = new JsonArray
        {
            PatchOp("add", "/fields/System.Title", title),
            PatchOp("add", "/fields/Microsoft.VSTS.TCM.ReproSteps", reproField),
            PatchOp("add", "/fields/Microsoft.VSTS.Common.Severity", sevValue),
            PatchOp("add", "/fields/System.AreaPath", Opts.BugAreaPath),
            PatchOp("add", "/fields/Microsoft.VSTS.Common.Priority", 2),
            PatchOp("add", "/fields/Microsoft.VSTS.Common.ValueArea", "Business"),
            PatchOp("add", "/fields/Custom.QA", Opts.QaOwner),
            PatchOp("add", "/fields/Custom.FoundWith", foundWith),
        };
        if (acceptanceHtml.Length > 0)
            body.Add(PatchOp("add", "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", acceptanceHtml));
        if (sysInfo.Length > 0)
            body.Add(PatchOp("add", "/fields/Microsoft.VSTS.TCM.SystemInfo", sysInfo));

        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/$Bug?api-version={Opts.ApiVersion}";
        using var doc = await SendDocAsync(HttpMethod.Post, url, body, "application/json-patch+json", "Create Bug", ct);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default)
    {
        var toUrl = $"https://dev.azure.com/{Opts.Organization}/{Quote(Opts.Project)}/_apis/wit/workItems/{toId}";
        var body = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/relations/-",
                ["value"] = new JsonObject { ["rel"] = "System.LinkTypes.Related", ["url"] = toUrl },
            },
        };
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{fromId}?api-version={Opts.ApiVersion}";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, body, "application/json-patch+json", $"Link {fromId} <-> {toId} as Related", ct);
    }

    public async Task LinkParentAsync(int childId, int parentId, CancellationToken ct = default)
    {
        var parentUrl = $"https://dev.azure.com/{Opts.Organization}/{Quote(Opts.Project)}/_apis/wit/workItems/{parentId}";
        var body = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = "/relations/-",
                ["value"] = new JsonObject { ["rel"] = "System.LinkTypes.Hierarchy-Reverse", ["url"] = parentUrl },
            },
        };
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{childId}?api-version={Opts.ApiVersion}";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, body, "application/json-patch+json", $"Link {childId} under parent {parentId}", ct);
    }

    public async Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default)
    {
        var safeName = Uri.EscapeDataString(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName);
        var url = $"{Opts.BaseUrl}/_apis/wit/attachments?fileName={safeName}&api-version={Opts.ApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(content) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var r = await _http.SendAsync(req, ct);
        await EnsureOkAsync(r, $"Upload attachment {fileName}", ct);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct) is { Length: > 0 } s ? s : "{}");
        return doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
    }

    public async Task AddWorkItemAttachmentLinkAsync(
        int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default)
    {
        var rel = new JsonObject { ["rel"] = "AttachedFile", ["url"] = attachmentUrl };
        if (!string.IsNullOrWhiteSpace(comment))
            rel["attributes"] = new JsonObject { ["comment"] = comment };
        var body = new JsonArray
        {
            new JsonObject { ["op"] = "add", ["path"] = "/relations/-", ["value"] = rel },
        };
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{workItemId}?api-version={Opts.ApiVersion}";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, body, "application/json-patch+json", $"Attach file to WI {workItemId}", ct);
    }

    // ── Reports query + enrichment ──────────────────────────────────────────

    public async Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards,
        IReadOnlyList<string>? types = null, CancellationToken ct = default)
    {
        // [System.ChangedDate] is date-precision in WIQL — supplying a time fails. Use date-only.
        var fromS = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toS = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string Esc(string v) => v.Replace("'", "''");
        var areaClause = "";
        if (boards.Count > 0)
            areaClause = " AND (" + string.Join(" OR ", boards.Select(b => $"[System.AreaPath] UNDER '{Esc(b)}'")) + ")";

        var chosen = (types ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (chosen.Count == 0)
            chosen = DefaultReportTypes.ToList();
        var typesCsv = string.Join(", ", chosen.Select(t => $"'{Esc(t)}'"));

        var wiql = new JsonObject
        {
            ["query"] =
                $"SELECT [System.Id] FROM WorkItems " +
                $"WHERE [System.TeamProject] = '{Opts.Project}' " +
                $"AND [System.ChangedDate] >= '{fromS}' " +
                $"AND [System.ChangedDate] <= '{toS}' " +
                $"AND [System.WorkItemType] IN ({typesCsv}) " +
                $"{areaClause}" +
                $" ORDER BY [System.ChangedDate] DESC",
        };

        using var doc = await PostDocAsync(
            $"{Opts.BaseUrl}/_apis/wit/wiql?api-version={Opts.ApiVersion}", wiql, "Report WIQL", ct);
        var ids = Array(doc.RootElement, "workItems").Select(x => x.GetProperty("id").GetInt32()).ToList();
        if (ids.Count == 0)
            return [];

        var fieldsCsv = string.Join(",", ReportFields);
        var results = new List<ReportTicket>(ids.Count);
        foreach (var chunk in Chunk(ids, 200))
        {
            var batchUrl = $"{Opts.BaseUrl}/_apis/wit/workitems?ids={string.Join(",", chunk)}&fields={fieldsCsv}&api-version={Opts.ApiVersion}";
            using var br = await GetDocAsync(batchUrl, "Report batch fetch", ct);
            foreach (var e in Array(br.RootElement, "value"))
            {
                var f = e.GetProperty("fields");
                var repro = GetStr(f, "Microsoft.VSTS.TCM.ReproSteps");
                var desc = GetStr(f, "System.Description");
                var resolved = GetStr(f, "Microsoft.VSTS.Common.ResolvedReason");
                var history = GetStr(f, "System.History");
                results.Add(new ReportTicket
                {
                    Id = e.GetProperty("id").GetInt32(),
                    Title = GetStr(f, "System.Title") ?? "",
                    WorkItemType = GetStr(f, "System.WorkItemType") ?? "",
                    State = GetStr(f, "System.State"),
                    AssignedTo = GetIdentity(f, "System.AssignedTo"),
                    IssueDescription = !string.IsNullOrEmpty(repro) ? repro : desc,
                    Fix = !string.IsNullOrEmpty(resolved) ? resolved : history,
                    ClosedAt = GetStr(f, "System.ClosedDate"),
                    CreatedBy = GetIdentity(f, "System.CreatedBy"),
                    CreatedDate = GetStr(f, "System.CreatedDate"),
                    Description = !string.IsNullOrEmpty(desc) ? desc : repro,
                    RootCause = GetStr(f, "Custom.RootCauseDetails"),
                    AreaPath = GetStr(f, "System.AreaPath"),
                });
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default)
    {
        // api-version HARDCODED 7.0-preview.3; camelCase /workItems. Best-effort — [] on any failure.
        var url = $"{Opts.BaseUrl}/_apis/wit/workItems/{workItemId}/comments?api-version=7.0-preview.3";
        JsonDocument doc;
        try
        {
            using var r = await _http.GetAsync(url, ct);
            r.EnsureSuccessStatusCode();
            doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception) { return []; }

        using (doc)
        {
            var outList = new List<TicketComment>();
            foreach (var c in Array(doc.RootElement, "comments"))
            {
                var text = GetStr(c, "text") ?? GetStr(c, "renderedText") ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                outList.Add(new TicketComment(
                    GetIdentity(c, "createdBy") ?? "Unknown", GetStr(c, "createdDate"), text));
            }
            return outList.OrderBy(x => x.Date ?? "", StringComparer.Ordinal).ToList();
        }
    }

    public async Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default)
    {
        // api-version HARDCODED 7.0; camelCase /workItems. Best-effort — [] on any failure.
        var url = $"{Opts.BaseUrl}/_apis/wit/workItems/{workItemId}/updates?api-version=7.0";
        JsonDocument doc;
        try
        {
            using var r = await _http.GetAsync(url, ct);
            r.EnsureSuccessStatusCode();
            doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception) { return []; }

        using (doc)
        {
            var events = new List<TimelineEvent>();
            foreach (var u in Array(doc.RootElement, "value"))
            {
                var fields = u.TryGetProperty("fields", out var fl) && fl.ValueKind == JsonValueKind.Object ? fl : default;
                var actor = GetIdentity(u, "revisedBy") ?? "Unknown";
                var date = GetStr(u, "revisedDate");

                if (fields.ValueKind == JsonValueKind.Object)
                {
                    if (fields.TryGetProperty("System.CreatedDate", out var created) && created.ValueKind == JsonValueKind.Object
                        && created.TryGetProperty("newValue", out var cdNew) && !string.IsNullOrEmpty(AsText(cdNew))
                        && !created.TryGetProperty("oldValue", out _))
                    {
                        string? who = null;
                        if (fields.TryGetProperty("System.CreatedBy", out var cb) && cb.ValueKind == JsonValueKind.Object
                            && cb.TryGetProperty("newValue", out var cbNew) && cbNew.ValueKind == JsonValueKind.Object)
                            who = GetStr(cbNew, "displayName");
                        events.Add(new TimelineEvent(GetStr(created, "newValue") ?? date, who ?? actor, "created", "Work item created"));
                    }

                    if (fields.TryGetProperty("System.State", out var st) && st.ValueKind == JsonValueKind.Object
                        && st.TryGetProperty("newValue", out var stNew))
                    {
                        var newV = AsText(stNew);
                        if (!string.IsNullOrEmpty(newV))   // Python gates the state event on newValue being truthy
                        {
                            var oldV = st.TryGetProperty("oldValue", out var ov) ? AsText(ov) : null;
                            var detail = !string.IsNullOrEmpty(oldV) ? $"{oldV} → {newV}" : $"Set to {newV}";
                            events.Add(new TimelineEvent(date, actor, "state", $"State {detail}"));
                        }
                    }

                    if (fields.TryGetProperty("System.AssignedTo", out var asg) && asg.ValueKind == JsonValueKind.Object
                        && (asg.TryGetProperty("newValue", out _) || asg.TryGetProperty("oldValue", out _)))
                    {
                        string? old = asg.TryGetProperty("oldValue", out var ov) && ov.ValueKind == JsonValueKind.Object ? GetStr(ov, "displayName") : null;
                        string? nw = asg.TryGetProperty("newValue", out var nv) && nv.ValueKind == JsonValueKind.Object ? GetStr(nv, "displayName") : null;
                        events.Add(new TimelineEvent(date, actor, "assignment", $"Assigned to {old ?? "Unassigned"} → {nw ?? "Unassigned"}"));
                    }
                }
            }
            return events;
        }
    }

    public async Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(
        IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default)
    {
        var target = tickets.Take(limit).ToList();
        if (tickets.Count > limit)
            _log.LogWarning("Report enrichment capped at {Limit} of {Total} tickets", limit, tickets.Count);

        using var sem = new SemaphoreSlim(concurrency);
        async Task One(ReportTicket t)
        {
            await sem.WaitAsync(ct);
            IReadOnlyList<TicketComment> comments;
            IReadOnlyList<TimelineEvent> updates;
            try
            {
                var commentsTask = GetWorkItemCommentsAsync(t.Id, ct);
                var updatesTask = GetWorkItemUpdatesAsync(t.Id, ct);
                await Task.WhenAll(commentsTask, updatesTask);
                comments = commentsTask.Result;
                updates = updatesTask.Result;
            }
            finally { sem.Release(); }

            t.Comments = comments.ToList();
            var timeline = updates.ToList();
            foreach (var c in comments)
                timeline.Add(new TimelineEvent(c.Date, c.Author, "comment", "Comment added"));
            t.Timeline = timeline.OrderBy(e => e.Date ?? "", StringComparer.Ordinal).ToList();
        }

        await Task.WhenAll(target.Select(One));
        return tickets;
    }

    // ── Git repo reads + Code Search ────────────────────────────────────────

    public async Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default)
    {
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/git/repositories?api-version={Opts.ApiVersion}", "List repositories", ct);
        return Array(doc.RootElement, "value")
            .Select(e => new AdoNamedRef(GetStrRequired(e, "id"), GetStr(e, "name") ?? ""))
            .ToList();
    }

    public async Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default)
    {
        var repoName = (name ?? Opts.Repository ?? "").Trim();
        if (repoName.Length == 0)
            throw new AdoApiError("Resolve repo", 400, "No repository configured (ado.repository).");
        if (_repoIdCache.TryGetValue(repoName.ToLowerInvariant(), out var cached))
            return cached;
        foreach (var repo in await ListRepositoriesAsync(ct))
            _repoIdCache[repo.Name.ToLowerInvariant()] = repo.Id;
        if (!_repoIdCache.TryGetValue(repoName.ToLowerInvariant(), out var rid))
            throw new AdoApiError("Resolve repo", 404, $"Repository '{repoName}' not found.");
        return rid;
    }

    public async Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default)
    {
        var rid = await ResolveRepositoryIdAsync(repository, ct);
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/refs?filter=heads/&api-version={Opts.ApiVersion}",
            "List branches", ct);
        return Array(doc.RootElement, "value")
            .Select(e => GetStr(e, "name") ?? "")               // "refs/heads/feature/x"
            .Where(n => n.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(n => n["refs/heads/".Length..])
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<GitRefs> GetWorkItemGitRefsAsync(int workItemId, CancellationToken ct = default)
    {
        var url = $"{Opts.BaseUrl}/_apis/wit/workitems/{workItemId}?$expand=relations&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Get work item relations {workItemId}", ct);
        var prIds = new List<string>();
        var commitShas = new List<string>();
        foreach (var rel in Array(doc.RootElement, "relations"))
        {
            if ((GetStr(rel, "rel") ?? "") != "ArtifactLink")
                continue;
            var raw = Uri.UnescapeDataString(GetStr(rel, "url") ?? "");
            if (raw.Contains("/Git/PullRequestId/", StringComparison.Ordinal))
                prIds.Add(raw[(raw.LastIndexOf('/') + 1)..]);
            else if (raw.Contains("/Git/Commit/", StringComparison.Ordinal))
                commitShas.Add(raw[(raw.LastIndexOf('/') + 1)..]);
        }
        return new GitRefs(prIds, commitShas);
    }

    public async Task<IReadOnlyList<LinkedChange>> GetLinkedChangesAsync(int workItemId, CancellationToken ct = default)
    {
        GitRefs refs;
        try { refs = await GetWorkItemGitRefsAsync(workItemId, ct); }
        catch (AdoApiError) { return []; }
        if (refs.PullRequestIds.Count == 0 && refs.CommitShas.Count == 0)
            return [];

        string? rid;
        try { rid = await ResolveRepositoryIdAsync(null, ct); }
        catch (AdoApiError) { rid = null; }
        var repo = Quote(Opts.Repository);
        var outList = new List<LinkedChange>();

        foreach (var pr in refs.PullRequestIds)
        {
            var title = $"Pull Request #{pr}";
            if (rid is not null)
            {
                try
                {
                    using var r = await _http.GetAsync(
                        $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/pullRequests/{pr}?api-version={Opts.ApiVersion}", ct);
                    if ((int)r.StatusCode < 400)
                    {
                        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                        var t = (GetStr(d.RootElement, "title") ?? "").Trim();
                        if (t.Length > 0) title = t;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Best-effort (matches Python's `except httpx.HTTPError`): swallow timeouts/transport
                    // errors and keep the id-only label; still honor genuine caller cancellation.
                }
            }
            outList.Add(new LinkedChange("pr", pr, title, $"{Opts.BaseUrl}/_git/{repo}/pullrequest/{pr}"));
        }

        foreach (var sha in refs.CommitShas)
        {
            var title = $"Commit {(sha.Length >= 8 ? sha[..8] : sha)}";
            if (rid is not null)
            {
                try
                {
                    using var r = await _http.GetAsync(
                        $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/commits/{sha}?api-version={Opts.ApiVersion}", ct);
                    if ((int)r.StatusCode < 400)
                    {
                        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                        var msg = (GetStr(d.RootElement, "comment") ?? "").Trim();
                        if (msg.Length > 0)
                        {
                            var firstLine = msg.Split('\n', '\r')[0];   // splitlines() semantics — drop trailing \r under CRLF
                            title = firstLine.Length > 120 ? firstLine[..120] : firstLine;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Best-effort (matches Python's `except httpx.HTTPError`): swallow timeouts/transport
                    // errors and keep the id-only label; still honor genuine caller cancellation.
                }
            }
            outList.Add(new LinkedChange("commit", sha, title, $"{Opts.BaseUrl}/_git/{repo}/commit/{sha}"));
        }
        return outList;
    }

    public async Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(string prId, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        var changes = await PrLatestIterationChangesAsync(rid, prId, ct);
        return changes is null ? [] : ChangePaths(changes.Value);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetPullRequestDiffAsync(string prId, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        var changes = await PrLatestIterationChangesAsync(rid, prId, ct);
        return changes is null ? [] : ChangeEntries(changes.Value);
    }

    /// <summary>PR latest-iteration "changeEntries" array (or null if no iterations).</summary>
    private async Task<JsonElement?> PrLatestIterationChangesAsync(string rid, string prId, CancellationToken ct)
    {
        var baseUrl = $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/pullRequests/{prId}";
        using var itDoc = await GetDocAsync($"{baseUrl}/iterations?api-version={Opts.ApiVersion}", $"PR {prId} iterations", ct);
        var iters = Array(itDoc.RootElement, "value").ToList();
        if (iters.Count == 0)
            return null;
        var last = iters.Max(i => i.GetProperty("id").GetInt32());
        using var chDoc = await GetDocAsync($"{baseUrl}/iterations/{last}/changes?api-version={Opts.ApiVersion}", $"PR {prId} changes", ct);
        // PR changes live under "changeEntries" (distinct from commit "changes").
        return chDoc.RootElement.TryGetProperty("changeEntries", out var ce) && ce.ValueKind == JsonValueKind.Array
            ? ce.Clone() : (JsonElement?)JsonDocument.Parse("[]").RootElement.Clone();
    }

    public async Task<IReadOnlyList<string>> GetCommitChangedFilesAsync(string sha, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/commits/{sha}/changes?api-version={Opts.ApiVersion}",
            $"Commit {sha} changes", ct);
        return doc.RootElement.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? ChangePaths(ch) : [];
    }

    public async Task<IReadOnlyList<ChangedFile>> GetCommitDiffAsync(string sha, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        using var doc = await GetDocAsync(
            $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/commits/{sha}/changes?api-version={Opts.ApiVersion}",
            $"Commit {sha} diff", ct);
        return doc.RootElement.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? ChangeEntries(ch) : [];
    }

    public async Task<IReadOnlyList<string>> GetCommitsForPathAsync(string path, int top = 5, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        var url = $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/commits" +
                  $"?searchCriteria.itemPath={Quote(path)}&searchCriteria.$top={top}&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Commits for {path}", ct);
        return Array(doc.RootElement, "value")
            .Select(c => GetStr(c, "commitId"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
    }

    public async Task<string> GetFileContentAsync(string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        var version = @ref ?? Opts.DefaultBranch;
        var url = $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/items" +
                  $"?path={Quote(path)}&includeContent=true" +
                  $"&versionDescriptor.version={Quote(version)}&versionDescriptor.versionType={versionType}" +
                  $"&api-version={Opts.ApiVersion}";
        using var r = await _http.GetAsync(url, ct);
        await EnsureOkAsync(r, $"Get file {path}", ct);
        var raw = await r.Content.ReadAsStringAsync(ct);
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        }
        catch (JsonException) { return raw; }  // some responses return raw text
    }

    public async Task<IReadOnlyList<string>> ListRepoFilePathsAsync(
        string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default)
    {
        var rid = repoId ?? await ResolveRepositoryIdAsync(null, ct);
        var version = string.IsNullOrWhiteSpace(branch) ? Opts.DefaultBranch : branch!;
        var scope = string.IsNullOrWhiteSpace(scopePath) ? "/" : (scopePath.StartsWith('/') ? scopePath : "/" + scopePath);
        var url = $"{Opts.BaseUrl}/_apis/git/repositories/{rid}/items" +
                  $"?scopePath={Quote(scope)}&recursionLevel=Full" +
                  $"&versionDescriptor.version={Quote(version)}&versionDescriptor.versionType=branch" +
                  $"&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"List items {scope}", ct);
        var paths = new List<string>();
        foreach (var item in Array(doc.RootElement, "value"))
        {
            if (item.TryGetProperty("isFolder", out var f) && f.ValueKind == JsonValueKind.True)
                continue;
            var p = GetStr(item, "path");
            if (!string.IsNullOrEmpty(p))
                paths.Add(p!.TrimStart('/'));
        }
        return paths;
    }

    public async Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default)
    {
        var org = Opts.Organization.Trim('/');
        var url = $"https://almsearch.dev.azure.com/{org}/{Quote(Opts.Project)}/_apis/search/codesearchresults?api-version={Opts.ApiVersion}";
        var body = new JsonObject
        {
            ["searchText"] = text,
            ["$top"] = top,
            ["filters"] = new JsonObject
            {
                ["Project"] = new JsonArray { Opts.Project },
                ["Repository"] = new JsonArray { Opts.Repository },
            },
        };
        using var doc = await SendDocAsync(HttpMethod.Post, url, body, "application/json", "Code Search", ct);
        return Array(doc.RootElement, "results")
            .Select(res => GetStr(res, "path"))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    // ── HTTP + parsing helpers ──────────────────────────────────────────────

    private async Task<JsonDocument> GetDocAsync(string url, string op, CancellationToken ct)
    {
        using var r = await _http.GetAsync(url, ct);
        await EnsureOkAsync(r, op, ct);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct) is { Length: > 0 } s ? s : "{}");
    }

    // ── ADO Test Run lifecycle (QAL-027) ─────────────────────────────────────

    public async Task<int> CreateAdoTestRunAsync(int planId, int suiteId, string name,
        IReadOnlyList<int> pointIds, CancellationToken ct = default)
    {
        // Include pointIds so ADO auto-creates one result per point (outcome unset). We then PATCH
        // those results with final outcomes via UpdateAdoTestResultsAsync. (POSTing fresh results to
        // a pointId-less run is rejected with 400 — the results must be created by ADO from points.)
        var body = new JsonObject
        {
            ["name"]     = name,
            ["plan"]     = new JsonObject { ["id"] = planId.ToString(CultureInfo.InvariantCulture) },
            ["pointIds"] = new JsonArray(pointIds.Select(id => JsonValue.Create(id)).ToArray<JsonNode?>()),
        };
        var url = $"{Opts.BaseUrl}/_apis/test/runs?api-version=7.0";
        using var doc = await PostDocAsync(url, body, "Create ADO Test Run", ct);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task<IReadOnlyDictionary<int, int>> GetAdoTestResultIdsAsync(int adoTestRunId, CancellationToken ct = default)
    {
        var url = $"{Opts.BaseUrl}/_apis/test/Runs/{adoTestRunId}/results?api-version=7.0";
        using var doc = await GetDocAsync(url, $"Get ADO test results (run {adoTestRunId})", ct);
        var map = new Dictionary<int, int>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var r in arr.EnumerateArray())
            {
                var resultId = r.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.Number
                    ? idP.GetInt32() : 0;
                var ptId = 0;
                if (r.TryGetProperty("testPoint", out var tp) && tp.TryGetProperty("id", out var tpId))
                    ptId = tpId.ValueKind == JsonValueKind.Number ? tpId.GetInt32()
                         : int.TryParse(tpId.GetString(), out var v) ? v : 0;
                if (resultId > 0 && ptId > 0) map[ptId] = resultId;
            }
        return map;
    }

    public async Task UpdateAdoTestResultsAsync(int adoTestRunId, IReadOnlyList<AdoTestResultUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;
        var arr = new JsonArray();
        foreach (var u in updates)
        {
            var o = new JsonObject
            {
                ["id"]           = u.ResultId,
                ["outcome"]      = u.Outcome,
                ["state"]        = "Completed",
                ["durationInMs"] = u.DurationMs,
            };
            if (!string.IsNullOrEmpty(u.ErrorMessage))
                o["errorMessage"] = u.ErrorMessage;
            arr.Add(o);
        }
        var url = $"{Opts.BaseUrl}/_apis/test/Runs/{adoTestRunId}/results?api-version=7.0";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, arr, "application/json",
            $"Update ADO test results (run {adoTestRunId})", ct);
    }

    public async Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["state"] = "Completed" };
        var url = $"{Opts.BaseUrl}/_apis/test/runs/{adoTestRunId}?api-version=7.0";
        using var _ = await SendDocAsync(HttpMethod.Patch, url, body, "application/json",
            $"Complete ADO test run {adoTestRunId}", ct);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(
        int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default)
    {
        // isRecursive=true makes ADO return test points from child suites too, which is needed
        // when the session's TestSuiteId is a parent/folder suite (e.g. COWORK 173473) but the
        // actual test cases live in level child-suites (175537 → 175538/539/540).
        // We filter by testCaseIds in-memory rather than via a testCaseId= query param to avoid
        // building a multi-KB URL (a large push can have hundreds of ids) that risks a 404/414.
        var wanted = testCaseIds.ToHashSet();
        var url = $"{Opts.BaseUrl}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestPoint" +
                  $"?isRecursive=true&api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, "Get test points", ct);
        var map = new Dictionary<int, int>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var pt in arr.EnumerateArray())
            {
                var tcId = pt.TryGetProperty("testCaseReference", out var tcr)
                    ? (tcr.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0)
                    : 0;
                var ptId = pt.TryGetProperty("id", out var ptProp) ? ptProp.GetInt32() : 0;
                if (tcId > 0 && ptId > 0 && (wanted.Count == 0 || wanted.Contains(tcId)))
                    map[tcId] = ptId;
            }
        return map;
    }

    // ── Build / pipeline reads (QAL-030) ────────────────────────────────────

    public async Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(
        int pipelineId, int? minBuildId = null, CancellationToken ct = default)
    {
        // NOTE: /build/builds has NO minBuildId query parameter — ADO silently ignores unknown
        // params, so the old "&minBuildId=N" filtered nothing and every poll returned the full
        // history (the pipeline watcher then re-ran runs for long-past executions). Filter
        // client-side instead; $top bounds the fetch to the recent window.
        var url = $"{Opts.BaseUrl}/_apis/build/builds?definitions={pipelineId}" +
                  "&statusFilter=completed&resultFilter=succeeded&queryOrder=finishTimeDescending" +
                  $"&$top=50&api-version={Opts.ApiVersion}";

        using var doc = await GetDocAsync(url, $"Get pipeline builds {pipelineId}", ct);
        var builds = new List<AdoBuild>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var b in arr.EnumerateArray())
            {
                var id  = b.TryGetProperty("id", out var idP) ? idP.GetInt32() : 0;
                var sha = b.TryGetProperty("sourceVersion", out var svP) ? svP.GetString() ?? "" : "";
                var repoId = b.TryGetProperty("repository", out var rp)
                    ? (rp.TryGetProperty("id", out var rIdP) ? rIdP.GetString() ?? "" : "") : "";
                var finished = b.TryGetProperty("finishTime", out var ftP)
                    ? DateTime.Parse(ftP.GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : DateTime.UtcNow;
                if (id > 0 && (minBuildId is null || id > minBuildId.Value))
                    builds.Add(new AdoBuild(id, sha, repoId, finished));
            }
        return builds;
    }

    public async Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(
        int buildId, CancellationToken ct = default)
    {
        var url = $"{Opts.BaseUrl}/_apis/build/builds/{buildId}/changes?api-version={Opts.ApiVersion}";
        using var doc = await GetDocAsync(url, $"Get build changes {buildId}", ct);
        var changes = new List<AdoBuildChange>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var c in arr.EnumerateArray())
            {
                var sha = c.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
                var msg = c.TryGetProperty("message", out var mP) ? mP.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(sha)) changes.Add(new AdoBuildChange(sha, msg));
            }
        return changes;
    }

    private Task<JsonDocument> PostDocAsync(string url, JsonNode body, string op, CancellationToken ct) =>
        SendDocAsync(HttpMethod.Post, url, body, "application/json", op, ct);

    private async Task<JsonDocument> SendDocAsync(HttpMethod method, string url, JsonNode body, string contentType, string op, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToJsonString(RawJson), Encoding.UTF8, contentType),
        };
        using var r = await _http.SendAsync(req, ct);
        await EnsureOkAsync(r, op, ct);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct) is { Length: > 0 } s ? s : "{}");
    }

    /// <summary>Mirrors Python <c>_raise_if_failed</c>: sanitize the body BEFORE parsing, unwrap a
    /// <c>{ "message": ... }</c> envelope, and raise a catchable <see cref="AdoApiError"/>.</summary>
    private async Task EnsureOkAsync(HttpResponseMessage r, string op, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode)
            return;
        var raw = await r.Content.ReadAsStringAsync(ct);
        var body = Sanitizer.Sanitize(raw, Opts.PersonalAccessToken);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m))
                throw new AdoApiError(op, (int)r.StatusCode, m.GetString() ?? "");
        }
        catch (JsonException) { /* not JSON — fall through */ }
        throw new AdoApiError(op, (int)r.StatusCode, body);
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetIdentity(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;

    /// <summary>Python f-string stringification: string as-is, null/undefined → null, other kinds → raw
    /// text (mirrors how the updates timeline interpolates raw newValue/oldValue).</summary>
    private static string? AsText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    /// <summary>Fail-fast read of a required field (mirrors Python's direct <c>d["id"]</c> subscript:
    /// raises rather than silently yielding an empty id that would corrupt the repo-id cache / URLs).</summary>
    private static string GetStrRequired(JsonElement e, string key) =>
        (e.TryGetProperty(key, out var v) ? AsText(v) : null)
        ?? throw new KeyNotFoundException($"Expected field '{key}' missing in ADO response.");

    /// <summary>Linked work-item ids whose relation type contains <paramref name="relSubstring"/>,
    /// de-duped preserving order (mirrors Python's <c>dict.fromkeys</c>).</summary>
    private static List<int> RelationIds(JsonElement root, string relSubstring)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var rel in Array(root, "relations"))
        {
            if (!(GetStr(rel, "rel") ?? "").Contains(relSubstring, StringComparison.Ordinal))
                continue;
            var m = WorkItemUrlRegex().Match(GetStr(rel, "url") ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>File paths from PR/commit change entries — skips folders and deletes (Python _change_paths).</summary>
    private static List<string> ChangePaths(JsonElement changes)
    {
        var outList = new List<string>();
        foreach (var c in changes.EnumerateArray())
        {
            var item = c.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? it : default;
            if (IsFolder(item))
                continue;
            if (string.Equals(GetStr(c, "changeType"), "delete", StringComparison.OrdinalIgnoreCase))
                continue;
            var p = item.ValueKind == JsonValueKind.Object ? GetStr(item, "path") : null;
            if (!string.IsNullOrEmpty(p))
                outList.Add(p);
        }
        return outList;
    }

    /// <summary>Like ChangePaths but keeps change type and deletes, de-duped by path (Python _change_entries).</summary>
    private static List<ChangedFile> ChangeEntries(JsonElement changes)
    {
        var outList = new List<ChangedFile>();
        var seen = new HashSet<string>();
        foreach (var c in changes.EnumerateArray())
        {
            var item = c.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? it : default;
            if (IsFolder(item))
                continue;
            var p = item.ValueKind == JsonValueKind.Object ? GetStr(item, "path") : null;
            if (!string.IsNullOrEmpty(p) && seen.Add(p))
                outList.Add(new ChangedFile(p, (GetStr(c, "changeType") ?? "").ToLowerInvariant()));
        }
        return outList;
    }

    private static bool IsFolder(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object &&
        ((item.TryGetProperty("isFolder", out var fol) && fol.ValueKind == JsonValueKind.True) ||
         string.Equals(GetStr(item, "gitObjectType"), "tree", StringComparison.Ordinal));

    private static IEnumerable<List<int>> Chunk(List<int> ids, int size)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.GetRange(i, Math.Min(size, ids.Count - i));
    }

    /// <summary>Python quote(safe='/') — URL-encode each path segment but keep the slashes.</summary>
    private static string Quote(string s) => string.Join("/", s.Split('/').Select(Uri.EscapeDataString));

    private static JsonObject PatchOp(string op, string path, string value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

    private static JsonObject PatchOp(string op, string path, int value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

    /// <summary>Python <c>html.escape(x, quote=True)</c>: &amp; &lt; &gt; then " and ' (order matters).</summary>
    internal static string HtmlEscape(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#x27;");
    }

    /// <summary>Microsoft.VSTS.TCM.Steps XML — byte-identical to the Python <c>_build_steps_xml</c>.</summary>
    internal static string BuildStepsXml(IReadOnlyList<TestCaseStep> steps)
    {
        var sb = new StringBuilder();
        sb.Append($"<steps id=\"0\" last=\"{steps.Count + 1}\">");
        for (var i = 0; i < steps.Count; i++)
        {
            var stepId = i + 2;
            var action = HtmlEscape(steps[i].Action);
            var expected = HtmlEscape(steps[i].Expected);
            sb.Append($"<step id=\"{stepId}\" type=\"ActionStep\">")
              .Append($"<parameterizedString isformatted=\"true\">&lt;DIV&gt;{action}&lt;/DIV&gt;</parameterizedString>")
              .Append($"<parameterizedString isformatted=\"true\">&lt;DIV&gt;{expected}&lt;/DIV&gt;</parameterizedString>")
              .Append("<description/></step>");
        }
        sb.Append("</steps>");
        return sb.ToString();
    }

    /// <summary>Inverse of <see cref="BuildStepsXml"/>: Steps XML → action/expected pairs (Python _parse_steps_xml).</summary>
    internal static List<TestCaseStep> ParseStepsXml(string xml)
    {
        var steps = new List<TestCaseStep>();
        if (string.IsNullOrEmpty(xml) || !xml.Contains("<step", StringComparison.Ordinal))
            return steps;
        XDocument root;
        try { root = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return steps; }
        foreach (var step in root.Descendants("step"))
        {
            var ps = step.Elements("parameterizedString").ToList();
            if (ps.Count < 2)
                continue;
            var action = StripTags(WebDecode(ps[0].Value));
            var expected = StripTags(WebDecode(ps[1].Value));
            if (action.Length > 0 || expected.Length > 0)
                steps.Add(new TestCaseStep { Action = action, Expected = expected });
        }
        return steps;
    }

    private static string WebDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    private static string StripTags(string s) =>
        TagRegex().Replace(s ?? "", " ").Replace("&nbsp;", " ").Trim();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"/work[iI]tems?/(\d+)\b")]
    private static partial Regex WorkItemUrlRegex();
}
