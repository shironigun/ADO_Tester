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
    private readonly IAzureDevOpsClient _ado;
    private readonly ILogger<GenerationService> _log;

    public GenerationService(
        ILlmClientFactory llmFactory, IKnowledgebaseService kb, IAzureDevOpsClient ado, ILogger<GenerationService> log)
    {
        _llmFactory = llmFactory;
        _kb = kb;
        _ado = ado;
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

        var llm = await _llmFactory.CreateAsync(ct);
        var agent = new GeneratorAgent(llm);

        // 3) Resolve the ADO Test Case id: supplied wins; else optionally author + create it now so the
        //    spec carries a real [id] (the hook results-publishing maps on). Deterministic ADO writes.
        var adoId = request.AdoTestCaseId;
        var adoCreated = false;
        if (request.CreateAdoTestCase && adoId is null)
        {
            var tc = await agent.AuthorAdoTestCaseAsync(
                request.Module, request.ScenarioTitle, request.ScenarioIntent, kbMarkdown!, cancellationToken: ct);
            _log.LogInformation("Authored ADO Test Case '{Title}' ({Steps} steps) — creating in ADO", tc.Title, tc.Steps.Count);
            var newId = await _ado.CreateTestCaseAsync(tc.Title, tc.Preconditions, tc.Steps, ct);
            if (request.TestPlanId is int plan && request.TestSuiteId is int suite)
                await _ado.AddTestCaseToSuiteAsync(plan, suite, newId, ct);
            if (request.StoryId is int story)
                await _ado.LinkTestedByAsync(story, newId, ct);
            adoId = newId;
            adoCreated = true;
            _log.LogInformation("Created ADO Test Case #{Id} (suite-add={Suite}, story-link={Story})",
                newId, request.TestPlanId is not null && request.TestSuiteId is not null, request.StoryId is not null);
        }

        // 4) Build the exact tagged title the generated spec must carry, then generate.
        var idTag = adoId is int id ? $"[{id}]" : "[TC-TODO]";
        var smokeTag = request.Smoke ? " @smoke" : "";
        var testTitle = $"{idTag} {request.ScenarioTitle.Trim()}{smokeTag}";

        _log.LogInformation("Generating E2E spec '{Title}' (module {Module}) using {Provider}",
            testTitle, request.Module, llm.ProviderName);
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
            AdoTestCaseId = adoId,
            AdoTestCaseCreated = adoCreated,
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
