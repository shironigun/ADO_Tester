using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Reports;

/// <summary>
/// Turns a week of tickets into structured insights via the active LLM (spec B7). Always runs as part
/// of report generation; ANY failure (no client / LLM error / bad JSON / non-object) degrades to a
/// minimal <see cref="ReportSummary"/> so the deck still has a TL;DR. System prompt is frozen.
/// </summary>
public sealed partial class ReportSummarizer
{
    private const int MaxTicketsInPrompt = 80;

    private const string SystemPrompt =
        """
        You are the Report Insights agent. You analyze a week of QA / development
        tickets and produce a structured JSON summary that drives a stakeholder presentation.

        Output rules:
        - Return ONLY a JSON object — no markdown fences, no prose.
        - Be factual and neutral. No emoji, no marketing language, no hedging ("might", "could be").
        - Reference ticket IDs (numeric only) when grouping into themes.
        - Speaker script entries are short (1–2 sentences max) — what a BA would SAY when this slide is on screen.

        Required keys:
        {
          "headline": "string — one sentence summarizing the week's QA story",
          "tldr": ["3–5 short bullets, each one fact + implication"],
          "themes": [
            { "name": "short label", "description": "1 sentence", "ticketIds": [int, ...] }
          ],
          "risks": ["open critical/high items, no-fix-recorded items, repeated regressions"],
          "wins": ["closed-with-fix items, high-impact resolutions"],
          "speakerScript": {
            "cover":      "what to say on the cover slide",
            "tldr":       "what to say on the TL;DR slide",
            "by_type":    "what to say on the by-type stats slide",
            "by_person":  "what to say on the by-person stats slide",
            "themes":     "what to say on the themes slide (if any)",
            "risks":      "what to say on the risks slide (if any)",
            "wins":       "what to say on the wins slide (if any)",
            "closing":    "what to say on the closing slide"
          }
        }
        Every list field MUST contain ONLY plain strings — never objects, never nested arrays.
        """;

    private readonly ILlmClientFactory _llmFactory;
    private readonly ILogger<ReportSummarizer> _log;

    public ReportSummarizer(ILlmClientFactory llmFactory, ILogger<ReportSummarizer> log)
    {
        _llmFactory = llmFactory;
        _log = log;
    }

    public async Task<ReportSummary> SummarizeAsync(
        IReadOnlyList<ReportTicket> tickets, Action<LlmEmit>? emit = null, CancellationToken cancellationToken = default)
    {
        if (tickets.Count == 0)
            return new ReportSummary { Headline = "No tickets in range." };

        ILlmClient llm;
        try { llm = await _llmFactory.CreateAsync(cancellationToken); }
        catch (Exception e) { _log.LogWarning(e, "LLM not available for report summary"); return Fallback(tickets); }

        string raw;
        try { raw = await llm.CompleteAsync(SystemPrompt, BuildUserPrompt(tickets), emit: emit, cancellationToken: cancellationToken); }
        catch (Exception e) { _log.LogWarning(e, "Summarizer LLM call failed — falling back"); return Fallback(tickets); }

        var cleaned = StripFences(raw);
        try
        {
            if (JsonNode.Parse(cleaned) is not JsonObject)
                return Fallback(tickets);
        }
        catch (JsonException e) { _log.LogWarning(e, "Summarizer returned invalid JSON"); return Fallback(tickets); }

        return ReportSummary.FromJson(cleaned);
    }

    private static string BuildUserPrompt(IReadOnlyList<ReportTicket> tickets)
    {
        var sample = tickets.Take(MaxTicketsInPrompt).ToList();
        var lines = new List<string> { $"# Ticket Sample ({sample.Count} of {tickets.Count})" };
        foreach (var t in sample)
        {
            var assignee = string.IsNullOrEmpty(t.AssignedTo) ? "Unassigned" : t.AssignedTo;
            var state = string.IsNullOrEmpty(t.State) ? "?" : t.State;
            var issue = Truncate(StripHtml(t.IssueDescription), 280);
            var fix = Truncate(StripHtml(t.Fix), 200);
            var fixLabel = fix.Length > 0 ? fix : "(no fix recorded)";
            lines.Add($"- #{t.Id} [{t.WorkItemType} · {state} · {assignee}] {t.Title}\n  issue: {issue}\n  fix:   {fixLabel}");
        }
        lines.Add("");
        lines.Add("Produce the JSON described in the system prompt.");
        return string.Join("\n", lines);
    }

    private ReportSummary Fallback(IReadOnlyList<ReportTicket> tickets)
    {
        var total = tickets.Count;
        var noFix = tickets.Count(t => string.IsNullOrWhiteSpace(t.Fix));
        var types = tickets.Where(t => !string.IsNullOrEmpty(t.WorkItemType)).Select(t => t.WorkItemType).ToHashSet();
        return new ReportSummary
        {
            Headline = $"{total} ticket(s) across {types.Count} type(s); {noFix} missing a recorded fix.",
            Tldr =
            [
                $"{total} ticket(s) touched in the period.",
                noFix > 0 ? $"{noFix} closed/changed without a recorded fix." : "All tickets have a recorded fix.",
                $"Spans {types.Count} work-item type(s): {(types.Count > 0 ? string.Join(", ", types.OrderBy(x => x)) : "—")}.",
            ],
            SpeakerScript = new()
            {
                ["cover"] = "This week's QA snapshot for the team.",
                ["tldr"] = "High-level overview of what moved.",
                ["by_type"] = "Volume by ticket type.",
                ["by_person"] = "Where the work landed by assignee.",
                ["closing"] = "Questions and follow-ups.",
            },
        };
    }

    private static string StripHtml(string? s) => string.IsNullOrEmpty(s) ? "" : HtmlTagRegex().Replace(s, " ").Trim();
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string StripFences(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
            s = FenceRegex().Replace(s, "").Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        if (first >= 0 && last > first)
            s = s[first..(last + 1)];
        return s;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Multiline)]
    private static partial Regex FenceRegex();
}
