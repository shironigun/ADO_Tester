using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Builds <see cref="PlanSuiteUnitMap"/> from an ADO Test Plan suite tree (INT-202).
/// Heuristic alias match first; optional LLM pass for remaining ambiguous folders.
/// </summary>
public sealed class PlanSuiteUnitMapper(
    IAzureDevOpsClient ado,
    ILlmClientFactory llmFactory,
    QaDbContext db,
    ILogger<PlanSuiteUnitMapper> log) : IPlanSuiteUnitMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PlanSuiteUnitMap MapSuites(int planId, IReadOnlyList<AdoTestSuite> suites)
    {
        var byId = suites.ToDictionary(s => s.Id);
        var entries = new List<PlanSuiteUnitMapEntry>();
        foreach (var s in suites.OrderBy(s => s.Id))
        {
            var path = BuildPath(s, byId);
            var (unit, ambiguous, notes) = MatchUnit(s.Name, path);
            entries.Add(new PlanSuiteUnitMapEntry
            {
                SuiteId = s.Id,
                SuiteName = s.Name,
                SuitePath = path,
                AutomationUnit = unit,
                Ambiguous = ambiguous,
                Notes = notes,
            });
        }

        return new PlanSuiteUnitMap
        {
            TestPlanId = planId,
            MappedAtUtc = DateTime.UtcNow,
            Entries = entries,
        };
    }

    public async Task<PlanSuiteUnitMap> FetchAndMapAsync(
        int planId, bool useAiForAmbiguous = true, CancellationToken ct = default)
    {
        var suites = await ado.GetTestSuitesAsync(planId, ct);
        var map = MapSuites(planId, suites);
        if (useAiForAmbiguous && map.Entries.Any(e => e.Ambiguous))
            map = await ResolveAmbiguousWithAiAsync(map, ct);
        return map;
    }

    public async Task<PlanSuiteUnitMap> MapAndPersistSessionAsync(
        int sessionId, bool useAiForAmbiguous = true, CancellationToken ct = default)
    {
        var session = await db.ApiTestSessions.FindAsync([sessionId], ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        if (session.TestPlanId is null or <= 0)
            throw new InvalidOperationException("Session has no TestPlanId — cannot map suites.");

        var map = await FetchAndMapAsync(session.TestPlanId.Value, useAiForAmbiguous, ct);
        session.SuiteUnitMapJson = JsonSerializer.Serialize(map, JsonOpts);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        log.LogInformation(
            "Persisted suite→unit map for session {SessionId} plan {PlanId}: {Total} suites, {Ambiguous} ambiguous",
            sessionId, map.TestPlanId, map.Entries.Count, map.Entries.Count(e => e.Ambiguous));
        return map;
    }

    /// <summary>Prefer name-only hits; fall back to full path. Longer alias wins; score ties → ambiguous.</summary>
    internal static (string? Unit, bool Ambiguous, string? Notes) MatchUnit(string suiteName, string suitePath)
    {
        var nameHits = ScoreHits(suiteName);
        if (nameHits.Count == 1)
            return (nameHits[0].Slug, false, null);
        if (nameHits.Count > 1)
        {
            nameHits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (nameHits[0].Score > nameHits[1].Score)
                return (nameHits[0].Slug, false, null);
            return (null, true, $"tied name aliases: {string.Join(", ", nameHits.Where(h => h.Score == nameHits[0].Score).Select(h => h.Slug))}");
        }

        var pathHits = ScoreHits(suitePath);
        if (pathHits.Count == 0)
            return (null, true, "no alias match");
        pathHits.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (pathHits.Count > 1 && pathHits[0].Score == pathHits[1].Score)
            return (null, true, $"tied path aliases: {string.Join(", ", pathHits.Where(h => h.Score == pathHits[0].Score).Select(h => h.Slug))}");
        return (pathHits[0].Slug, false, null);
    }

    private static List<(string Slug, int Score)> ScoreHits(string text)
    {
        var hay = text.ToLowerInvariant();
        var hits = new List<(string Slug, int Score)>();
        foreach (var (slug, aliases) in AutomationUnitCatalog.Units)
        {
            var best = 0;
            foreach (var a in aliases)
            {
                var alias = a.ToLowerInvariant();
                if (hay.Contains(alias, StringComparison.Ordinal))
                    best = Math.Max(best, alias.Length);
            }
            if (best > 0)
                hits.Add((slug, best));
        }
        return hits;
    }

    internal static string BuildPath(AdoTestSuite suite, IReadOnlyDictionary<int, AdoTestSuite> byId)
    {
        var parts = new List<string>();
        var cur = suite;
        var guard = 0;
        while (guard++ < 64)
        {
            parts.Add(cur.Name);
            if (cur.ParentId is not int pid || !byId.TryGetValue(pid, out var parent))
                break;
            if (parent.Id == cur.Id) break;
            cur = parent;
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private async Task<PlanSuiteUnitMap> ResolveAmbiguousWithAiAsync(PlanSuiteUnitMap map, CancellationToken ct)
    {
        var ambiguous = map.Entries.Where(e => e.Ambiguous).ToList();
        if (ambiguous.Count == 0) return map;

        var sb = new StringBuilder();
        sb.AppendLine("Map each ADO Test Plan suite folder to exactly one automation unit slug, or null if unclear.");
        sb.AppendLine("Known unit slugs:");
        sb.AppendLine(string.Join(", ", AutomationUnitCatalog.Slugs));
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON array only: [{\"suiteId\":123,\"automationUnit\":\"deals\"|null,\"ambiguous\":true|false,\"notes\":\"...\"}]");
        sb.AppendLine();
        sb.AppendLine("Suites to map:");
        foreach (var e in ambiguous)
            sb.AppendLine($"- suiteId={e.SuiteId} path=\"{e.SuitePath}\" name=\"{e.SuiteName}\"");

        try
        {
            var llm = await llmFactory.CreateAsync(ct);
            var node = await JsonUtils.CompleteJsonAsync(
                llm,
                "You map Azure Test Plan suite folders to product automation unit slugs. Output JSON array only.",
                sb.ToString(),
                opener: '[',
                cancellationToken: ct);
            if (node is not System.Text.Json.Nodes.JsonArray arr) return map;

            var byId = map.Entries.ToDictionary(e => e.SuiteId);
            foreach (var item in arr)
            {
                if (item is not System.Text.Json.Nodes.JsonObject obj) continue;
                if (!obj.TryGetPropertyValue("suiteId", out var idNode) || idNode is null) continue;
                int sid;
                try { sid = idNode.GetValue<int>(); }
                catch { continue; }
                if (!byId.TryGetValue(sid, out var entry)) continue;

                string? unit = null;
                if (obj.TryGetPropertyValue("automationUnit", out var uNode)
                    && uNode is not null
                    && uNode.GetValueKind() == JsonValueKind.String)
                {
                    var u = uNode.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        var match = AutomationUnitCatalog.Slugs.FirstOrDefault(s =>
                            s.Equals(u, StringComparison.OrdinalIgnoreCase));
                        if (match is not null) unit = match;
                    }
                }

                var stillAmbiguous = unit is null;
                if (obj.TryGetPropertyValue("ambiguous", out var aNode) && aNode is not null
                    && aNode.GetValueKind() == JsonValueKind.True)
                    stillAmbiguous = true;

                entry.AutomationUnit = unit;
                entry.Ambiguous = stillAmbiguous || unit is null;
                if (obj.TryGetPropertyValue("notes", out var nNode) && nNode is not null
                    && nNode.GetValueKind() == JsonValueKind.String)
                    entry.Notes = nNode.GetValue<string>();
                else if (!entry.Ambiguous)
                    entry.Notes = "ai-mapped";
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AI suite→unit resolve failed; leaving heuristic ambiguous flags");
        }

        map.MappedAtUtc = DateTime.UtcNow;
        return map;
    }
}
