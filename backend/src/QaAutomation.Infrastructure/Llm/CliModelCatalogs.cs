using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.Llm;

/// <summary>Shared catalog DTOs + parsers for CLI model pickers (Claude / Copilot / Cursor).</summary>
public static class CliModelCatalogs
{
    public sealed record Option(string Label, string Value);
    public sealed record ModelEntry(string Id, string Label, IReadOnlyList<Option> Efforts, IReadOnlyList<Option> Contexts);

    public static readonly Option[] ClaudeEfforts =
    [
        new("Low", "low"),
        new("Medium", "medium"),
        new("High", "high"),
        new("Max", "max"),
    ];

    public static readonly Option[] CopilotEfforts =
    [
        new("None", "none"),
        new("Minimal", "minimal"),
        new("Low", "low"),
        new("Medium", "medium"),
        new("High", "high"),
        new("Extra High", "xhigh"),
        new("Max", "max"),
    ];

    public static readonly Option[] CopilotContexts =
    [
        new("200k", "default"),
        new("1M", "long_context"),
    ];

    public static readonly Option[] CursorEfforts =
    [
        new("Low", "low"),
        new("Medium", "medium"),
        new("High", "high"),
        new("Extra High", "xhigh"),
        new("Max", "max"),
    ];

    /// <summary>Grok 4.5 effort tiers as exposed by <c>agent --list-models</c> suffixes.</summary>
    public static readonly Option[] CursorGrokEfforts =
    [
        new("Low", "low"),
        new("Medium", "medium"),
        new("High", "high"),
    ];

    public static readonly Option[] CursorContexts =
    [
        new("Default", ""),
        new("200k", "200k"),
        new("1M", "1m"),
    ];

    /// <summary>Display-only context label for Claude model aliases (CLI has no --context flag).</summary>
    public static string? ClaudeContextLabel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.Trim().ToLowerInvariant();
        if (m.Contains("opus") || m.Contains("1m")) return "1M";
        if (m is "haiku" or "sonnet" || m.Contains("haiku") || m.Contains("sonnet")) return "200k";
        return "200k";
    }

    public static string? CopilotContextLabel(string? contextTier) => contextTier?.Trim() switch
    {
        "long_context" => "1M",
        "default" => "200k",
        "" or null => null,
        var other => other,
    };

    public static IReadOnlyList<ModelEntry> ClaudeCatalog() =>
    [
        new("haiku", "Haiku (latest)", ClaudeEfforts, [new("200k", "200k")]),
        new("sonnet", "Sonnet (latest)", ClaudeEfforts, [new("200k", "200k")]),
        new("opus", "Opus (latest)", ClaudeEfforts, [new("1M", "1M")]),
        new("claude-haiku-4-6", "Claude Haiku 4.6", ClaudeEfforts, [new("200k", "200k")]),
        new("claude-sonnet-4-5", "Claude Sonnet 4.5", ClaudeEfforts, [new("200k", "200k")]),
        new("claude-sonnet-4-6", "Claude Sonnet 4.6", ClaudeEfforts, [new("200k", "200k")]),
        new("claude-opus-4-7", "Claude Opus 4.7", ClaudeEfforts, [new("1M", "1M")]),
        new("claude-opus-4-8", "Claude Opus 4.8", ClaudeEfforts, [new("1M", "1M")]),
    ];

    public static IReadOnlyList<ModelEntry> CopilotCatalog() =>
    [
        new("claude-opus-4.8", "Claude Opus 4.8", CopilotEfforts, CopilotContexts),
        new("claude-opus-4.7", "Claude Opus 4.7", CopilotEfforts, CopilotContexts),
        new("claude-sonnet-4.6", "Claude Sonnet 4.6", CopilotEfforts, CopilotContexts),
        new("claude-sonnet-4.5", "Claude Sonnet 4.5", CopilotEfforts, CopilotContexts),
        new("claude-haiku-4.5", "Claude Haiku 4.5", CopilotEfforts, CopilotContexts),
        new("gpt-5.4", "GPT-5.4", CopilotEfforts, CopilotContexts),
        new("gpt-5.3-codex", "GPT-5.3 Codex", CopilotEfforts, CopilotContexts),
        new("gpt-5-mini", "GPT-5 Mini", CopilotEfforts, CopilotContexts),
        new("gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview", CopilotEfforts, CopilotContexts),
    ];

    private static readonly Regex CursorModelLine = new(
        @"^(?<id>[A-Za-z0-9._\-]+)\s+-\s+(?<label>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex CursorGrokVariant = new(
        @"^cursor-grok-4\.5(?:-(?<effort>low|medium|high))?(?:-fast)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Featured Cursor models always offered in Settings (haiku / composer / grok 4.5) with
    /// effort + context pickers, then the live <c>agent --list-models</c> list (deduped).
    /// </summary>
    public static IReadOnlyList<ModelEntry> ParseCursorListModels(string stdout)
    {
        var parsed = new List<(string Id, string Label)>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Available", StringComparison.OrdinalIgnoreCase))
                continue;
            var m = CursorModelLine.Match(line);
            if (!m.Success) continue;
            parsed.Add((m.Groups["id"].Value, m.Groups["label"].Value.Trim()));
        }

        var grokEfforts = InferGrokEfforts(parsed.Select(p => p.Id));
        var featured = new List<ModelEntry>
        {
            // Alias accepted by agent --model (parameterized with [effort=…,context=…]).
            new("haiku", "Haiku", CursorEfforts, CursorContexts),
            new("composer-2.5", "Composer 2.5", CursorEfforts, CursorContexts),
            new("cursor-grok-4.5", "Cursor Grok 4.5", grokEfforts, CursorContexts),
        };

        var list = new List<ModelEntry>(featured);
        var seen = new HashSet<string>(featured.Select(f => f.Id), StringComparer.OrdinalIgnoreCase)
        {
            "composer-2.5-fast",
        };

        foreach (var (id, label) in parsed)
        {
            if (IsCollapsedCursorVariant(id)) continue;
            if (!seen.Add(id)) continue;
            list.Add(new ModelEntry(id, label, CursorEfforts, CursorContexts));
        }

        if (parsed.Count == 0 && list.Count == featured.Count)
        {
            // Keep featured even when CLI missing; also surface auto for blank default.
            list.Insert(0, new ModelEntry("auto", "Auto (default)", CursorEfforts, CursorContexts));
        }

        return list;
    }

    private static Option[] InferGrokEfforts(IEnumerable<string> ids)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var m = CursorGrokVariant.Match(id);
            if (!m.Success) continue;
            var e = m.Groups["effort"].Success ? m.Groups["effort"].Value.ToLowerInvariant() : "high";
            found.Add(e);
        }
        if (found.Count == 0) return CursorGrokEfforts;
        var subset = CursorGrokEfforts.Where(o => found.Contains(o.Value)).ToArray();
        return subset.Length > 0 ? subset : CursorGrokEfforts;
    }

    /// <summary>True when this list-models id is represented by a featured base model.</summary>
    public static bool IsCollapsedCursorVariant(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var s = id.Trim();
        if (s.Equals("composer-2.5", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("composer-2.5-fast", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("haiku", StringComparison.OrdinalIgnoreCase)) return true;
        return CursorGrokVariant.IsMatch(s);
    }

    /// <summary>Compose Cursor <c>--model</c> value with optional bracket overrides.</summary>
    public static string ComposeCursorModel(string model, string? effort, string? context)
    {
        var id = NormalizeCursorModelId(model ?? "", ref effort);
        if (id.Length == 0) id = "auto";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context))
            parts.Add($"context={context.Trim()}");
        if (!string.IsNullOrWhiteSpace(effort))
            parts.Add($"effort={effort.Trim()}");
        if (parts.Count == 0)
            return id;
        // If model already has brackets, don't double-wrap.
        if (id.Contains('[', StringComparison.Ordinal))
            return id;
        return $"{id}[{string.Join(",", parts)}]";
    }

    /// <summary>Map aliases / effort-suffixed grok ids onto base model + effort.</summary>
    public static string NormalizeCursorModelId(string model, ref string? effort)
    {
        var id = (model ?? "").Trim();
        if (id.Length == 0) return "";

        // Friendly aliases.
        if (id.Equals("composer", StringComparison.OrdinalIgnoreCase)) return "composer-2.5";
        if (id.Equals("grok", StringComparison.OrdinalIgnoreCase)
            || id.Equals("grok-4.5", StringComparison.OrdinalIgnoreCase)
            || id.Equals("cursor-grok-4.5", StringComparison.OrdinalIgnoreCase))
            return "cursor-grok-4.5";
        if (id.Equals("claude-haiku", StringComparison.OrdinalIgnoreCase)
            || id.Equals("claude-haiku-4-5", StringComparison.OrdinalIgnoreCase)
            || id.Equals("claude-haiku-4.5", StringComparison.OrdinalIgnoreCase))
            return "haiku";

        var grok = CursorGrokVariant.Match(id);
        if (grok.Success)
        {
            if (string.IsNullOrWhiteSpace(effort) && grok.Groups["effort"].Success)
                effort = grok.Groups["effort"].Value.ToLowerInvariant();
            return "cursor-grok-4.5";
        }

        if (id.Equals("composer-2.5-fast", StringComparison.OrdinalIgnoreCase))
            return "composer-2.5";

        return id;
    }

    public static object ToApiModel(ModelEntry e) => new
    {
        id = e.Id,
        label = e.Label,
        efforts = e.Efforts.Select(o => new { label = o.Label, value = o.Value }).ToArray(),
        contexts = e.Contexts.Select(o => new { label = o.Label, value = o.Value }).ToArray(),
    };
}
