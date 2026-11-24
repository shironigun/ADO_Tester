using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    // A 30k cap cut large files (e.g. stored procedures) mid-body, so the verifier saw only the top and
    // returned false "fail" verdicts for logic further down. Files now get what is left of the total budget,
    // capped per file; ranking already puts the most relevant files first.
    private const int MaxBytesPerFile = 100_000;
    private const int MaxTotalBytes = 250_000;
    private const int MinBytesPerFile = 8_000;

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

        // Most relevant first, so the cut below (and every later byte budget) drops the least useful files.
        // A 28-file PR used to keep the first 20 in API order, which put config before the service it changed.
        changed = RankChanged(changed, StoryTerms(wi.Title, analysis?.Feature, analysis?.Module));
        if (changed.Count > MaxFiles)
        {
            var skipped = changed.Skip(MaxFiles).Select(c => c.Path[(c.Path.LastIndexOf('/') + 1)..]).ToList();
            Pub(key, EventKinds.System, EventPhases.Discover,
                $"{changed.Count} files changed; reading the {MaxFiles} most relevant. Skipped: " +
                $"{string.Join(", ", skipped.Take(8))}{(skipped.Count > 8 ? ", …" : "")}");
            changed = changed.Take(MaxFiles).ToList();
        }
        if (changed.Count == 0)
        {
            Pub(key, EventKinds.System, EventPhases.Discover, "No code anchor found — verifying against expected behavior only", EventLevels.Warn);
            return ("", "");
        }

        var diffSummary = "Files changed (anchor):\n" + string.Join("\n", changed.Select(c => $"- [{(string.IsNullOrEmpty(c.ChangeType) ? "edit" : c.ChangeType)}] {c.Path}"));

        // 3. Read changed files; collect dependency candidates from their imports.
        var contents = new Dictionary<string, string>();
        var importCands = new List<string>();
        var fromRef = new List<string>();
        var unreadable = new List<(string Name, string Why)>();
        var cut = new List<string>();
        foreach (var c in changed)
        {
            if (c.ChangeType == "delete") continue;
            var (txt, usedRef, why) = await ReadChangedFileAsync(c, ct);
            if (txt is null) { unreadable.Add((c.Path[(c.Path.LastIndexOf('/') + 1)..], why)); continue; }
            if (usedRef) fromRef.Add(c.Path[(c.Path.LastIndexOf('/') + 1)..]);
            var budget = Math.Clamp(MaxTotalBytes - contents.Values.Sum(t => t.Length), MinBytesPerFile, MaxBytesPerFile);
            if (txt.Length > budget)
            {
                cut.Add($"{c.Path[(c.Path.LastIndexOf('/') + 1)..]} ({budget:N0} of {txt.Length:N0} chars)");
                contents[c.Path] = txt[..budget] + $"\n\n/* … TRUNCATED: {txt.Length - budget:N0} more chars not shown. Do not conclude behavior is missing from the unseen part. */";
            }
            else contents[c.Path] = txt;
            foreach (var spec in BugHelpers.ExtractRelativeImports(txt))
                foreach (var cand in BugHelpers.ResolveImport(spec, c.Path))
                    if (!importCands.Contains(cand)) importCands.Add(cand);
        }
        // Say it out loud: these files are listed as changed, so a silent drop looks like "code verified, nothing found".
        if (fromRef.Count > 0)
            Pub(key, EventKinds.System, EventPhases.Discover,
                $"{fromRef.Count} file(s) are not on the branch under test; read from the PR/commit instead: {Names(fromRef)}");
        if (cut.Count > 0)
            Pub(key, EventKinds.System, EventPhases.Discover,
                $"{cut.Count} file(s) exceeded the size budget and were truncated: {string.Join(", ", cut.Take(5))}", EventLevels.Warn);
        if (unreadable.Count > 0)
            Pub(key, EventKinds.System, EventPhases.Discover,
                $"Could not read {unreadable.Count} changed file(s): {Names(unreadable.Select(u => u.Name))}. " +
                $"Verdicts that depend on them will be inconclusive. First reason: {Slice(unreadable[0].Why, 160)}",
                EventLevels.Warn);

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
            var snippet = Slice(txt, Math.Clamp(MaxTotalBytes - total, MinBytesPerFile, 30_000));
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

    // ── ranking ──────────────────────────────────────────────────────────────

    // Never the logic under test: generated code, lockfiles, binaries, snapshots, EF migrations.
    private static readonly Regex NoiseRx = new(
        @"(^|/)(bin|obj|node_modules|dist)/|/migrations/|\.designer\.cs$|\.g\.cs$|\.generated\.|modelsnapshot\.cs$|\.min\.(js|css)$|" +
        @"\.lock$|package-lock\.json$|\.(png|jpe?g|gif|svg|ico|woff2?|ttf|pdf|dll|exe|zip|snap)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Startup, DI and config wiring: rarely the behaviour a test case asserts, and it used to crowd out the real change.
    private static readonly Regex WiringRx = new(
        @"(^|/)(program|startup)\.cs$|configurationextensions|/configurations?/|appsettings[^/]*\.json$|package\.json$|\.csproj$|\.sln$|\.props$|\.targets$|" +
        @"\.ya?ml$|dockerfile|\.md$|\.resx$|\.config$|(^|/)(vite|webpack|tsconfig|eslint|jest)[^/]*$|\.gitignore$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TestRx = new(
        @"(^|/)(tests?|__tests__|specs?|e2e|mocks?|fixtures?)/|\.tests?/|\.(spec|test)\.[a-z]+$|tests?\.cs$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Behaviour-bearing code (matched on the file name), UI behaviour, and the contracts they exchange.
    private static readonly Regex LogicRx = new(
        @"service|handler|repository|controller|processor|manager|validator|command|query|usecase|workflow|saga|resolver|mapper|rule|provider|facade|orchestrator|worker|function|job",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UiRx = new(@"modal|dialog|form|slice|hook|page|view", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContractRx = new(@"request|response|dto|model|contract|schema|entity|enum|interface", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "with", "from", "into", "that", "this", "when", "then", "have", "will", "should", "able", "user", "users", "feature", "story", "support" };

    /// <summary>Words from the story (title, analysed feature/module) that, found in a file path, mark it as on-topic.
    /// Plurals are folded ("customers" → "customer") so they match typical class/file names.</summary>
    internal static IReadOnlyList<string> StoryTerms(params string?[] texts) =>
        texts.Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => Regex.Split(t!.ToLowerInvariant(), "[^a-z0-9]+"))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w)
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct().ToList();

    /// <summary>Higher = read first. Logic and contracts beat wiring, tests and generated files; paths that
    /// contain the story's own words get a boost (capped at three words so one broad term can't dominate).</summary>
    internal static int RelevanceScore(ChangedFile f, IReadOnlyList<string> terms)
    {
        var p = f.Path.Replace('\\', '/').ToLowerInvariant();
        if (NoiseRx.IsMatch(p)) return -100;
        if (f.ChangeType.Contains("delete", StringComparison.Ordinal)) return -50;
        var name = p[(p.LastIndexOf('/') + 1)..];
        var score = 0;
        if (WiringRx.IsMatch(p)) score -= 20;
        if (TestRx.IsMatch(p))
            score -= 10;   // below the code it covers; a name like "FooServiceTests" must not earn the "service" bonus
        else
        {
            if (LogicRx.IsMatch(name)) score += 10;
            if (UiRx.IsMatch(name)) score += 6;
            if (name.EndsWith(".sql", StringComparison.Ordinal)) score += 8;
            if (ContractRx.IsMatch(name)) score += 4;
        }
        score += 6 * Math.Min(3, terms.Count(t => p.Contains(t, StringComparison.Ordinal)));
        return score;
    }

    /// <summary>Most relevant first; ties keep the order the API returned (stable).</summary>
    internal static List<ChangedFile> RankChanged(IReadOnlyList<ChangedFile> files, IReadOnlyList<string> terms) =>
        files.Select((f, i) => (f, i, s: RelevanceScore(f, terms)))
            .OrderByDescending(x => x.s).ThenBy(x => x.i).Select(x => x.f).ToList();

    /// <summary>Reads a changed file on the branch under test; if that fails (e.g. the PR adds it and it isn't
    /// merged there yet) falls back to the PR/commit it came from. Returns the reason when neither works.</summary>
    private async Task<(string? Text, bool UsedRef, string Why)> ReadChangedFileAsync(ChangedFile c, CancellationToken ct)
    {
        var why = "";
        try
        {
            var t = await _ado.GetFileContentAsync(c.Path, ct: ct);
            if (!string.IsNullOrEmpty(t)) return (t, false, "");
            why = "empty on the branch under test";
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { why = e.Message; }

        if (c.Ref is not null)
        {
            try
            {
                var t = await _ado.GetFileContentAsync(c.Path, c.Ref, versionType: "commit", ct: ct);
                if (!string.IsNullOrEmpty(t)) return (t, true, "");
                why += $"; empty at {Short(c.Ref)}";
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { why += $"; at {Short(c.Ref)}: {e.Message}"; }
        }
        return (null, false, why);
    }

    private static string Names(IEnumerable<string> names)
    {
        var all = names.ToList();
        return string.Join(", ", all.Take(8)) + (all.Count > 8 ? ", …" : "");
    }

    private void Pub(string key, string kind, string? phase = null, string text = "",
        string level = EventLevels.Info, Dictionary<string, object?>? data = null)
        => _bus.Publish(key, new ConsoleEvent { Kind = kind, Phase = phase, Text = text, Level = level, Data = data });

    private static string Slice(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
