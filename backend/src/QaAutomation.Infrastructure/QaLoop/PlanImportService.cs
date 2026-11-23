using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// INT-203 — import Plan TCs for the selected automation unit into session drafts.
/// Thin (empty/title-only steps) → Draft + AdoTestCaseId (improve). Rich → linked Draft.
/// Gaps from selected analysis rows without a Plan title match → Draft with no AdoTestCaseId.
/// Re-run skips any AdoTestCaseId already present on the session.
/// </summary>
public sealed class PlanImportService(
    IAzureDevOpsClient ado,
    IPlanSuiteUnitMapper mapper,
    QaDbContext db,
    ILogger<PlanImportService> log) : IPlanImportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task<PlanImportMeta> ImportAsync(
        int sessionId, string? selectedUnit = null, bool remapIfMissing = true, CancellationToken ct = default)
    {
        var session = await db.ApiTestSessions
            .Include(s => s.Drafts)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.TestPlanId is null or <= 0)
            throw new InvalidOperationException("Session has no TestPlanId.");

        var unit = ResolveUnit(session, selectedUnit);
        var map = await EnsureMapAsync(session, remapIfMissing, ct);

        var suiteIds = map.Entries
            .Where(e => !e.Ambiguous
                        && e.AutomationUnit is not null
                        && e.AutomationUnit.Equals(unit, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.SuiteId)
            .Distinct()
            .ToList();

        var casesById = new Dictionary<int, LinkedTestCase>();
        foreach (var sid in suiteIds)
        {
            // recurse:false — map already lists every suite under the unit
            var cases = await ado.GetSuiteTestCasesAsync(session.TestPlanId.Value, sid, recurse: false, ct);
            foreach (var tc in cases)
                casesById.TryAdd(tc.Id, tc);
        }

        var existingAdoIds = session.Drafts
            .Where(d => d.AdoTestCaseId is not null)
            .Select(d => d.AdoTestCaseId!.Value)
            .ToHashSet();

        var existingTitles = session.Drafts
            .Select(d => NormalizeTitle(d.Title))
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var sort = session.Drafts.Count == 0 ? 0 : session.Drafts.Max(d => d.SortOrder) + 1;
        var meta = ApplyImportedCases(
            db, session.Id, casesById.Values, ExtractGapTitles(session.AnalysisJson),
            existingAdoIds, existingTitles, sort, now);
        meta.SelectedUnit = unit;
        meta.LastImportAtUtc = now;

        session.PlanImportMetaJson = JsonSerializer.Serialize(meta, JsonOpts);
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Plan import session {SessionId} unit={Unit}: thin={Thin} rich={Rich} gaps={Gaps} suites={Suites}",
            sessionId, unit, meta.ThinImproved, meta.RichLinked, meta.GapsCreated, suiteIds.Count);
        return meta;
    }

    /// <summary>Core import + dedup (unit-testable without ADO).</summary>
    internal static PlanImportMeta ApplyImportedCases(
        QaDbContext db,
        int sessionId,
        IEnumerable<LinkedTestCase> cases,
        IReadOnlyList<string> gapTitles,
        HashSet<int> existingAdoIds,
        HashSet<string> existingTitles,
        int sortStart,
        DateTime now)
    {
        var meta = new PlanImportMeta();
        var sort = sortStart;
        var caseList = cases.OrderBy(c => c.Id).ToList();

        foreach (var tc in caseList)
        {
            if (existingAdoIds.Contains(tc.Id))
                continue;

            var thin = IsThin(tc);
            db.ApiTestDrafts.Add(BuildImportedDraft(sessionId, tc, thin, sort++, now));
            existingAdoIds.Add(tc.Id);
            existingTitles.Add(NormalizeTitle(tc.Title));
            if (thin) meta.ThinImproved++;
            else meta.RichLinked++;
        }

        foreach (var gapTitle in gapTitles)
        {
            var norm = NormalizeTitle(gapTitle);
            if (norm.Length == 0) continue;
            if (existingTitles.Contains(norm)) continue;
            if (caseList.Any(c => NormalizeTitle(c.Title) == norm)) continue;

            db.ApiTestDrafts.Add(new ApiTestDraft
            {
                SessionId = sessionId,
                Level = TestLevel.Endpoint,
                Title = gapTitle,
                Preconditions = "",
                StepsJson = "[]",
                SpecCode = "",
                Tags = JsonSerializer.Serialize(new[] { "import-gap" }),
                Status = ApiDraftStatus.Draft,
                AdoTestCaseId = null,
                SortOrder = sort++,
                CreatedAt = now,
                UpdatedAt = now,
            });
            existingTitles.Add(norm);
            meta.GapsCreated++;
        }

        return meta;
    }

    internal static string ResolveUnit(ApiTestSession session, string? overrideUnit)
    {
        if (!string.IsNullOrWhiteSpace(overrideUnit))
            return overrideUnit.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(session.FrsSlug))
            return session.FrsSlug.Trim().ToLowerInvariant();
        // Module is often "Deals" — kebab-ish
        var m = session.Module?.Trim() ?? "";
        if (m.Length == 0) return "deals";
        return Regex.Replace(m.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    internal static bool IsThin(LinkedTestCase tc)
    {
        if (tc.Steps.Count == 0) return true;
        return tc.Steps.All(s => string.IsNullOrWhiteSpace(s.Action) && string.IsNullOrWhiteSpace(s.Expected));
    }

    internal static string NormalizeTitle(string title)
    {
        var clean = Regex.Replace((title ?? "").ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        return string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Selected SurfaceMatrix rows with Status=gap → candidate gap draft titles.</summary>
    internal static IReadOnlyList<string> ExtractGapTitles(string? analysisJson)
    {
        if (string.IsNullOrWhiteSpace(analysisJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(analysisJson);
            if (!doc.RootElement.TryGetProperty("Rows", out var rows)
                && !doc.RootElement.TryGetProperty("rows", out rows))
                return [];
            if (rows.ValueKind != JsonValueKind.Array) return [];

            var list = new List<string>();
            foreach (var r in rows.EnumerateArray())
            {
                var selected = r.TryGetProperty("Selected", out var sel) && sel.ValueKind == JsonValueKind.True
                    || r.TryGetProperty("selected", out sel) && sel.ValueKind == JsonValueKind.True;
                if (!selected) continue;

                var status = r.TryGetProperty("Status", out var st) ? st.GetString()
                    : r.TryGetProperty("status", out st) ? st.GetString() : null;
                if (!string.Equals(status, "gap", StringComparison.OrdinalIgnoreCase)) continue;

                var label = r.TryGetProperty("Label", out var lb) ? lb.GetString()
                    : r.TryGetProperty("label", out lb) ? lb.GetString() : null;
                if (!string.IsNullOrWhiteSpace(label))
                    list.Add(label!);
            }
            return list;
        }
        catch { return []; }
    }

    private async Task<PlanSuiteUnitMap> EnsureMapAsync(
        ApiTestSession session, bool remapIfMissing, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(session.SuiteUnitMapJson))
        {
            var existing = JsonSerializer.Deserialize<PlanSuiteUnitMap>(session.SuiteUnitMapJson, JsonOpts);
            if (existing is not null && existing.Entries.Count > 0)
                return existing;
        }
        if (!remapIfMissing)
            throw new InvalidOperationException("SuiteUnitMapJson is empty — run plan-map first.");
        return await mapper.MapAndPersistSessionAsync(session.Id, useAiForAmbiguous: false, ct);
    }

    private static ApiTestDraft BuildImportedDraft(int sessionId, LinkedTestCase tc, bool thin, int sort, DateTime now)
    {
        var stepsJson = JsonSerializer.Serialize(
            tc.Steps.Select(s => new { action = s.Action, expected = s.Expected }));
        return new ApiTestDraft
        {
            SessionId = sessionId,
            Level = InferLevel(tc.Title),
            Title = tc.Title,
            Preconditions = tc.Preconditions ?? "",
            StepsJson = stepsJson,
            SpecCode = "",
            Tags = JsonSerializer.Serialize(new[] { thin ? "import-thin" : "import-rich" }),
            Status = ApiDraftStatus.Draft,
            AdoTestCaseId = tc.Id,
            SortOrder = sort,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static TestLevel InferLevel(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("contract") || t.Contains("schema")) return TestLevel.Contract;
        if (t.Contains("workflow") || t.Contains("e2e")) return TestLevel.Workflow;
        return TestLevel.Endpoint;
    }
}
