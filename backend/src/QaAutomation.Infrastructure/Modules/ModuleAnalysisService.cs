using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Infrastructure.Modules;

/// <summary>
/// Track A module run. Reads a module's source (ADO repo+branch by default, or a local SourceRoot) and
/// the existing test cases of a plan/suite (+ sub-suites), then runs the <see cref="ModuleAnalyzerAgent"/>
/// once to discover all flows and map coverage. Stores a whole-module KB doc (FTS-indexed) and returns the
/// coverage map. Spec generation happens on demand per flow afterwards. Scoped (uses the KB service).
/// </summary>
public sealed class ModuleAnalysisService : IModuleAnalysisService
{
    private const int MaxAdoFiles = 80;
    private const int AdoFetchConcurrency = 8;
    private static readonly string[] SkipDirs = { "node_modules", "bin", "obj", ".git", "dist", "build", "coverage" };

    private readonly ILlmClientFactory _llmFactory;
    private readonly IKnowledgebaseService _kb;
    private readonly IAzureDevOpsClient _ado;
    private readonly IGenerationService _gen;
    private readonly IOptionsMonitor<QaLoopOptions> _qaLoop;
    private readonly ILogger<ModuleAnalysisService> _log;

    public ModuleAnalysisService(
        ILlmClientFactory llmFactory, IKnowledgebaseService kb, IAzureDevOpsClient ado,
        IGenerationService gen, IOptionsMonitor<QaLoopOptions> qaLoop, ILogger<ModuleAnalysisService> log)
    {
        _llmFactory = llmFactory;
        _kb = kb;
        _ado = ado;
        _gen = gen;
        _qaLoop = qaLoop;
        _log = log;
    }

    public async Task<ModuleAnalyzeResult> AnalyzeAsync(int projectId, ModuleAnalyzeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new ArgumentException("Module is required.");
        if (request.PlanId <= 0 || request.SuiteId <= 0)
            throw new ArgumentException("PlanId and SuiteId are required.");

        // 1) Existing coverage: the plan/suite (+ sub-suites) test cases.
        var cases = await _ado.GetSuiteTestCasesAsync(request.PlanId, request.SuiteId, request.Recurse, ct);
        var titleOf = cases.ToDictionary(c => c.Id, c => c.Title);
        _log.LogInformation("Module '{Module}': {Cases} existing test case(s) in plan {Plan}/suite {Suite}",
            request.Module, cases.Count, request.PlanId, request.SuiteId);

        // 2) Module source.
        var (bundle, filesRead) = await GatherSourceAsync(
            request.Module, request.Repository, request.Branch, request.SourceRoot,
            request.MaxFileChars, request.MaxBundleChars, ct);
        if (filesRead == 0)
            throw new InvalidOperationException("No source files were read for the module (check repo/branch or SourceRoot).");

        // 3) One LLM pass: discover flows + map coverage.
        var llm = await _llmFactory.CreateAsync(ct);
        _log.LogInformation("Module '{Module}' analysis using {Provider}: {Files} files, {Chars} chars, {Cases} cases",
            request.Module, llm.ProviderName, filesRead, bundle.Length, cases.Count);
        var flowsRaw = await new ModuleAnalyzerAgent(llm).AnalyzeAsync(request.Module, bundle, cases, cancellationToken: ct);

        // 4) Map to the coverage result; valid covering ids are those that exist in the suite.
        var flows = new List<ModuleFlow>();
        foreach (var f in flowsRaw)
        {
            var ids = f.CoveredByTestCaseIds.Where(titleOf.ContainsKey).Distinct().ToList();
            flows.Add(new ModuleFlow
            {
                Name = f.Name,
                Summary = f.Summary,
                Covered = ids.Count > 0,
                CoveringTestCaseIds = ids,
                CoveringTestCaseTitles = ids.Select(id => titleOf[id]).ToList(),
            });
        }

        // 5) Store the whole-module KB doc (all flows + coverage), FTS-indexed for grounding/search.
        var fileName = $"{Slug(request.Module)}-module-map.md";
        var mapMd = ModuleAnalyzerAgent.RenderModuleMap(request.Module, flowsRaw, id => titleOf.GetValueOrDefault(id));
        await StoreAsync(projectId, fileName, mapMd, ct);

        return new ModuleAnalyzeResult
        {
            Module = request.Module,
            ExistingTestCaseCount = cases.Count,
            FilesRead = filesRead,
            FlowCount = flows.Count,
            CoveredCount = flows.Count(x => x.Covered),
            GapCount = flows.Count(x => !x.Covered),
            KbDocFileName = fileName,
            Flows = flows,
        };
    }

    /// <summary>On-demand spec for one analyzed flow: read the module source, produce the flow's DETAILED KB
    /// doc, then emit + write the spec. A COVERED flow tags its first existing test case id; a GAP (no covering
    /// ids) authors and creates a new ADO Test Case in the plan/suite (and links the story) and tags it.</summary>
    public async Task<ModuleFlowSpecResult> GenerateFlowSpecAsync(
        int projectId, ModuleFlowSpecRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new ArgumentException("Module is required.");
        if (string.IsNullOrWhiteSpace(request.FlowName))
            throw new ArgumentException("FlowName is required.");

        var isGap = request.CoveringTestCaseIds.Count == 0;
        if (isGap && (request.PlanId <= 0 || request.SuiteId <= 0))
            throw new ArgumentException("PlanId and SuiteId are required to create a test case for a gap flow.");

        // 1) Module source (same gathering as analyze).
        var (bundle, filesRead) = await GatherSourceAsync(
            request.Module, request.Repository, request.Branch, request.SourceRoot,
            request.MaxFileChars, request.MaxBundleChars, ct);
        if (filesRead == 0)
            throw new InvalidOperationException("No source files were read for the module (check repo/branch or SourceRoot).");

        // 2) Detailed per-flow KB (deferred from analyze, generated now), grounded in the source +
        //    any covering test-case titles. Stored + FTS-indexed so the spec (and future runs) can ground on it.
        var llm = await _llmFactory.CreateAsync(ct);
        _log.LogInformation("Flow spec '{Flow}' ({Kind}) for module '{Module}' using {Provider}: {Files} files",
            request.FlowName, isGap ? "gap" : "covered", request.Module, llm.ProviderName, filesRead);
        var kbMarkdown = await new KbExtractorAgent(llm).ExtractFlowAsync(
            request.Module, request.FlowName, bundle, request.CoveringTestCaseTitles, cancellationToken: ct);
        var kbFileName = $"{Slug(request.Module)}-{Slug(request.FlowName)}.md";
        await StoreAsync(projectId, kbFileName, kbMarkdown, ct);

        // 3) Emit the spec via the Generator. Covered => tag the existing id; gap => author + create a new TC.
        var specReq = new GenerateSpecRequest
        {
            Module = request.Module,
            ScenarioTitle = request.FlowName,
            ScenarioIntent = request.FlowSummary,
            KbFlowMarkdown = kbMarkdown,
            Smoke = request.Smoke,
            OutputRoot = request.OutputRoot,
            Write = !string.IsNullOrWhiteSpace(request.OutputRoot),
            AdoTestCaseId = isGap ? null : request.CoveringTestCaseIds[0],
            CreateAdoTestCase = isGap,
            TestPlanId = isGap ? request.PlanId : null,
            TestSuiteId = isGap ? request.SuiteId : null,
            StoryId = isGap ? request.StoryId : null,
        };
        var spec = await _gen.GenerateE2eSpecAsync(projectId, specReq, ct);

        return new ModuleFlowSpecResult
        {
            FlowName = request.FlowName,
            WasGap = isGap,
            KbDocFileName = kbFileName,
            Spec = spec,
        };
    }

    private async Task<KnowledgebaseDocument> StoreAsync(int projectId, string fileName, string markdown, CancellationToken ct)
    {
        var existing = await _kb.ListAsync(projectId, ct);
        foreach (var d in existing.Where(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            await _kb.DeleteAsync(d.Id, ct);
        return await _kb.UploadAsync(projectId, fileName, markdown, cancellationToken: ct);
    }

    // ── source gathering (ADO repo+branch by default, else local clone) ──────────

    private async Task<(string Bundle, int FilesRead)> GatherSourceAsync(
        string module, string? repository, string? branch, string sourceRoot,
        int maxFileChars, int maxBundleChars, CancellationToken ct)
    {
        var dirs = new[] { _qaLoop.CurrentValue.FrontendComponentsDir(module), _qaLoop.CurrentValue.FrontendTestsDir(module) };
        var exts = new[] { ".ts", ".tsx" };

        if (!string.IsNullOrWhiteSpace(sourceRoot))
            return GatherFromLocal(sourceRoot, dirs, exts, maxFileChars, maxBundleChars);

        var repoId = await _ado.ResolveRepositoryIdAsync(string.IsNullOrWhiteSpace(repository) ? null : repository, ct);
        var branchRef = string.IsNullOrWhiteSpace(branch) ? null : branch;

        var paths = new List<string>();
        foreach (var dir in dirs)
        {
            try
            {
                foreach (var p in await _ado.ListRepoFilePathsAsync(dir, branchRef, repoId, ct))
                    if (exts.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) paths.Add(p);
            }
            catch (Exception e) { _log.LogWarning(e, "Module: could not list repo dir {Dir}", dir); }
        }
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).Take(MaxAdoFiles).ToList();

        using var gate = new SemaphoreSlim(AdoFetchConcurrency);
        var fetched = (await Task.WhenAll(paths.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try { return (p, await _ado.GetFileContentAsync(p, branchRef, repoId, "branch", ct)); }
            catch { return (p, ""); }
            finally { gate.Release(); }
        }))).Where(r => !string.IsNullOrEmpty(r.Item2)).ToDictionary(r => r.Item1, r => r.Item2);

        return (Assemble(paths, p => fetched.GetValueOrDefault(p), maxFileChars, maxBundleChars), fetched.Count);
    }

    private (string, int) GatherFromLocal(string sourceRoot, string[] dirs, string[] exts, int maxFileChars, int maxBundleChars)
    {
        var files = new List<string>();
        foreach (var rel in dirs)
        {
            var full = Path.GetFullPath(Path.Combine(sourceRoot, rel));
            if (!Directory.Exists(full)) continue;
            files.AddRange(Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => SkipDirs.Contains(s, StringComparer.OrdinalIgnoreCase)))
                .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
        }
        files = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        var read = 0;
        var bundle = Assemble(
            files.Select(f => Path.GetRelativePath(sourceRoot, f).Replace('\\', '/')).ToList(),
            rel => { try { read++; return File.ReadAllText(Path.Combine(sourceRoot, rel)); } catch { read--; return null; } },
            maxFileChars, maxBundleChars);
        return (bundle, read);
    }

    private static string Assemble(List<string> paths, Func<string, string?> contentOf, int maxFileChars, int maxBundleChars)
    {
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            if (sb.Length >= maxBundleChars) break;
            var content = contentOf(p);
            if (string.IsNullOrEmpty(content)) continue;
            if (content.Length > maxFileChars) content = content[..maxFileChars] + "\n/* …truncated… */";
            sb.Append("\n### ").Append(p).Append('\n').Append("```\n").Append(content).Append("\n```\n");
        }
        var bundle = sb.ToString();
        return bundle.Length > maxBundleChars ? bundle[..maxBundleChars] : bundle;
    }

    private static string Slug(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "module" : slug;
    }
}
