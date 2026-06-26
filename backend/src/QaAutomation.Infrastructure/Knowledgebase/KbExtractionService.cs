using System.Text;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Infrastructure.Knowledgebase;

/// <summary>
/// Track A — hackathon KB foundation. Gathers a module's source (component code + existing tests) from a
/// local repo clone, runs the <see cref="KbExtractorAgent"/> once per flow (and once for <c>rules.md</c>),
/// and stores + indexes each produced markdown via <see cref="IKnowledgebaseService"/> so the
/// Analyst/Generator can retrieve it. Per-flow generation keeps each LLM call small — the mitigation for
/// the Claude-CLI large-output failure. Scoped: it depends on the scoped <see cref="IKnowledgebaseService"/>.
/// </summary>
public sealed class KbExtractionService : IKbExtractionService
{
    private static readonly string[] SkipDirs = { "node_modules", "bin", "obj", ".git", "dist", "build", "coverage" };

    private readonly ILlmClientFactory _llmFactory;
    private readonly IKnowledgebaseService _kb;
    private readonly ILogger<KbExtractionService> _log;

    public KbExtractionService(
        ILlmClientFactory llmFactory, IKnowledgebaseService kb, ILogger<KbExtractionService> log)
    {
        _llmFactory = llmFactory;
        _kb = kb;
        _log = log;
    }

    public async Task<KbExtractionResult> ExtractAsync(
        int projectId, KbExtractionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new ArgumentException("Module is required.");
        if (string.IsNullOrWhiteSpace(request.SourceRoot))
            throw new ArgumentException("SourceRoot is required.");
        if (!Directory.Exists(request.SourceRoot))
            throw new DirectoryNotFoundException($"SourceRoot not found: {request.SourceRoot}");
        if (request.Flows.Count == 0)
            throw new ArgumentException("At least one flow is required.");

        var (bundle, filesRead) = GatherSource(request);
        if (filesRead == 0)
            throw new InvalidOperationException(
                "No source files were read — check SourceRoot and SourceDirs (none of the module dirs matched).");

        var llm = await _llmFactory.CreateAsync(ct);
        _log.LogInformation(
            "KB extraction for '{Module}' using {Provider}: {Files} files, {Chars} bundle chars, {Flows} flow(s)",
            request.Module, llm.ProviderName, filesRead, bundle.Length, request.Flows.Count);
        var agent = new KbExtractorAgent(llm);

        IReadOnlyList<string> titles = [];   // MVP: existing-test-case enrichment is a later step

        var docs = new List<KbDocResult>();
        foreach (var flow in request.Flows)
        {
            ct.ThrowIfCancellationRequested();
            var markdown = await agent.ExtractFlowAsync(request.Module, flow, bundle, titles, cancellationToken: ct);
            var fileName = $"{Slug(request.Module)}-{Slug(flow)}.md";
            var doc = await StoreAsync(projectId, fileName, markdown, ct);
            docs.Add(new KbDocResult { FileName = fileName, Kind = "flow", CharCount = markdown.Length, DocumentId = doc.Id });
            _log.LogInformation("KB extracted flow '{Flow}' → {File} ({Chars} chars)", flow, fileName, markdown.Length);
        }

        ct.ThrowIfCancellationRequested();
        var rules = await agent.ExtractRulesAsync(request.Module, request.Flows, bundle, cancellationToken: ct);
        var rulesName = $"{Slug(request.Module)}-rules.md";
        var rulesDoc = await StoreAsync(projectId, rulesName, rules, ct);
        docs.Add(new KbDocResult { FileName = rulesName, Kind = "rules", CharCount = rules.Length, DocumentId = rulesDoc.Id });

        return new KbExtractionResult
        {
            Module = request.Module,
            FilesRead = filesRead,
            BundleChars = bundle.Length,
            Docs = docs,
        };
    }

    /// <summary>Replace any existing KB doc with the same file name (idempotent re-runs), then store + index.</summary>
    private async Task<KnowledgebaseDocument> StoreAsync(int projectId, string fileName, string markdown, CancellationToken ct)
    {
        var existing = await _kb.ListAsync(projectId, ct);
        foreach (var d in existing.Where(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            await _kb.DeleteAsync(d.Id, ct);
        return await _kb.UploadAsync(projectId, fileName, markdown, ct);
    }

    // ── source gathering ───────────────────────────────────────────────────────

    /// <summary>Read the module's source files into one bounded bundle string (path-headed code blocks),
    /// skipping build/vendor dirs, truncating large files, and capping the total size.</summary>
    private (string Bundle, int FilesRead) GatherSource(KbExtractionRequest req)
    {
        var dirs = req.SourceDirs is { Count: > 0 } ? req.SourceDirs : DefaultDirs(req.Module);
        var exts = req.Extensions is { Count: > 0 } ? req.Extensions : new List<string> { ".ts", ".tsx" };

        var sb = new StringBuilder();
        var filesRead = 0;
        foreach (var rel in dirs)
        {
            var full = Path.GetFullPath(Path.Combine(req.SourceRoot, rel));
            if (!Directory.Exists(full))
            {
                _log.LogWarning("KB source dir not found, skipping: {Dir}", full);
                continue;
            }
            foreach (var file in EnumerateFiles(full, exts))
            {
                if (sb.Length >= req.MaxBundleChars)
                    break;
                string content;
                try { content = File.ReadAllText(file); }
                catch (Exception e) { _log.LogWarning(e, "KB skip unreadable file {File}", file); continue; }

                if (content.Length > req.MaxFileChars)
                    content = content[..req.MaxFileChars] + "\n/* …truncated… */";

                var relPath = Path.GetRelativePath(req.SourceRoot, file).Replace('\\', '/');
                sb.Append("\n### ").Append(relPath).Append('\n');
                sb.Append("```").Append(LangFor(file)).Append('\n');
                sb.Append(content).Append("\n```\n");
                filesRead++;
            }
        }

        var bundle = sb.ToString();
        if (bundle.Length > req.MaxBundleChars)
            bundle = bundle[..req.MaxBundleChars];
        return (bundle, filesRead);
    }

    private static IEnumerable<string> EnumerateFiles(string root, List<string> exts)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !PathHasSkippedSegment(f))
            .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static bool PathHasSkippedSegment(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => SkipDirs.Contains(seg, StringComparer.OrdinalIgnoreCase));

    private static List<string> DefaultDirs(string module) =>
    [
        $"web-app/src/target-crm/src/components/{module}",
        $"web-app/src/target-crm/tests-e2e/{module}",
    ];

    private static string LangFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".ts" => "ts",
        ".tsx" => "tsx",
        ".cs" => "csharp",
        _ => "",
    };

    /// <summary>Lowercase, non-alphanumerics → single hyphen, trimmed — for safe KB file names.</summary>
    private static string Slug(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "module" : slug;
    }
}
