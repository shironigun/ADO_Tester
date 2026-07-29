using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>
/// Post-processes raw allure-results (before `allure generate`) so the report groups tests by API
/// level instead of Playwright's default "chromium" suite, and attaches an AI failure analysis to
/// every failed test. Idempotent — safe to run again on already-enriched results (report rebuild).
/// Never throws: an enrichment failure degrades the report, it must not fail the run.
/// </summary>
public sealed class AllureEnricher
{
    /// <summary>One LLM call per run; failures beyond this cap keep Playwright's own error
    /// attachment but get no AI analysis (logged).</summary>
    private const int MaxAiFailures = 60;
    private const int MaxErrorChars = 500;
    private const string AiAttachmentName = "AI Failure Analysis";

    private static readonly Regex TcPendingRegex = new(@"\[<tcId-pending(?::\d+)?>\]\s*", RegexOptions.Compiled);

    private readonly ILlmClientFactory _llmFactory;
    private readonly IEventBus _bus;
    private readonly ILogger<AllureEnricher> _log;

    public AllureEnricher(ILlmClientFactory llmFactory, IEventBus bus, ILogger<AllureEnricher> log)
    {
        _llmFactory = llmFactory;
        _bus = bus;
        _log = log;
    }

    public async Task EnrichAsync(string resultsDir, PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
    {
        try
        {
            await EnrichCoreAsync(resultsDir, spec, eventKey, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Run {RunId}: Allure enrichment failed — report will be un-enriched", spec.RunId);
            Pub(eventKey, "Allure enrichment failed (report still generated): " + ex.Message, EventLevels.Warn);
        }
    }

    private async Task EnrichCoreAsync(string resultsDir, PlaywrightRunSpec spec, string eventKey, CancellationToken ct)
    {
        var docs = new List<(string Path, JsonObject Root)>();
        foreach (var file in Directory.EnumerateFiles(resultsDir, "*-result.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject root)
                    docs.Add((file, root));
            }
            catch (JsonException ex) { _log.LogWarning(ex, "Skipping malformed allure result {File}", file); }
        }
        if (docs.Count == 0) return;

        // ── 1. Level grouping + display-name cleanup (all tests) ────────────────
        foreach (var (_, root) in docs)
        {
            var levelDir = DetectLevelDir(root, spec.LevelFilter);
            var display = LevelDisplay(levelDir);
            var labels = root["labels"] as JsonArray ?? (JsonArray)(root["labels"] = new JsonArray());

            // Suites tab: Level → describe title (e.g. "Endpoint: PUT lead/deal/…")
            var describe = GetLabel(labels, "subSuite") ?? GetLabel(labels, "suite") ?? "";
            SetLabel(labels, "parentSuite", display);
            if (describe.Length > 0) SetLabel(labels, "suite", describe);
            RemoveLabel(labels, "subSuite");

            // Behaviors tab: epic=level, feature=endpoint/flow, story=test case
            SetLabel(labels, "epic", display);
            if (describe.Length > 0) SetLabel(labels, "feature", describe);
            SetLabel(labels, "layer", "API");

            // Strip the "[<tcId-pending:N>]" scaffolding from display names (historyId is already
            // computed and stored in the file, so renaming does not break trend continuity).
            if (root["name"]?.GetValue<string>() is { } name)
            {
                var clean = TcPendingRegex.Replace(name, "").Trim();
                if (clean.Length > 0) root["name"] = clean;
            }
        }

        // ── 2. AI failure analysis attachments (failed/broken tests) ───────────
        var failures = docs
            .Where(d => d.Root["status"]?.GetValue<string>() is "failed" or "broken")
            .ToList();

        if (failures.Count > 0)
        {
            Pub(eventKey, $"AI failure analysis — {Math.Min(failures.Count, MaxAiFailures)} of {failures.Count} failure(s), one batch call…");
            try
            {
                var input = failures.Take(MaxAiFailures).Select((d, i) => new FailureToExplain(
                    i,
                    LevelDisplay(DetectLevelDir(d.Root, spec.LevelFilter)),
                    d.Root["name"]?.GetValue<string>() ?? "",
                    Truncate(d.Root["statusDetails"]?["message"]?.GetValue<string>() ?? "", MaxErrorChars)))
                    .ToList();

                var llm = await _llmFactory.CreateAsync(ct);
                var analyses = await new FailureExplainerAgent(llm).ExplainAsync(input, ct);

                var attached = 0;
                foreach (var a in analyses)
                {
                    if (a.Index < 0 || a.Index >= failures.Count) continue;
                    AttachAnalysis(resultsDir, failures[a.Index].Root, a);
                    attached++;
                }
                if (failures.Count > MaxAiFailures)
                    Pub(eventKey, $"AI analysis capped at {MaxAiFailures} failures — {failures.Count - MaxAiFailures} analyzed by category only.", EventLevels.Warn);
                Pub(eventKey, $"AI failure analysis attached to {attached} test(s).", EventLevels.Success);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Run {RunId}: AI failure analysis unavailable", spec.RunId);
                Pub(eventKey, "AI failure analysis unavailable (report keeps Playwright error context): " + ex.Message, EventLevels.Warn);
            }
        }

        // ── 3. Persist ──────────────────────────────────────────────────────────
        foreach (var (path, root) in docs)
            File.WriteAllText(path, root.ToJsonString());
    }

    private static void AttachAnalysis(string resultsDir, JsonObject root, FailureExplanation a)
    {
        var md = $"""
            # AI Failure Analysis

            **Root cause:** {a.RootCause}

            **What happened:** {a.Explanation}

            **Suggested fix:** {a.SuggestedFix}

            ---
            _Confidence: {a.Confidence} · generated by the QA Loop failure explainer_
            """;

        var source = $"{Guid.NewGuid()}-attachment.md";
        File.WriteAllText(Path.Combine(resultsDir, source), md);

        var attachments = root["attachments"] as JsonArray ?? (JsonArray)(root["attachments"] = new JsonArray());
        // Idempotency: a rebuild replaces the previous analysis instead of stacking duplicates.
        for (var i = attachments.Count - 1; i >= 0; i--)
            if (attachments[i]?["name"]?.GetValue<string>() == AiAttachmentName)
                attachments.RemoveAt(i);

        attachments.Insert(0, new JsonObject
        {
            ["name"] = AiAttachmentName,
            ["source"] = source,
            ["type"] = "text/markdown",
        });
    }

    /// <summary>Level folder ("contracts"/"endpoints"/"workflows") from the result's file path —
    /// falls back to the run's level filter when the path has no level prefix (level-scoped runs
    /// use the level folder itself as testDir).</summary>
    private static string? DetectLevelDir(JsonObject root, string? levelFilter)
    {
        var full = (root["fullName"]?.GetValue<string>() ?? "").Replace('\\', '/');
        var seg = full.Split('/')[0].ToLowerInvariant();
        if (IsLevelDir(seg)) return seg;

        if (root["titlePath"] is JsonArray tp && tp.Count > 0)
        {
            var t = (tp[0]?.GetValue<string>() ?? "").Replace('\\', '/').Split('/')[0].ToLowerInvariant();
            if (IsLevelDir(t)) return t;
        }

        return levelFilter is { Length: > 0 } lf && Enum.TryParse<TestLevel>(lf, ignoreCase: true, out var level)
            ? level.ToDirName()
            : null;
    }

    private static bool IsLevelDir(string s) => s is "contracts" or "endpoints" or "workflows" or "uis";

    private static string LevelDisplay(string? dir) => dir switch
    {
        "contracts" => "Contract Tests",
        "endpoints" => "Endpoint Tests",
        "workflows" => "Workflow Tests",
        "uis"       => "UI Tests",
        _            => "Other",
    };

    private static string? GetLabel(JsonArray labels, string name) => labels
        .OfType<JsonObject>()
        .FirstOrDefault(l => l["name"]?.GetValue<string>() == name)?["value"]?.GetValue<string>();

    private static void SetLabel(JsonArray labels, string name, string value)
    {
        var existing = labels.OfType<JsonObject>().FirstOrDefault(l => l["name"]?.GetValue<string>() == name);
        if (existing is not null) existing["value"] = value;
        else labels.Add(new JsonObject { ["name"] = name, ["value"] = value });
    }

    private static void RemoveLabel(JsonArray labels, string name)
    {
        for (var i = labels.Count - 1; i >= 0; i--)
            if (labels[i] is JsonObject o && o["name"]?.GetValue<string>() == name)
                labels.RemoveAt(i);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Pub(string key, string text, string level = EventLevels.Info)
        => _bus.Publish(key, new ConsoleEvent { Kind = EventKinds.System, Phase = "run", Text = text, Level = level });
}
