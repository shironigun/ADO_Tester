using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Notifications;

/// <summary>
/// Posts Teams adaptive-card notifications on QA Loop run completion and triage/file-bugs.
/// Singleton. Empty webhook → logs and returns silently. Cancelled runs never notify.
/// </summary>
public sealed class TeamsNotifier : ITeamsNotifier
{
    private static readonly string CompactTemplate  = LoadTemplate("teams_summary_compact.json.template");
    private static readonly string DeveloperTemplate = LoadTemplate("teams_summary_developer.json.template");
    private static readonly string ScheduledTemplate = LoadTemplate("teams_summary_scheduled.json.template");

    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<QaLoopOptions> _opts;
    private readonly ILogger<TeamsNotifier> _log;

    public TeamsNotifier(IHttpClientFactory http, IOptionsMonitor<QaLoopOptions> opts, ILogger<TeamsNotifier> log)
    {
        _http = http;
        _opts = opts;
        _log = log;
    }

    public async Task NotifyRunCompleteAsync(TestRun run, CancellationToken ct = default)
    {
        if (run.Status == TestRunStatus.Cancelled) return;
        var opts = _opts.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.TeamsWebhookUrl))
        {
            _log.LogDebug("Teams webhook not configured — skipping run-complete notification.");
            return;
        }

        var passed = run.Passed;
        var failed = run.Failed;
        var duration = FormatDuration(run.StartedAt ?? run.CreatedAt, run.FinishedAt);
        var adoRunUrl = run.AdoTestRunId is int rid
            ? $"https://dev.azure.com/_testManagement/runs?runId={rid}" : "";
        var reasoning = ExtractReasoning(run.ImpactAnalysisJson);

        // ponytail: minimal token set; TeamsNotifier only builds notifications, never blocks runs.
        var tokens = new Dictionary<string, string>
        {
            ["module"]           = run.Module ?? "",
            ["status"]           = run.Status.ToString(),
            ["passed"]           = passed.ToString(),
            ["failed"]           = failed.ToString(),
            ["duration"]         = duration,
            ["trigger"]          = run.TriggerSource.ToWire(),
            ["profile"]          = run.Profile.ToString(),
            ["ado_run_url"]      = adoRunUrl,
            ["has_failures"]     = (failed > 0).ToString().ToLowerInvariant(),
            ["level_breakdown"]  = BuildLevelBreakdown(run),
            ["failed_tests"]     = "",
            ["has_impact"]       = (!string.IsNullOrWhiteSpace(reasoning)).ToString().ToLowerInvariant(),
            ["impact_reasoning"] = reasoning,
            ["has_report"]       = (!string.IsNullOrWhiteSpace(run.ReportPath)).ToString().ToLowerInvariant(),
            ["report_url"]       = run.ReportPath ?? "",
            ["report_line"]      = string.IsNullOrWhiteSpace(run.ReportPath) ? "" : $"[View Allure report]({run.ReportPath})",
            ["env"]              = "",
            ["level_table"]      = BuildLevelBreakdown(run),
            ["failed_color"]     = (failed > 0) ? "Attention" : "Good",
            ["email_body"]       = Uri.EscapeDataString($"QA Loop run completed for {run.Module}."),
        };

        var body = FillTokens(SelectTemplate(opts.TeamsTemplate), tokens);
        await PostAsync(opts.TeamsWebhookUrl, body, ct);
    }

    public async Task NotifyTriageAsync(
        TestRun run, int triagedCount, IReadOnlyList<int> filedBugIds, CancellationToken ct = default)
    {
        if (run.Status == TestRunStatus.Cancelled) return;
        var opts = _opts.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.TeamsWebhookUrl))
        {
            _log.LogDebug("Teams webhook not configured — skipping triage notification.");
            return;
        }

        var bugSummary = filedBugIds.Count == 0
            ? $"{triagedCount} failures triaged — {triagedCount} real bug draft(s) awaiting review."
            : $"{triagedCount} failures triaged — {filedBugIds.Count} bug(s) filed: {string.Join(", ", filedBugIds.Select(id => $"#{id}"))}";

        var tokens = new Dictionary<string, string>
        {
            ["module"]       = run.Module ?? "",
            ["status"]       = "Triage complete",
            ["passed"]       = run.Passed.ToString(),
            ["failed"]       = run.Failed.ToString(),
            ["duration"]     = "",
            ["trigger"]      = run.TriggerSource.ToWire(),
            ["profile"]      = run.Profile.ToString(),
            ["ado_run_url"]  = "",
            ["has_failures"] = "false",
            ["level_breakdown"] = bugSummary,
            ["failed_tests"] = "",
            ["has_impact"]   = "false",
            ["impact_reasoning"] = "",
            ["has_report"]   = "false",
            ["report_url"]   = "",
            ["report_line"]  = "",
            ["env"]          = "",
            ["level_table"]  = bugSummary,
            ["failed_color"] = "Attention",
            ["email_body"]   = Uri.EscapeDataString(bugSummary),
        };

        var body = FillTokens(SelectTemplate(opts.TeamsTemplate), tokens);
        await PostAsync(opts.TeamsWebhookUrl, body, ct);
    }

    private async Task PostAsync(string webhookUrl, string body, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(webhookUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Teams webhook returned {Status} — notification may not have delivered.", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Teams notification failed — continuing (non-blocking).");
        }
    }

    private static string SelectTemplate(string name) => name switch
    {
        "compact"   => CompactTemplate,
        "scheduled" => ScheduledTemplate,
        _           => DeveloperTemplate,  // default: developer
    };

    private static string FillTokens(string template, Dictionary<string, string> tokens)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in tokens)
            sb.Replace($"{{{{{k}}}}}", v ?? "");
        return sb.ToString();
    }

    private static string BuildLevelBreakdown(TestRun run)
    {
        // ponytail: minimal text breakdown; richer per-level stats require Level breakdown on TestRun
        return $"Total: {run.Passed + run.Failed} | Passed: {run.Passed} | Failed: {run.Failed}";
    }

    private static string ExtractReasoning(string? impactJson)
    {
        if (string.IsNullOrEmpty(impactJson)) return "";
        try
        {
            var impact = JsonSerializer.Deserialize<QaAutomation.Core.Models.ImpactAnalysis>(impactJson);
            return impact?.Reasoning ?? "";
        }
        catch { return ""; }
    }

    private static string FormatDuration(DateTime start, DateTime? end)
    {
        if (end is null) return "";
        var ts = end.Value - start;
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{ts.Seconds}s";
    }

    private static string LoadTemplate(string fileName)
    {
        var asm = typeof(TeamsNotifier).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Templates." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Teams template not found: {fileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
