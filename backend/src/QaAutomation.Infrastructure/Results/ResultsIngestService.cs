using System.Text;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Infrastructure.Agents;

namespace QaAutomation.Infrastructure.Results;

/// <summary>
/// Track A triage loop. Parses a Playwright JUnit run, triages each failure with the
/// <see cref="FailureTriageAgent"/>, and — for real bugs — files a house-style Bug via the deterministic
/// ADO client, linked to the failing Test Case (and optionally the story). Scoped (reads the KB).
/// </summary>
public sealed class ResultsIngestService : IResultsIngestService
{
    private readonly ILlmClientFactory _llmFactory;
    private readonly IKnowledgebaseService _kb;
    private readonly IAzureDevOpsClient _ado;
    private readonly ILogger<ResultsIngestService> _log;

    public ResultsIngestService(
        ILlmClientFactory llmFactory, IKnowledgebaseService kb, IAzureDevOpsClient ado, ILogger<ResultsIngestService> log)
    {
        _llmFactory = llmFactory;
        _kb = kb;
        _ado = ado;
        _log = log;
    }

    public async Task<IngestResultsResult> IngestAsync(
        int projectId, IngestResultsRequest request, CancellationToken ct = default)
    {
        var xml = request.JunitXml;
        var artifactsRoot = request.ArtifactsRoot;
        if (string.IsNullOrWhiteSpace(xml))
        {
            if (string.IsNullOrWhiteSpace(request.JunitPath))
                throw new ArgumentException("Provide JunitXml or JunitPath.");
            if (!File.Exists(request.JunitPath))
                throw new FileNotFoundException($"JUnit file not found: {request.JunitPath}");
            xml = await File.ReadAllTextAsync(request.JunitPath, ct);
            artifactsRoot ??= Path.GetDirectoryName(Path.GetFullPath(request.JunitPath));
        }

        var (total, failures) = JUnitParser.Parse(xml!, artifactsRoot);
        _log.LogInformation("Ingesting JUnit: {Total} test(s), {Failures} failure(s)", total, failures.Count);

        string? kbMarkdown = null;
        if (!string.IsNullOrWhiteSpace(request.KbFileName))
            kbMarkdown = await TryLoadKbAsync(projectId, request.KbFileName!, ct);

        var outcomes = new List<FailureOutcome>();
        if (failures.Count == 0)
            return new IngestResultsResult { TotalTests = total, Failures = 0, BugsFiled = 0, Outcomes = outcomes };

        var llm = await _llmFactory.CreateAsync(ct);
        var agent = new FailureTriageAgent(llm);
        var filed = 0;

        foreach (var f in failures)
        {
            ct.ThrowIfCancellationRequested();
            var triage = await agent.TriageAsync(f.Name, f.AdoTestCaseId, f.Message, f.Details, kbMarkdown, cancellationToken: ct);
            _log.LogInformation("Triaged '{Test}' → {Class} ({Conf})", f.Name, triage.Classification, triage.Confidence);

            int? bugId = null;
            var didFile = false;
            if (request.FileBugs && triage.Classification == "real_bug")
            {
                var bug = triage.Bug;
                // Which artifacts actually exist on disk and fit the size cap (so System Info is honest).
                var artifacts = ExistingArtifacts(f.Attachments);
                // Machine/test traceability goes in System Info — NOT in the human-facing repro/actual.
                var systemInfo = BuildSystemInfo(f, triage, artifacts);

                bugId = await _ado.CreateBugAsync(
                    bug.Title, bug.Severity, bug.Preconditions, bug.ReproSteps, bug.Expected, bug.Actual, bug.SuggestedFix,
                    systemInfo: systemInfo, ct: ct);

                // Link to the failing Test Case (preferred) and optionally the story. Best-effort.
                if (f.AdoTestCaseId is int tcId)
                    await TryLinkAsync(bugId.Value, tcId, "test case");
                if (request.StoryId is int story)
                    await TryLinkAsync(bugId.Value, story, "story");

                // Upload the real evidence files as ADO attachments on the bug.
                await AttachArtifactsAsync(bugId.Value, artifacts, ct);

                didFile = true;
                filed++;
                _log.LogInformation("Filed Bug #{Bug} for '{Test}' [{Sev}] with {N} attachment(s)",
                    bugId, f.Name, bug.Severity, artifacts.Count);
            }

            outcomes.Add(new FailureOutcome
            {
                TestTitle = f.Name,
                AdoTestCaseId = f.AdoTestCaseId,
                Classification = triage.Classification,
                Confidence = triage.Confidence,
                Reason = triage.Reason,
                Filed = didFile,
                BugId = bugId,
                BugTitle = triage.Bug.Title,
                Severity = triage.Bug.Severity,
                Preconditions = triage.Bug.Preconditions,
                ReproSteps = triage.Bug.ReproSteps,
                Expected = triage.Bug.Expected,
                Actual = triage.Bug.Actual,
                Artifacts = f.Attachments,
            });
        }

        return new IngestResultsResult
        {
            TotalTests = total,
            Failures = failures.Count,
            BugsFiled = filed,
            Outcomes = outcomes,
        };
    }

    private async Task TryLinkAsync(int bugId, int targetId, string what)
    {
        try { await _ado.LinkRelatedAsync(bugId, targetId); }
        catch (Exception e) { _log.LogWarning(e, "Could not link Bug #{Bug} to {What} #{Target}", bugId, what, targetId); }
    }

    private async Task<string?> TryLoadKbAsync(int projectId, string fileName, CancellationToken ct)
    {
        try
        {
            var docs = await _kb.ListAsync(projectId, ct);
            var doc = docs.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (doc is null || string.IsNullOrEmpty(doc.StoredPath) || !File.Exists(doc.StoredPath))
            {
                _log.LogWarning("KB doc '{File}' not found for triage grounding — proceeding without it", fileName);
                return null;
            }
            return await File.ReadAllTextAsync(doc.StoredPath, ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not load KB doc '{File}' for triage", fileName);
            return null;
        }
    }

    private const long MaxAttachmentBytes = 25_000_000;   // 25 MB — skip larger artifacts (e.g. long videos)

    /// <summary>Filter the parsed attachment paths to files that exist on disk and fit the size cap.</summary>
    private List<(string Path, string Name)> ExistingArtifacts(IReadOnlyList<string> paths)
    {
        var result = new List<(string, string)>();
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) { _log.LogWarning("Artifact not found on disk, skipping: {Path}", p); continue; }
                if (fi.Length > MaxAttachmentBytes) { _log.LogWarning("Artifact too large ({MB} MB), skipping: {Path}", fi.Length / 1_000_000, p); continue; }
                result.Add((p, Path.GetFileName(p)));
            }
            catch (Exception e) { _log.LogWarning(e, "Could not stat artifact {Path}", p); }
        }
        return result;
    }

    /// <summary>Upload each evidence file as an ADO attachment and link it onto the bug. Best-effort per file.</summary>
    private async Task AttachArtifactsAsync(int bugId, List<(string Path, string Name)> artifacts, CancellationToken ct)
    {
        foreach (var (path, name) in artifacts)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var url = await _ado.UploadAttachmentAsync(name, bytes, ct);
                if (!string.IsNullOrEmpty(url))
                    await _ado.AddWorkItemAttachmentLinkAsync(bugId, url, "E2E run artifact", ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Could not attach artifact {Name} to bug #{Bug}", name, bugId);
            }
        }
    }

    /// <summary>Build the bug's System Info HTML — the machine/test traceability that does NOT belong in the
    /// human-facing repro: which automated test failed, its ADO Test Case, the triage verdict, the raw
    /// failure, and the attached evidence.</summary>
    private static string BuildSystemInfo(
        FailedTest f, Agents.FailureTriageResult triage, List<(string Path, string Name)> artifacts)
    {
        var sb = new StringBuilder();
        sb.Append("<div><b>Found by:</b> automated E2E test (Playwright)</div>");
        sb.Append($"<div><b>Automated test:</b> {Esc(f.Name)}</div>");
        if (f.AdoTestCaseId is int id)
            sb.Append($"<div><b>ADO Test Case:</b> #{id}</div>");
        sb.Append($"<div><b>Triage:</b> {Esc(triage.Classification)} ({Esc(triage.Confidence)} confidence)</div>");
        if (!string.IsNullOrWhiteSpace(triage.Reason))
            sb.Append($"<div><b>Triage reasoning:</b> {Esc(triage.Reason)}</div>");
        if (!string.IsNullOrWhiteSpace(f.Message))
            sb.Append($"<div><b>Test failure:</b> {Esc(f.Message)}</div>");
        sb.Append(artifacts.Count > 0
            ? $"<div><b>Evidence:</b> {Esc(string.Join(", ", artifacts.Select(a => a.Name)))} — attached to this bug</div>"
            : "<div><b>Evidence:</b> none captured by the run</div>");
        return sb.ToString();
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
}
