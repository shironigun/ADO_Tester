using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Reads the FRS Knowledge Base from disk using KnowledgebaseDocument.StoredPath.
/// Parses atlas YAML frontmatter to resolve slugs, extract path globs, and build the
/// FrsModuleContext consumed by the QA Loop pipeline. Scoped.
/// </summary>
public sealed class FrsKbService : IFrsKbService
{
    private readonly IKnowledgebaseService _kb;
    private readonly ILogger<FrsKbService> _log;

    // Memoize atlas parses within the scoped lifetime (one scan = one scope).
    private readonly Dictionary<string, ParsedAtlasYaml?> _atlasCache = new(StringComparer.OrdinalIgnoreCase);

    public FrsKbService(IKnowledgebaseService kb, ILogger<FrsKbService> log)
    {
        _kb = kb;
        _log = log;
    }

    // ── ListSlugsAsync ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListSlugsAsync(int projectId, CancellationToken ct)
    {
        var docs = await _kb.GetDocumentsByFolderAsync(projectId, "modules", ct);
        return docs
            .Select(d => Path.GetFileNameWithoutExtension(d.FileName))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── ResolveModuleAsync ───────────────────────────────────────────────────

    public async Task<FrsModuleContext?> ResolveModuleAsync(
        string moduleName, int projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return null;

        var moduleDocs = await _kb.GetDocumentsByFolderAsync(projectId, "modules", ct);
        if (moduleDocs.Count == 0)
        {
            _log.LogDebug("FrsKbService: no modules/ documents found in KB for project {P}", projectId);
            return null;
        }

        // Deduplicate by filename — latest UploadedAt wins.
        var deduped = moduleDocs
            .GroupBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.UploadedAt).First())
            .ToList();

        // Pass 1: exact slug match (filename without extension).
        var exactMatch = deduped.FirstOrDefault(d =>
            string.Equals(Path.GetFileNameWithoutExtension(d.FileName), moduleName,
                StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return await BuildContextAsync(exactMatch, projectId, ct);

        // Pass 2: alias + keywords_exclusive match — requires atlas YAML read.
        foreach (var doc in deduped)
        {
            var slug = Path.GetFileNameWithoutExtension(doc.FileName);
            var yaml = await GetAtlasYamlAsync(slug, projectId, ct);
            if (yaml == null) continue;

            bool matched = yaml.Aliases.Any(a =>
                    string.Equals(a, moduleName, StringComparison.OrdinalIgnoreCase))
                || yaml.KeywordsExclusive.Any(k =>
                    moduleName.Contains(k, StringComparison.OrdinalIgnoreCase)
                    || k.Contains(moduleName, StringComparison.OrdinalIgnoreCase));

            if (matched)
                return await BuildContextAsync(doc, projectId, ct);
        }

        _log.LogDebug("FrsKbService: no slug matched '{Module}' in project {P}", moduleName, projectId);
        return null;
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<FrsModuleContext?> BuildContextAsync(
        KnowledgebaseDocument moduleDoc, int projectId, CancellationToken ct)
    {
        var slug = Path.GetFileNameWithoutExtension(moduleDoc.FileName);

        // Read module + atlas + RULES from disk.
        var moduleMarkdown = await ReadStoredFileAsync(moduleDoc, slug, "module");
        if (moduleMarkdown == null) return null;

        var atlasDoc = await _kb.GetDocumentByPathAsync(projectId, "atlas", $"{slug}.md", ct);
        var atlasMarkdown = atlasDoc != null ? await ReadStoredFileAsync(atlasDoc, slug, "atlas") : null;

        var rulesDoc = await _kb.GetDocumentByPathAsync(projectId, "", "RULES.md", ct);
        var rulesMarkdown = rulesDoc != null ? await ReadStoredFileAsync(rulesDoc, slug, "RULES") : null;

        // Parse YAML from atlas.
        var yaml = atlasMarkdown != null ? ParseAtlasYaml(atlasMarkdown) : new ParsedAtlasYaml();

        // Extract RULES section for this slug.
        var rulesSection = rulesMarkdown != null
            ? ExtractRulesSection(rulesMarkdown, slug)
            : string.Empty;

        // Extract FE↔BE links table from atlas.
        var feBackLinks = atlasMarkdown != null
            ? ExtractSection(atlasMarkdown, "## Frontend ↔ Backend links", "## ")
            : string.Empty;

        // Build compact FeaturesJson.
        var featuresJson = SerializeFeaturesJson(yaml.Features);

        return new FrsModuleContext(
            Slug: slug,
            ModuleMarkdown: moduleMarkdown,
            AtlasMarkdown: atlasMarkdown ?? string.Empty,
            FrontendPathIncludes: yaml.FrontendPathIncludes.ToArray(),
            BackendPathIncludes: yaml.BackendPathIncludes.ToArray(),
            KeywordsExclusive: yaml.KeywordsExclusive.ToArray(),
            RulesMarkdown: rulesSection,
            FeBackLinksMarkdown: feBackLinks,
            FeaturesJson: featuresJson);
    }

    private async Task<ParsedAtlasYaml?> GetAtlasYamlAsync(
        string slug, int projectId, CancellationToken ct)
    {
        if (_atlasCache.TryGetValue(slug, out var cached))
            return cached;

        var atlasDoc = await _kb.GetDocumentByPathAsync(projectId, "atlas", $"{slug}.md", ct);
        if (atlasDoc == null)
        {
            _atlasCache[slug] = null;
            return null;
        }

        var text = await ReadStoredFileAsync(atlasDoc, slug, "atlas");
        var result = text != null ? ParseAtlasYaml(text) : null;
        _atlasCache[slug] = result;
        return result;
    }

    private async Task<string?> ReadStoredFileAsync(
        KnowledgebaseDocument doc, string slug, string kind)
    {
        if (string.IsNullOrEmpty(doc.StoredPath) || !File.Exists(doc.StoredPath))
        {
            _log.LogWarning("FrsKbService: stored file not found for {Kind}/{Slug} at '{Path}'",
                kind, slug, doc.StoredPath);
            return null;
        }
        try
        {
            return await File.ReadAllTextAsync(doc.StoredPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FrsKbService: failed to read {Kind}/{Slug}", kind, slug);
            return null;
        }
    }

    // ── YAML frontmatter parser ───────────────────────────────────────────────

    private static ParsedAtlasYaml ParseAtlasYaml(string atlasMarkdown)
    {
        try
        {
            // Extract text between first and second '---' fence.
            var lines = atlasMarkdown.Replace("\r\n", "\n").Split('\n');
            int start = -1, end = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    if (start == -1) start = i;
                    else { end = i; break; }
                }
            }
            if (start == -1 || end == -1) return new ParsedAtlasYaml();

            var yaml = new ParsedAtlasYaml();
            string? currentKey = null;
            bool inFeature = false;
            var currentFeature = new FeatureEntry();

            for (int i = start + 1; i < end; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Top-level key
                var topMatch = Regex.Match(line, @"^(\w[\w_-]*):\s*(.*)$");
                if (topMatch.Success && !line.StartsWith("  ") && !line.StartsWith("- "))
                {
                    if (inFeature) { yaml.Features.Add(currentFeature); currentFeature = new FeatureEntry(); inFeature = false; }
                    currentKey = topMatch.Groups[1].Value;
                    var val = topMatch.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(val) && val != "|" && val != ">")
                    {
                        // Scalar value (e.g. slug: messaging)
                        // We only care about array keys, but slug/module can be scalar.
                    }
                    continue;
                }

                // Array item: "  - value" or "- value"
                var itemMatch = Regex.Match(line, @"^[\s]*-\s+(.+)$");
                if (itemMatch.Success && currentKey != null)
                {
                    var val = itemMatch.Groups[1].Value.Trim().Trim('"', '\'');

                    switch (currentKey)
                    {
                        case "aliases": yaml.Aliases.Add(val); break;
                        case "keywords_exclusive": yaml.KeywordsExclusive.Add(val); break;
                        case "keywords_shared_ambiguous": break; // not needed
                        case "keywords_reject": break;
                        case "frontend_path_includes": yaml.FrontendPathIncludes.Add(val); break;
                        case "frontend_path_excludes": break;
                        case "backend_path_includes": yaml.BackendPathIncludes.Add(val); break;
                        case "backend_path_excludes": break;
                        case "features":
                            // Start new feature block when we see an "- id: ..." or "- " line.
                            if (inFeature) { yaml.Features.Add(currentFeature); currentFeature = new FeatureEntry(); }
                            inFeature = true;
                            // val may be "id: msg-send" or just a dash
                            var idMatch = Regex.Match(val, @"id:\s*(\S+)");
                            if (idMatch.Success) currentFeature.Id = idMatch.Groups[1].Value;
                            break;
                    }
                    continue;
                }

                // Indented feature sub-key: "    id: msg-send" or "    aliases: [...]"
                if (inFeature && line.StartsWith("    ") || line.StartsWith("  "))
                {
                    var subMatch = Regex.Match(line.Trim(), @"^(id|aliases|apis):\s*(.*)$");
                    if (subMatch.Success)
                    {
                        var subKey = subMatch.Groups[1].Value;
                        var subVal = subMatch.Groups[2].Value.Trim();
                        switch (subKey)
                        {
                            case "id": currentFeature.Id = subVal.Trim('"', '\''); break;
                            case "aliases":
                                if (subVal.StartsWith("["))
                                    currentFeature.Aliases.AddRange(
                                        subVal.Trim('[', ']').Split(',')
                                            .Select(s => s.Trim().Trim('"', '\'')));
                                break;
                            case "apis":
                                if (subVal.StartsWith("["))
                                    currentFeature.Apis.AddRange(
                                        subVal.Trim('[', ']').Split(',')
                                            .Select(s => s.Trim().Trim('"', '\'')));
                                break;
                        }
                    }
                    else if (line.TrimStart().StartsWith("- ") && inFeature)
                    {
                        // Inline list item under a feature sub-key — skip for now (handled by flat parse above).
                    }
                }
            }
            if (inFeature) yaml.Features.Add(currentFeature);
            return yaml;
        }
        catch (Exception)
        {
            // Defensive: non-FRS docs or malformed YAML → return empty, do not throw.
            return new ParsedAtlasYaml();
        }
    }

    // ── Section extractors ────────────────────────────────────────────────────

    private static string ExtractRulesSection(string rulesMarkdown, string slug)
    {
        var lines = rulesMarkdown.Replace("\r\n", "\n").Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == $"## {slug}") { start = i; break; }
        }
        if (start == -1) return string.Empty;

        var sb = new System.Text.StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            if (i > start && lines[i].StartsWith("## ")) break;
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().Trim();
    }

    private static string ExtractSection(string markdown, string heading, string nextHeadingPrefix)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == heading) { start = i; break; }
        }
        if (start == -1) return string.Empty;

        var sb = new System.Text.StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            if (i > start && lines[i].StartsWith(nextHeadingPrefix) && lines[i].Length > nextHeadingPrefix.Length)
                break;
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().Trim();
    }

    // ── Feature JSON serialization ────────────────────────────────────────────

    private static string SerializeFeaturesJson(List<FeatureEntry> features)
    {
        if (features.Count == 0) return "[]";
        var items = features
            .Where(f => !string.IsNullOrWhiteSpace(f.Id))
            .Select(f => new
            {
                id      = f.Id,
                aliases = f.Aliases.ToArray(),
                apis    = f.Apis.ToArray(),
            });
        return JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false });
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ParsedAtlasYaml
    {
        public List<string> Aliases               { get; } = [];
        public List<string> KeywordsExclusive     { get; } = [];
        public List<string> FrontendPathIncludes  { get; } = [];
        public List<string> BackendPathIncludes   { get; } = [];
        public List<FeatureEntry> Features        { get; } = [];
    }

    private sealed class FeatureEntry
    {
        public string Id            { get; set; } = string.Empty;
        public List<string> Aliases { get; } = [];
        public List<string> Apis    { get; } = [];
    }
}
