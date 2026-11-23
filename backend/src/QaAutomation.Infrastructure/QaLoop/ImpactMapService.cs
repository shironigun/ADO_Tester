using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Seeds/extends product impact-map from read-only <c>*/build/build-qa.yml</c> (INT-401).
/// Write target is always under <c>AUTOMATION_ROOT</c>; YAML files are never modified.
/// </summary>
public sealed class ImpactMapService(
    IScriptPathAdapter paths,
    ILogger<ImpactMapService> log) : IImpactMapService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deals first, then grow one unit at a time — never empty-module scaffold.</summary>
    private static readonly UnitSeed[] UnitSeeds =
    [
        new(
            Unit: "deals",
            Tag: "@deals",
            BuildQaRelative: "Lead/build/build-qa.yml",
            ExtraPathPrefixes:
            [
                "web-app/src/target-crm/src/components/deals",
                "automation/modules/deals",
                "automation/common",
                "automation/ai",
            ],
            ContractSources:
            [
                "web-app/src/target-crm/src/components/deals/model",
                "Lead/src/Target.Crm.Lead/Target.Crm.Lead.Api/Target.Crm.Lead.Api/Deal/DealApiRoutes.cs",
                "Lead/src/Target.Crm.Lead/Target.Crm.Lead.Api/Target.Crm.Lead.Api/Deal/DealApi.cs",
                "/Models/Request/",
                "/Models/Response/",
            ]),
        // INT-501 — Messaging (second unit). Specs appear under modules/messaging only when generated.
        new(
            Unit: "messaging",
            Tag: "@messaging",
            BuildQaRelative: "messaging/build/build-qa.yml",
            ExtraPathPrefixes:
            [
                "web-app/src/target-crm/src/components/messaging",
                "automation/modules/messaging",
            ],
            ContractSources:
            [
                "web-app/src/target-crm/src/components/messaging/api",
                "web-app/src/target-crm/src/components/messaging/models",
                "/Models/Request/",
                "/Models/Response/",
            ]),
        // Nested / permission-gated units (first-class — not collapsed into side-menu parent).
        new(
            Unit: "cpq-plus",
            Tag: "@cpq-plus",
            BuildQaRelative: "Lead/build/build-qa.yml",
            ExtraPathPrefixes:
            [
                "web-app/src/target-crm/src/components/cpq-plus",
                "cpq-plus",
                "automation/modules/cpq-plus",
            ],
            ContractSources:
            [
                "web-app/src/target-crm/src/components/cpq-plus",
                "cpq-plus",
                "/Models/Request/",
                "/Models/Response/",
            ]),
    ];

    public ImpactMapSeedResult SeedFromBuildQa()
    {
        if (!paths.IsProductMode || paths.AutomationRoot is null)
            throw new InvalidOperationException("AUTOMATION_ROOT is not set; cannot seed impact-map.");

        var autoRoot = paths.AutomationRoot;
        var repoRoot = Directory.GetParent(autoRoot)?.FullName
            ?? throw new InvalidOperationException($"Cannot resolve product repo root from '{autoRoot}'.");

        var map = LoadOrCreate(autoRoot);
        var sources = new List<string>();
        var units = new List<string>();

        foreach (var seed in UnitSeeds)
        {
            var yml = Path.Combine(repoRoot, seed.BuildQaRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(yml))
            {
                log.LogWarning("build-qa.yml missing for unit {Unit}: {Path}", seed.Unit, yml);
                continue;
            }

            sources.Add(NormalizeRel(Path.GetRelativePath(repoRoot, yml)));
            var includes = BuildQaPathIncludeParser.ParseIncludes(File.ReadAllText(yml));
            foreach (var prefix in includes.Concat(seed.ExtraPathPrefixes))
            {
                var key = NormalizePrefix(prefix);
                if (string.IsNullOrEmpty(key)) continue;
                if (!map.PathMap.TryGetValue(key, out var tags))
                {
                    tags = [];
                    map.PathMap[key] = tags;
                }
                if (!tags.Contains(seed.Tag, StringComparer.OrdinalIgnoreCase))
                    tags.Add(seed.Tag);
            }

            foreach (var src in seed.ContractSources)
            {
                if (!map.ContractSources.Contains(src, StringComparer.Ordinal))
                    map.ContractSources.Add(src);
            }

            units.Add(seed.Unit);
        }

        map.Module = units.Count == 1 ? units[0] : string.Join(",", units);
        map.SmokeTag ??= "@smoke";
        map.Note =
            "Path map seeded from service */build/build-qa.yml trigger.paths.include (read-only) " +
            "plus curated FE/automation prefixes. Changing a mapped path selects the listed test tags.";

        var outPath = Path.Combine(autoRoot, "resources", "impact-map.json");
        paths.EnsureUnderRoot(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(map, JsonOpts) + Environment.NewLine);

        log.LogInformation("Seeded impact-map at {Path} ({Count} prefixes, units={Units})",
            outPath, map.PathMap.Count, string.Join(",", units));

        return new ImpactMapSeedResult(
            WrittenPath: outPath,
            PathPrefixCount: map.PathMap.Count,
            UnitsSeeded: units,
            SourcesRead: sources);
    }

    public ImpactPathSimResult Simulate(IReadOnlyList<string> changedFiles)
    {
        if (!paths.IsProductMode || paths.AutomationRoot is null)
            throw new InvalidOperationException("AUTOMATION_ROOT is not set; cannot simulate impact.");

        var map = LoadOrCreate(paths.AutomationRoot);
        var files = changedFiles
            .Select(f => f.Replace('\\', '/').Trim())
            .Where(f => f.Length > 0)
            .ToList();

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();

        foreach (var file in files)
        {
            foreach (var (prefix, prefixTags) in map.PathMap)
            {
                var p = prefix.Replace('\\', '/');
                if (file.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                    || file.Contains("/" + p, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var t in prefixTags) tags.Add(t);
                    if (!matched.Contains(file, StringComparer.OrdinalIgnoreCase))
                        matched.Add(file);
                }
            }
        }

        var smoke = string.IsNullOrWhiteSpace(map.SmokeTag) ? "@smoke" : map.SmokeTag;
        var usedSmoke = tags.Count == 0;
        var selected = usedSmoke ? new List<string> { smoke } : tags.OrderBy(t => t).ToList();
        var grep = string.Join("|", selected);
        return new ImpactPathSimResult(grep, selected, matched, usedSmoke);
    }

    private static ImpactMapDocument LoadOrCreate(string autoRoot)
    {
        var path = Path.Combine(autoRoot, "resources", "impact-map.json");
        if (!File.Exists(path))
            return new ImpactMapDocument();

        try
        {
            var doc = JsonSerializer.Deserialize<ImpactMapDocument>(File.ReadAllText(path), JsonOpts);
            return doc ?? new ImpactMapDocument();
        }
        catch (JsonException)
        {
            return new ImpactMapDocument();
        }
    }

    private static string NormalizePrefix(string raw)
    {
        var s = raw.Replace('\\', '/').Trim();
        while (s.EndsWith("/*", StringComparison.Ordinal)) s = s[..^2];
        while (s.EndsWith("/", StringComparison.Ordinal)) s = s[..^1];
        return s;
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/');

    private sealed record UnitSeed(
        string Unit,
        string Tag,
        string BuildQaRelative,
        IReadOnlyList<string> ExtraPathPrefixes,
        IReadOnlyList<string> ContractSources);

    private sealed class ImpactMapDocument
    {
        public string? Module { get; set; }
        public string? Note { get; set; }
        public Dictionary<string, List<string>> PathMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ContractSources { get; set; } = [];
        public string? SmokeTag { get; set; } = "@smoke";
    }
}

/// <summary>Minimal extractor for Azure Pipelines <c>trigger.paths.include</c> lists (no YAML package).</summary>
public static class BuildQaPathIncludeParser
{
    private static readonly Regex IncludeItem = new(
        @"^\s*-\s+(?<path>[^\s#]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ParseIncludes(string yamlText)
    {
        var lines = yamlText.Replace("\r\n", "\n").Split('\n');
        var results = new List<string>();
        var inPaths = false;
        var inInclude = false;
        var pathsIndent = -1;
        var includeIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            var indent = line.TakeWhile(c => c == ' ').Count();
            var trimmed = line.Trim();

            if (!inPaths && trimmed.StartsWith("paths:", StringComparison.Ordinal))
            {
                inPaths = true;
                pathsIndent = indent;
                inInclude = false;
                continue;
            }

            if (inPaths)
            {
                if (indent <= pathsIndent && !trimmed.StartsWith('-') && trimmed.Contains(':'))
                {
                    // Left the paths: block (e.g. pool:, stages:)
                    if (!trimmed.StartsWith("include:", StringComparison.Ordinal)
                        && !trimmed.StartsWith("exclude:", StringComparison.Ordinal))
                    {
                        inPaths = false;
                        inInclude = false;
                    }
                }

                if (trimmed.StartsWith("include:", StringComparison.Ordinal))
                {
                    inInclude = true;
                    includeIndent = indent;
                    continue;
                }

                if (inInclude)
                {
                    if (indent <= includeIndent && trimmed.Contains(':') && !trimmed.StartsWith('-'))
                    {
                        inInclude = false;
                        continue;
                    }

                    var m = IncludeItem.Match(line);
                    if (m.Success)
                        results.Add(m.Groups["path"].Value.Trim().Trim('"', '\''));
                }
            }
        }

        return results;
    }
}
