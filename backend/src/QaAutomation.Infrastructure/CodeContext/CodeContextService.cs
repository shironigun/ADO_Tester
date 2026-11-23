using System.Text.Json;
using System.Text.Json.Nodes;
using QaAutomation.Core;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Agents;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Orchestrators;

namespace QaAutomation.Infrastructure.CodeContext;

/// <summary>
/// Shared code-discovery engine extracted from <see cref="Orchestrators.BugOrchestrator"/>. Anchors
/// on linked PRs/commits, optionally runs a hint-seeded code search, reads and byte-budgets the
/// changed/related files, then runs an LLM triage pass to pick extra files. Returns a
/// (diffSummary, code) tuple for prompt injection.
/// <para>Two knobs allow caller-specific behaviour: <c>alwaysSearch</c> (Author Tests: search even
/// when linked refs exist) and <c>extraHintText</c> (append route/screen/module hints to search).</para>
/// <para>BugOrchestrator calls with <c>alwaysSearch:false</c> and <c>null</c> hint — identical to its
/// former private behaviour, preserving byte-for-byte parity on the bug pipeline.</para>
/// </summary>
public sealed class CodeContextService : ICodeContextService
{
    private const int MaxFiles = 20;
    private const int MaxBytesPerFile = 30_000;
    private const int MaxTotalBytes = 250_000;

    private readonly IAzureDevOpsClient _ado;
    private readonly IEventBus _bus;

    public CodeContextService(IAzureDevOpsClient ado, IEventBus bus)
    {
        _ado = ado;
        _bus = bus;
    }

    public async Task<(string DiffSummary, string Code)> DiscoverAsync(
        int workItemId, AdoWorkItem wi, AnalystOutput? analysis,
        IReadOnlyList<string> commitIds, IReadOnlyList<string> prIds,
        string? extraHintText, bool alwaysSearch,
        ILlmClient llm, string key, CancellationToken ct)
    {
        string repoId;
        try { repoId = await _ado.ResolveRepositoryIdAsync(ct: ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Pub(key, EventKinds.System, EventPhases.Discover, $"Repo resolve failed: {e.Message}", EventLevels.Warn);
            return ("", "");
        }

        var changed = new List<ChangedFile>();
        bool Have(string p) => changed.Any(c => c.Path == p);
        void AddChanged(IEnumerable<ChangedFile> items) { foreach (var c in items) if (!Have(c.Path)) changed.Add(c); }

        // 1. Anchor on linked + manual PRs/commits.
        IReadOnlyList<string> autoPr = [], autoCommit = [];
        try { var refs = await _ado.GetWorkItemGitRefsAsync(workItemId, ct); autoPr = refs.PullRequestIds; autoCommit = refs.CommitShas; }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        foreach (var pr in autoPr.Concat(prIds).Distinct())
        {
            try { AddChanged(await _ado.GetPullRequestDiffAsync(pr, repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"PR #{pr}: {changed.Count} changed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"PR #{pr} skipped: {e.Message}", EventLevels.Warn); }
        }
        foreach (var sha in autoCommit.Concat(commitIds).Distinct())
        {
            try { AddChanged(await _ado.GetCommitDiffAsync(sha, repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"commit {Short(sha)}: {changed.Count} changed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"commit {Short(sha)} skipped: {e.Message}", EventLevels.Warn); }
        }

        // 2. No linked refs (or alwaysSearch=true) → search + recent commits.
        if (changed.Count == 0 || alwaysSearch)
        {
            var feature = analysis?.Feature ?? wi.Title;
            var terms = BugHelpers.SearchQuery(feature, wi.Title, analysis?.Module);
            if (!string.IsNullOrWhiteSpace(extraHintText))
                terms = string.Join(" ", terms, extraHintText.Trim()).Trim();
            if (!alwaysSearch)
                Pub(key, EventKinds.Phase, EventPhases.Discover, $"No linked PRs/commits — searching for '{terms}'");
            else if (!string.IsNullOrWhiteSpace(terms))
                Pub(key, EventKinds.System, EventPhases.Discover, $"Code search: '{terms}'");
            IReadOnlyList<string> seeds = [];
            try { seeds = terms.Length > 0 ? await _ado.SearchCodeAsync(terms, 8, ct: ct) : []; Pub(key, EventKinds.Result, EventPhases.Discover, $"Code Search → {seeds.Count} seed file(s)"); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Pub(key, EventKinds.System, EventPhases.Discover, $"Code Search unavailable: {e.Message}", EventLevels.Warn); }
            foreach (var seed in seeds.Take(2))
            {
                try
                {
                    var shas = await _ado.GetCommitsForPathAsync(seed, 1, repoId, ct);
                    if (shas.Count > 0) { AddChanged(await _ado.GetCommitDiffAsync(shas[0], repoId, ct)); Pub(key, EventKinds.System, EventPhases.Discover, $"recent commit {Short(shas[0])} on {seed}"); }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            foreach (var sp in seeds)
                if (!Have(sp)) changed.Add(new ChangedFile(sp, "seed"));
        }

        if (changed.Count > MaxFiles)
            changed = changed.Take(MaxFiles).ToList();
        if (changed.Count == 0)
        {
            Pub(key, EventKinds.System, EventPhases.Discover, "No code anchor found — verifying against expected behavior only", EventLevels.Warn);
            return ("", "");
        }

        var diffSummary = "Files changed (anchor):\n" + string.Join("\n", changed.Select(c => $"- [{(string.IsNullOrEmpty(c.ChangeType) ? "edit" : c.ChangeType)}] {c.Path}"));

        // 3. Read changed files; collect dependency candidates from their imports.
        var contents = new Dictionary<string, string>();
        var importCands = new List<string>();
        foreach (var c in changed)
        {
            if (c.ChangeType == "delete") continue;
            string txt;
            try { txt = await _ado.GetFileContentAsync(c.Path, ct: ct); } catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            if (string.IsNullOrEmpty(txt)) continue;
            contents[c.Path] = Slice(txt, MaxBytesPerFile);
            foreach (var spec in BugHelpers.ExtractRelativeImports(txt))
                foreach (var cand in BugHelpers.ResolveImport(spec, c.Path))
                    if (!importCands.Contains(cand)) importCands.Add(cand);
        }

        // 4. Callers (regression surface): symbol search on each changed file's base name.
        var callerCands = new List<string>();
        foreach (var path in contents.Keys.Take(5).ToList())
        {
            var fname = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var symbol = fname.Contains('.') ? fname[..fname.LastIndexOf('.')] : fname;
            try
            {
                foreach (var h in await _ado.SearchCodeAsync(symbol, 6, ct: ct))
                    if (!contents.ContainsKey(h) && !callerCands.Contains(h)) callerCands.Add(h);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { break; }
        }

        // 5. Triage: one LLM pass picks the extra files actually needed.
        var candidateLines = importCands.Take(30).Select(p => $"[dependency] {p}")
            .Concat(callerCands.Take(30).Select(p => $"[caller] {p}")).ToList();
        var roleOf = new Dictionary<string, string>();
        foreach (var p in importCands) roleOf[p] = "dependency";
        foreach (var p in callerCands) roleOf[p] = "caller";
        var selectedExtra = candidateLines.Count > 0 ? await TriageFilesAsync(llm, diffSummary, wi, analysis, candidateLines, key, ct) : new List<string>();

        // 6. Assemble role-tagged block within byte budget.
        var blocks = contents.Select(kv => $"=== [CHANGED] {kv.Key} ===\n{kv.Value}").ToList();
        var total = contents.Values.Sum(t => t.Length);
        var fetched = new HashSet<string>(contents.Keys);
        foreach (var path in selectedExtra)
        {
            if (total >= MaxTotalBytes || fetched.Contains(path)) continue;
            string txt;
            try { txt = await _ado.GetFileContentAsync(path, ct: ct); } catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            if (string.IsNullOrEmpty(txt)) continue;
            var snippet = Slice(txt, MaxBytesPerFile);
            blocks.Add($"=== [{(roleOf.TryGetValue(path, out var r) ? r : "related").ToUpperInvariant()}] {path} ===\n{snippet}");
            total += snippet.Length;
            fetched.Add(path);
        }

        Pub(key, EventKinds.Result, EventPhases.Discover,
            $"Anchored on {contents.Count} changed file(s); expanded to {fetched.Count} total · {total:N0} chars",
            EventLevels.Success, new() { ["files"] = fetched.OrderBy(x => x, StringComparer.Ordinal).ToList() });
        return (diffSummary, string.Join("\n\n", blocks));
    }

    private async Task<List<string>> TriageFilesAsync(
        ILlmClient llm, string diffSummary, AdoWorkItem wi, AnalystOutput? analysis,
        List<string> candidateLines, string key, CancellationToken ct)
    {
        var feature = analysis is not null ? $"{analysis.Module} / {analysis.Feature}" : wi.Title;
        const string system =
            "You are a senior engineer scoping a REGRESSION review. Given the change under test " +
            "and candidate files (dependencies the change imports, and callers that reference it), " +
            "pick ONLY the files needed to judge whether the change breaks the expected behavior. " +
            "Prefer callers and direct dependencies; ignore clearly unrelated files. " +
            "Return ONLY a JSON array of file path strings (max 10), no prose.";
        var user =
            $"## Change under test\n{diffSummary}\n\n" +
            $"## Feature\n{feature}\n\n" +
            "## Candidate files (role-tagged)\n" + string.Join("\n", candidateLines) +
            "\n\nReturn the JSON array of paths to read.";
        try
        {
            var raw = await llm.CompleteAsync(system, user, cancellationToken: ct);
            if (JsonNode.Parse(JsonUtils.StripFences(raw, '[')) is JsonArray arr)
            {
                var picked = arr.Where(x => x?.GetValueKind() == JsonValueKind.String).Select(x => x!.GetValue<string>()).Take(10).ToList();
                Pub(key, EventKinds.Result, EventPhases.Triage, $"Triage selected {picked.Count} extra file(s) to read");
                return picked;
            }
        }
        catch (Exception e)
        {
            Pub(key, EventKinds.System, EventPhases.Triage, $"Triage skipped ({e.Message}) — using deterministic picks", EventLevels.Warn);
        }
        return candidateLines.Select(ln => ln.Split("] ", 2)[^1]).Take(8).ToList();
    }

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private static string Slice(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
