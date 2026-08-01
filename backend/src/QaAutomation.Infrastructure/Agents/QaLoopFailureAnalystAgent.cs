using System.Text;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// QA Loop failure analyst — plain-English explanation of a Playwright UI script failure.
/// Separate from Author Tests / FailureTriageAgent (no bug JSON; explanation string only).
/// </summary>
public sealed class QaLoopFailureAnalystAgent(ILlmClient llm)
{
    public async Task<string> ExplainAsync(
        string failureMessage,
        string caseTitle,
        string stepsJson,
        string? screenshotDirectory = null,
        CancellationToken ct = default)
    {
        var userPrompt = BuildPrompt(failureMessage, caseTitle, stepsJson, screenshotDirectory);
        var raw = await llm.CompleteAsync(
            AgentPrompts.QaLoopFailureAnalystSystem, userPrompt,
            model: null, progress: null, emit: null, cancellationToken: ct);
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            throw new AgentException("QA Loop failure analyst returned an empty explanation.");
        return text;
    }

    internal static string BuildPrompt(
        string failureMessage, string caseTitle, string stepsJson, string? screenshotDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test case: {caseTitle}");
        sb.AppendLine();
        sb.AppendLine("## Steps (JSON)");
        sb.AppendLine(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson);
        sb.AppendLine();
        sb.AppendLine("## Playwright failure");
        sb.AppendLine(string.IsNullOrWhiteSpace(failureMessage) ? "(no failure message)" : failureMessage);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            sb.AppendLine("## Screenshot / run directory");
            sb.AppendLine(screenshotDirectory);
            sb.AppendLine("(Directory may contain screenshots, traces, or junit.xml from the last UI run.)");
        }
        else
        {
            sb.AppendLine("## Screenshot / run directory");
            sb.AppendLine("(none — LastUiRunId missing or run directory not found)");
        }
        return sb.ToString();
    }
}
