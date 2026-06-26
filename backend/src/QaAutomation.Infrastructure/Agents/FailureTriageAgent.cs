using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>The triage verdict for one failed test: a classification + an authored house-style bug
/// (the bug is only filed when <see cref="Classification"/> is <c>real_bug</c>). <see cref="Observations"/>
/// is the actual behavior as distinct bullets (each renders as its own list item).</summary>
public sealed record FailureTriageResult(
    string Classification, string Confidence, string Reason, GeneratedBug Bug, List<string> Observations);

/// <summary>
/// Failure-triage agent (Track A) — given a failed Playwright test (title + failure message/stack) and,
/// when available, the KB flow it covers, it classifies the failure (real_bug / flaky / test_issue /
/// stale_test) and authors a house-style bug. Authored prompt (not a Python port). Reuses
/// <see cref="GeneratedBug"/> so the deterministic ADO client files it the same way as the Bug Writer.
/// </summary>
public sealed class FailureTriageAgent
{
    private static readonly string[] ValidClasses = { "real_bug", "flaky", "test_issue", "stale_test" };

    private readonly ILlmClient _llm;

    public FailureTriageAgent(ILlmClient llm) => _llm = llm;

    public async Task<FailureTriageResult> TriageAsync(
        string testTitle, int? adoTestCaseId, string failureMessage, string failureDetails, string? kbFlowMarkdown,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(testTitle, adoTestCaseId, failureMessage, failureDetails, kbFlowMarkdown);
        var data = await JsonUtils.CompleteJsonAsync(
            _llm, AgentPrompts.FailureTriageSystem, prompt, '{', progress, emit,
            label: "Triage", cancellationToken: cancellationToken);
        if (data is not JsonObject o)
            throw new AgentException("Triage returned non-object JSON.");

        var classification = JsonUtils.StrOr(o, "classification").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (!ValidClasses.Contains(classification))
            classification = classification.Contains("real") || classification.Contains("bug") ? "real_bug"
                : classification.Contains("flak") ? "flaky"
                : classification.Contains("stale") ? "stale_test"
                : "test_issue";

        var confidence = JsonUtils.StrOr(o, "confidence").Trim().ToLowerInvariant();
        if (confidence is not ("high" or "medium" or "low"))
            confidence = "medium";

        var bugObj = o["bug"] as JsonObject ?? new JsonObject();
        // "actual" may be an array (preferred — one observation per bullet) or a single string.
        var observations = JsonUtils.CoerceStringList(bugObj["actual"] ?? bugObj["Actual"]);
        var bug = new GeneratedBug
        {
            Title = JsonUtils.StrOr(bugObj, "title"),
            Severity = NormalizeSeverity(JsonUtils.StrOr(bugObj, "severity")),
            Preconditions = JsonUtils.StrOr(bugObj, "preconditions"),
            ReproSteps = JsonUtils.CoerceStringList(bugObj["reproSteps"] ?? bugObj["ReproSteps"]),
            Expected = JsonUtils.StrOr(bugObj, "expected"),
            Actual = string.Join(" ", observations),   // joined form for any string consumer
            SuggestedFix = JsonUtils.StrOr(bugObj, "suggestedFix"),
        };

        return new FailureTriageResult(classification, confidence, JsonUtils.StrOr(o, "reason"), bug, observations);
    }

    /// <summary>Normalize to the four ADO severities (mirrors the Bug Writer's coercion).</summary>
    internal static string NormalizeSeverity(string raw)
    {
        var sev = (raw ?? "").Trim();
        if (sev is "Critical" or "High" or "Medium" or "Low")
            return sev;
        var low = sev.ToLowerInvariant();
        return low.Contains("crit") ? "Critical"
            : low.Contains("high") ? "High"
            : low.Contains("low") ? "Low"
            : "Medium";
    }

    internal static string BuildPrompt(
        string testTitle, int? adoTestCaseId, string failureMessage, string failureDetails, string? kbFlowMarkdown)
    {
        var lines = new List<string>
        {
            "## A Playwright E2E test failed — triage it.",
            "",
            $"**Test title:** {testTitle}",
            $"**ADO test case id:** {(adoTestCaseId is int id ? id.ToString() : "unknown (no [id] in title)")}",
            "",
            "## Failure message",
            string.IsNullOrWhiteSpace(failureMessage) ? "(none)" : failureMessage,
            "",
            "## Failure details / stack",
            string.IsNullOrWhiteSpace(failureDetails) ? "(none)" : failureDetails,
        };
        if (!string.IsNullOrWhiteSpace(kbFlowMarkdown))
        {
            lines.Add("");
            lines.Add("## KB flow this test covers — ground expected behavior + repro steps in this:");
            lines.Add(kbFlowMarkdown!);
        }
        lines.Add("");
        lines.Add("Classify the failure and author the bug JSON now.");
        return string.Join("\n", lines);
    }
}
