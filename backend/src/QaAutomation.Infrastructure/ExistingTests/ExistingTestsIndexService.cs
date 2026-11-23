using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Infrastructure.ExistingTests;

/// <summary>
/// Cached scan/index of ADO test cases for the Tester dedup layer. Persists to PostgreSQL with a
/// 24-hour staleness TTL; <c>forceRescan</c> bypasses the cache. Singleton: uses
/// <see cref="IServiceScopeFactory"/> for all DB access.
/// </summary>
public sealed class ExistingTestsIndexService : IExistingTestsIndexService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","is","are","was","were","be","been","being","have","has","had",
        "do","does","did","will","would","could","should","may","might","must","can",
        "it","its","in","on","at","to","for","of","and","or","not","with","from","by",
        "when","then","if","as","verify","that","this","those","these","which","what",
        "how","where","who","user","test","check","ensure","confirm"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAzureDevOpsClient _ado;
    private readonly ITextSearchProvider _textSearch;

    public ExistingTestsIndexService(
        IServiceScopeFactory scopeFactory, IAzureDevOpsClient ado, ITextSearchProvider textSearch)
    {
        _scopeFactory = scopeFactory;
        _ado = ado;
        _textSearch = textSearch;
    }

    public async Task<TestPlanScan> EnsureIndexAsync(int planId, int? suiteId, bool forceRescan, CancellationToken ct)
    {
        // Cache check — skip on forceRescan.
        if (!forceRescan)
        {
            using var rScope = _scopeFactory.CreateScope();
            var rDb = rScope.ServiceProvider.GetRequiredService<QaDbContext>();
            var cached = await rDb.TestPlanScans
                .Where(s => s.PlanId == planId && s.SuiteId == suiteId)
                .OrderByDescending(s => s.ScannedAt)
                .FirstOrDefaultAsync(ct);
            if (cached is not null && DateTime.UtcNow - cached.ScannedAt < StaleAfter)
                return cached;
        }

        // Resolve the suite to scan (find root when suiteId is null).
        int scanSuiteId;
        if (suiteId.HasValue)
        {
            scanSuiteId = suiteId.Value;
        }
        else
        {
            var suites = await _ado.GetTestSuitesAsync(planId, ct);
            var root = suites.FirstOrDefault(s => s.ParentId is null)
                ?? suites.MinBy(s => s.Id)
                ?? throw new InvalidOperationException($"Test plan {planId} has no suites.");
            scanSuiteId = root.Id;
        }

        var cases = await _ado.GetSuiteTestCasesAsync(planId, scanSuiteId, recurse: true, ct);
        var patternSummary = BuildTitlePatternSummary(cases.Select(c => c.Title).ToList());

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        // Remove stale scans + their FTS rows.
        var oldScans = await db.TestPlanScans
            .Where(s => s.PlanId == planId && s.SuiteId == suiteId)
            .ToListAsync(ct);
        if (oldScans.Count > 0)
        {
            var oldIds = oldScans.Select(s => s.Id).ToList();
            var oldEntries = await db.ExistingTestCaseIndexEntries
                .Where(e => oldIds.Contains(e.ScanId)).ToListAsync(ct);
            db.ExistingTestCaseIndexEntries.RemoveRange(oldEntries);
            db.TestPlanScans.RemoveRange(oldScans);
            await db.SaveChangesAsync(ct);
            foreach (var sid in oldIds)
                await _textSearch.DeleteTestCasesForScanAsync(db, sid, ct);
        }

        // Insert new scan.
        var scan = new TestPlanScan
        {
            PlanId = planId, SuiteId = suiteId,
            ScannedAt = DateTime.UtcNow, CaseCount = cases.Count,
            TitlePatternSummary = patternSummary,
        };
        db.TestPlanScans.Add(scan);
        await db.SaveChangesAsync(ct);

        // Insert index entries.
        foreach (var c in cases)
        {
            var sig = NormalizeTitle(c.Title);
            var steps = BuildStepsSummary(c);
            db.ExistingTestCaseIndexEntries.Add(new ExistingTestCaseIndexEntry
            {
                ScanId = scan.Id, AdoTestCaseId = c.Id, SuitePath = "",
                Title = c.Title, TitleSignature = sig,
                StepsSummary = steps,
                StepsHash = steps is not null ? ComputeMd5(steps) : null,
            });
        }
        await db.SaveChangesAsync(ct);

        // Populate the full-text index (provider-specific store).
        var entries = await db.ExistingTestCaseIndexEntries
            .Where(e => e.ScanId == scan.Id).ToListAsync(ct);
        foreach (var e in entries)
            await _textSearch.IndexTestCaseAsync(db, scan.Id, e.Id, e.Title, e.StepsSummary ?? "", ct);

        return scan;
    }

    public async Task<TestPlanScan?> GetScanStatusAsync(int planId, int? suiteId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        return await db.TestPlanScans
            .Where(s => s.PlanId == planId && s.SuiteId == suiteId)
            .OrderByDescending(s => s.ScannedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> BuildTesterContextAsync(
        int planId, int? suiteId, AnalystOutput? analysis, int byteBudget, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var scan = await db.TestPlanScans
            .Where(s => s.PlanId == planId && s.SuiteId == suiteId)
            .OrderByDescending(s => s.ScannedAt)
            .FirstOrDefaultAsync(ct);
        if (scan is null) return null;

        var entries = await db.ExistingTestCaseIndexEntries
            .Where(e => e.ScanId == scan.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        if (entries.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"## Existing Test Cases in ADO (do NOT duplicate) — {entries.Count} cases · scanned {scan.ScannedAt:yyyy-MM-dd HH:mm} UTC");
        if (!string.IsNullOrEmpty(scan.TitlePatternSummary))
            sb.AppendLine(scan.TitlePatternSummary);
        sb.AppendLine();
        sb.AppendLine("### All existing titles:");
        foreach (var e in entries)
            sb.AppendLine($"- [{e.AdoTestCaseId}] {e.Title}");

        // PostgreSQL ts_rank-ranked full steps for the most feature-relevant cases.
        if (analysis is not null)
        {
            var feature = $"{analysis.Module} {analysis.Feature}";
            var relevant = await _textSearch.SearchTestCaseEntryIdsAsync(db, scan.Id, feature, 10, ct);
            if (relevant.Count > 0)
            {
                var byId = entries.ToDictionary(e => e.Id);
                sb.AppendLine();
                sb.AppendLine("### Most relevant existing cases (full steps):");
                foreach (var eid in relevant)
                {
                    if (!byId.TryGetValue(eid, out var r)) continue;
                    sb.AppendLine($"#### [{r.AdoTestCaseId}] {r.Title}");
                    if (!string.IsNullOrEmpty(r.StepsSummary))
                        sb.AppendLine(r.StepsSummary);
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Instruction: produce ONLY net-new or complementary coverage. Skip scenarios already covered above (you may cite an existing TC id instead).");

        var result = sb.ToString();
        return result.Length > byteBudget ? result[..byteBudget] : result;
    }

    public async Task<IReadOnlyList<DedupResult>> FindDuplicatesAsync(
        int planId, int? suiteId, IReadOnlyList<DedupTestCase> cases, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var scan = await db.TestPlanScans
            .Where(s => s.PlanId == planId && s.SuiteId == suiteId)
            .OrderByDescending(s => s.ScannedAt)
            .FirstOrDefaultAsync(ct);
        if (scan is null) return [];

        var entries = await db.ExistingTestCaseIndexEntries
            .Where(e => e.ScanId == scan.Id).ToListAsync(ct);

        var results = new List<DedupResult>();
        foreach (var tc in cases)
        {
            var sig = NormalizeTitle(tc.Title);
            var sigWords = sig.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            // Exact signature match → clear duplicate.
            var exact = entries.FirstOrDefault(e => e.TitleSignature == sig && e.TitleSignature.Length > 0);
            if (exact is not null)
            {
                results.Add(new DedupResult(tc.Title, true, exact.Title, exact.AdoTestCaseId));
                continue;
            }

            // Jaccard similarity on normalized title words.
            ExistingTestCaseIndexEntry? bestMatch = null;
            double bestScore = 0;
            foreach (var e in entries)
            {
                var eWords = e.TitleSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                if (eWords.Count == 0 || sigWords.Count == 0) continue;
                var intersection = sigWords.Count(w => eWords.Contains(w));
                var union = sigWords.Count + eWords.Count - intersection;
                if (union <= 0) continue;
                var jaccard = (double)intersection / union;
                if (jaccard > bestScore) { bestScore = jaccard; bestMatch = e; }
            }

            if (bestScore >= 0.75 && bestMatch is not null)
                results.Add(new DedupResult(tc.Title, false, bestMatch.Title, bestMatch.AdoTestCaseId));
        }

        return results;
    }

    private static string NormalizeTitle(string title)
    {
        var clean = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Stopwords.Contains(w) && w.Length > 1)
            .ToArray();
        return string.Join(" ", words);
    }

    private static string? BuildStepsSummary(LinkedTestCase tc)
    {
        if (tc.Steps.Count == 0) return null;
        var combined = string.Join(" | ", tc.Steps.Select(s => s.Action));
        return combined.Length > 1000 ? combined[..1000] : combined;
    }

    private static string? BuildTitlePatternSummary(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0) return null;
        var prefixes = titles
            .Select(t => t.Contains(" - ") ? t[..t.IndexOf(" - ", StringComparison.Ordinal)] : null)
            .Where(p => p is not null)
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"\"{g.Key}\" ({g.Count()}x)")
            .ToList();
        return prefixes.Count == 0 ? null : "Common title prefixes: " + string.Join(", ", prefixes);
    }

    private static string ComputeMd5(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
