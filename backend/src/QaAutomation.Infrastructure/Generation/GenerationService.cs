using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Infrastructure.Generation;

/// <summary>
/// Track A Generator. Loads a KB flow doc, runs the <see cref="GeneratorAgent"/> to emit a self-contained
/// Playwright E2E spec, and writes it into the greenfield <c>/automation</c> folder — guarded so it can
/// only ever write inside an "automation" folder (constitution: write only in /automation). Scoped: it
/// depends on the scoped <see cref="IKnowledgebaseService"/>.
/// </summary>
public sealed class GenerationService : IGenerationService
{
    private const string AutomationRelPath = "web-app/src/target-crm/automation";

    private readonly ILlmClientFactory _llmFactory;
    private readonly IKnowledgebaseService _kb;
    private readonly ILogger<GenerationService> _log;

    public GenerationService(ILlmClientFactory llmFactory, IKnowledgebaseService kb, ILogger<GenerationService> log)
    {
        _llmFactory = llmFactory;
        _kb = kb;
        _log = log;
    }

    public async Task<GenerateSpecResult> GenerateE2eSpecAsync(
        int projectId, GenerateSpecRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new ArgumentException("Module is required.");
        if (string.IsNullOrWhiteSpace(request.ScenarioTitle))
            throw new ArgumentException("ScenarioTitle is required.");

        // 1) Resolve the KB flow markdown (inline wins; else load the stored doc).
        var kbMarkdown = request.KbFlowMarkdown;
        if (string.IsNullOrWhiteSpace(kbMarkdown))
        {
            if (string.IsNullOrWhiteSpace(request.KbFileName))
                throw new ArgumentException("Provide KbFileName or KbFlowMarkdown.");
            kbMarkdown = await LoadKbDocAsync(projectId, request.KbFileName!, ct);
        }

        // 2) Optional house-style reference (read-only from the repo).
        string? style = null;
        if (!string.IsNullOrWhiteSpace(request.StyleReferencePath) && !string.IsNullOrWhiteSpace(request.SourceRoot))
        {
            var refPath = Path.GetFullPath(Path.Combine(request.SourceRoot!, request.StyleReferencePath!));
            if (File.Exists(refPath))
            {
                try { style = await File.ReadAllTextAsync(refPath, ct); }
                catch (Exception e) { _log.LogWarning(e, "Could not read style reference {Path}", refPath); }
            }
        }

        // 3) Build the exact tagged title the generated test must carry.
        var idTag = request.AdoTestCaseId is int id ? $"[{id}]" : "[TC-TODO]";
        var smokeTag = request.Smoke ? " @smoke" : "";
        var testTitle = $"{idTag} {request.ScenarioTitle.Trim()}{smokeTag}";

        // 4) Generate.
        var llm = await _llmFactory.CreateAsync(ct);
        _log.LogInformation("Generating E2E spec '{Title}' (module {Module}) using {Provider}",
            testTitle, request.Module, llm.ProviderName);
        var agent = new GeneratorAgent(llm);
        var code = await agent.GenerateE2eSpecAsync(
            request.Module, testTitle, request.ScenarioIntent, kbMarkdown!, style, cancellationToken: ct);

        // 5) Write (guarded to /automation).
        var fileName = $"{Slug(request.ScenarioTitle)}.spec.ts";
        string? filePath = null;
        var written = false;
        if (request.Write)
        {
            var outputRoot = ResolveOutputRoot(request);
            var dir = Path.Combine(outputRoot, "e2e", Slug(request.Module));
            Directory.CreateDirectory(dir);
            filePath = Path.Combine(dir, fileName);
            await File.WriteAllTextAsync(filePath, code, ct);
            written = true;
            _log.LogInformation("Wrote spec → {Path} ({Chars} chars)", filePath, code.Length);
        }

        return new GenerateSpecResult
        {
            TestTitle = testTitle,
            FileName = fileName,
            FilePath = filePath,
            Written = written,
            CharCount = code.Length,
            Code = code,
        };
    }

    private async Task<string> LoadKbDocAsync(int projectId, string fileName, CancellationToken ct)
    {
        var docs = await _kb.ListAsync(projectId, ct);
        var doc = docs.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"KB doc '{fileName}' not found in project {projectId}. Run KB extraction first.");
        if (string.IsNullOrEmpty(doc.StoredPath) || !File.Exists(doc.StoredPath))
            throw new FileNotFoundException($"KB doc '{fileName}' has no stored file on disk.");
        return await File.ReadAllTextAsync(doc.StoredPath, ct);
    }

    /// <summary>Resolve and GUARD the output folder — it must be an "automation" folder, enforcing the
    /// constitution's write-only-inside-/automation rule (prevents accidental writes into app code).</summary>
    private static string ResolveOutputRoot(GenerateSpecRequest request)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(request.OutputRoot))
            root = Path.GetFullPath(request.OutputRoot!);
        else if (!string.IsNullOrWhiteSpace(request.SourceRoot))
            root = Path.GetFullPath(Path.Combine(request.SourceRoot!, AutomationRelPath));
        else
            throw new ArgumentException("Provide OutputRoot or SourceRoot.");

        var segments = root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!segments.Any(s => string.Equals(s, "automation", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"Refusing to write outside an 'automation' folder (got '{root}'). " +
                "Constitution rule: agents write only inside /automation.");
        return root;
    }

    private static string Slug(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "spec" : slug;
    }
}
