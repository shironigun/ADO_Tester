using System.Text;
using Microsoft.Extensions.Logging;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
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
                // Upload evidence FIRST (images first) so we can embed them inline in the repro field.
                var artifacts = ExistingArtifacts(f.Attachments);
                var uploaded = await UploadArtifactsAsync(artifacts, ct);

                // Repro field: UI steps + multi-bullet observations + inline "Attachment #N" images/links.
                var reproHtml = BuildReproHtml(bug, triage.Observations, uploaded);
                // System Info: machine/test traceability only (NOT in the human repro).
                var systemInfo = BuildSystemInfo(f, triage);
                _log.LogDebug("Composed auto-bug repro HTML:\n{Repro}", reproHtml);

                // NB: keep foundWith at its default — 'Custom.FoundWith' is a fixed picklist and rejects
                // arbitrary values like "Automated Test". The automation provenance is in System Info instead.
                bugId = await _ado.CreateBugAsync(
                    bug.Title, bug.Severity, preconditions: "", reproSteps: [], expected: bug.Expected,
                    actual: "", suggestedFix: bug.SuggestedFix,
                    systemInfo: systemInfo, reproHtml: reproHtml, ct: ct);

                // Link to the failing Test Case (preferred) and optionally the story. Best-effort.
                if (f.AdoTestCaseId is int tcId)
                    await TryLinkAsync(bugId.Value, tcId, "test case");
                if (request.StoryId is int story)
                    await TryLinkAsync(bugId.Value, story, "story");

                // Also list every artifact in the WI Attachments tab (images are additionally inline above).
                await LinkAttachmentsAsync(bugId.Value, uploaded, ct);

                didFile = true;
                filed++;
                _log.LogInformation("Filed Bug #{Bug} for '{Test}' [{Sev}] with {N} attachment(s)",
                    bugId, f.Name, bug.Severity, uploaded.Count);
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
                Observations = triage.Observations,
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

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private sealed record UploadedArtifact(string Name, string Url, bool IsImage);

    /// <summary>Upload each evidence file as an ADO attachment; return the references (images first, so the
    /// screenshot is Attachment #1). Best-effort per file — a failed upload is skipped, not fatal.</summary>
    private async Task<List<UploadedArtifact>> UploadArtifactsAsync(List<(string Path, string Name)> artifacts, CancellationToken ct)
    {
        var uploaded = new List<UploadedArtifact>();
        foreach (var (path, name) in artifacts)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var url = await _ado.UploadAttachmentAsync(name, bytes, ct);
                if (!string.IsNullOrEmpty(url))
                    uploaded.Add(new UploadedArtifact(name, url, IsImage(name)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Could not upload artifact {Name}", name);
            }
        }
        // Images first so the screenshot is "Attachment #1" (what the cross-reference points at).
        return uploaded.OrderByDescending(a => a.IsImage).ToList();
    }

    /// <summary>Add each uploaded attachment to the bug's Attachments tab (images are also inline in repro).</summary>
    private async Task LinkAttachmentsAsync(int bugId, List<UploadedArtifact> uploaded, CancellationToken ct)
    {
        foreach (var a in uploaded)
        {
            try { await _ado.AddWorkItemAttachmentLinkAsync(bugId, a.Url, "E2E run artifact", ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Could not link attachment {Name} to bug #{Bug}", a.Name, bugId);
            }
        }
    }

    /// <summary>Compose the bug's Repro Steps HTML for an auto-filed E2E bug: preconditions, numbered UI
    /// steps, multi-bullet observations (with a cross-reference to the screenshot), then an Attachments
    /// section with "Attachment #N" headings — images embedded inline, other files as links.</summary>
    private static string BuildReproHtml(GeneratedBug bug, List<string> observations, List<UploadedArtifact> uploaded)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bug.Preconditions))
            sb.Append($"<div><b>Preconditions:</b> {Esc(bug.Preconditions)}</div>");

        sb.Append("<div><b>Steps to reproduce:</b></div><ol>");
        foreach (var s in bug.ReproSteps)
            sb.Append($"<li>{Esc(s)}</li>");
        sb.Append("</ol>");

        sb.Append("<div><b>OBSERVATION:</b></div><ul>");
        foreach (var o in observations)
            sb.Append($"<li>{Esc(o)}</li>");
        // Cross-reference the screenshot if we have one (it's Attachment #1 after the images-first sort).
        if (uploaded.Count > 0 && uploaded[0].IsImage)
            sb.Append("<li>Screenshot at the point of failure — see Attachment #1 below.</li>");
        sb.Append("</ul>");

        if (uploaded.Count > 0)
        {
            sb.Append("<div><b>Attachments:</b></div>");
            for (var i = 0; i < uploaded.Count; i++)
            {
                var a = uploaded[i];
                sb.Append($"<div><b>Attachment #{i + 1}: {Esc(a.Name)}</b></div>");
                sb.Append(a.IsImage
                    ? $"<div><img src=\"{a.Url}\" alt=\"{Esc(a.Name)}\" /></div>"
                    : $"<div><a href=\"{a.Url}\">{Esc(a.Name)}</a></div>");
            }
        }
        return sb.ToString();
    }

    /// <summary>Build the bug's System Info HTML — machine/test traceability that does NOT belong in the
    /// human-facing repro: which automated test failed, its ADO Test Case, the triage verdict, the raw failure.</summary>
    private static string BuildSystemInfo(FailedTest f, Agents.FailureTriageResult triage)
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
        return sb.ToString();
    }

    private static bool IsImage(string name) => ImageExts.Contains(Path.GetExtension(name).ToLowerInvariant());

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
}
