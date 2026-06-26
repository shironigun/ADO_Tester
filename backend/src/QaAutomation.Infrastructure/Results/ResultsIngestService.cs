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
                var artifacts = ExistingArtifacts(f.Attachments);

                // Backed by an existing ADO Test Case (the [id] in the title)? Then the expected result is
                // authoritative, so use the Expected/Actual format. With no backing test case, we only have
                // what we observed, so use the Observation format.
                var hasTestCase = f.AdoTestCaseId is not null;

                // Repro field: UI steps + (Expected/Actual when backed by a test case, else Observation).
                // Evidence goes to the Attachments tab, NOT embedded here.
                var reproHtml = BuildReproHtml(bug, triage.Observations, hasTestCase);
                var systemInfo = BuildSystemInfo(f, triage);
                _log.LogDebug("Composed auto-bug repro HTML:\n{Repro}", reproHtml);
                _log.LogDebug("Composed auto-bug system info:\n{Sys}", systemInfo);

                // NB: keep foundWith at its default. 'Custom.FoundWith' is a fixed picklist and rejects
                // arbitrary values like "Automated Test"; the automation provenance is in System Info instead.
                // Acceptance Criteria ("resolved when") only when there is no test case backing the expected
                // result in the repro (the Expected/Actual route already states it, so avoid duplicating).
                bugId = await _ado.CreateBugAsync(
                    bug.Title, bug.Severity, preconditions: "", reproSteps: [],
                    expected: hasTestCase ? "" : bug.Expected,
                    actual: "", suggestedFix: "",
                    systemInfo: systemInfo, reproHtml: reproHtml, ct: ct);

                // Link to the failing Test Case (preferred) and optionally the story. Best-effort.
                if (f.AdoTestCaseId is int tcId)
                    await TryLinkAsync(bugId.Value, tcId, "test case");
                if (request.StoryId is int story)
                    await TryLinkAsync(bugId.Value, story, "story");

                // Upload the evidence files and link them to the bug's Attachments tab.
                var attached = await AttachArtifactsAsync(bugId.Value, artifacts, ct);

                didFile = true;
                filed++;
                _log.LogInformation("Filed Bug #{Bug} for '{Test}' [{Sev}] with {N} attachment(s)",
                    bugId, f.Name, bug.Severity, attached);
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

    /// <summary>Upload each existing evidence file as an ADO attachment and link it to the bug's Attachments
    /// tab. Best-effort per file (a failed upload is skipped, not fatal). Returns the number attached.</summary>
    private async Task<int> AttachArtifactsAsync(int bugId, List<(string Path, string Name)> artifacts, CancellationToken ct)
    {
        var attached = 0;
        foreach (var (path, name) in artifacts)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var url = await _ado.UploadAttachmentAsync(name, bytes, ct);
                if (string.IsNullOrEmpty(url))
                    continue;
                await _ado.AddWorkItemAttachmentLinkAsync(bugId, url, "E2E run artifact", ct);
                attached++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Could not attach artifact {Name} to bug #{Bug}", name, bugId);
            }
        }
        return attached;
    }

    /// <summary>Compose the bug's Repro Steps HTML for an auto-filed E2E bug: preconditions, a blank line,
    /// numbered UI steps, then the result section. When the failure is backed by an existing ADO Test Case
    /// (<paramref name="hasTestCase"/>) the expected result is authoritative, so render Expected Result +
    /// Actual Result; otherwise render an Observation list (we only have what we saw). Evidence is NOT
    /// embedded here (it lives on the bug's Attachments tab).</summary>
    private static string BuildReproHtml(GeneratedBug bug, List<string> observations, bool hasTestCase)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bug.Preconditions))
            sb.Append($"<div><b>Preconditions:</b> {Esc(bug.Preconditions)}</div>");

        // Blank line before the steps (separates them from the preconditions).
        if (sb.Length > 0)
            sb.Append("<br/>");
        sb.Append("<div><b>Steps to reproduce:</b></div><ol>");
        foreach (var s in bug.ReproSteps)
            sb.Append($"<li>{Esc(s)}</li>");
        sb.Append("</ol>");

        if (hasTestCase && !string.IsNullOrWhiteSpace(bug.Expected))
        {
            sb.Append("<br/><div><b>Expected Result:</b></div><ul>");
            sb.Append($"<li>{Esc(bug.Expected)}</li>");
            sb.Append("</ul>");
            sb.Append("<div><b>Actual Result:</b></div><ul>");
            foreach (var o in observations)
                sb.Append($"<li>{Esc(o)}</li>");
            sb.Append("</ul>");
        }
        else
        {
            sb.Append("<div><b>OBSERVATION:</b></div><ul>");
            foreach (var o in observations)
                sb.Append($"<li>{Esc(o)}</li>");
            sb.Append("</ul>");
        }

        return sb.ToString();
    }

    /// <summary>Build the bug's System Info HTML: a scannable "how this was found" block: provenance, the
    /// automated test + ADO Test Case, the AI triage verdict and its reasoning, and the exact failing check.
    /// Ordered and section-separated for readability (NOT in the human repro).</summary>
    private static string BuildSystemInfo(FailedTest f, Agents.FailureTriageResult triage)
    {
        var verdict = triage.Classification switch
        {
            "real_bug" => "Real bug",
            "flaky" => "Flaky test",
            "test_issue" => "Test / selector issue",
            "stale_test" => "Stale test (requirement changed)",
            _ => triage.Classification,
        };

        var sb = new StringBuilder();
        sb.Append("<div><b>How this bug was found</b></div>");
        sb.Append("<div>Filed automatically from a failed end-to-end (Playwright) test.</div>");

        sb.Append("<br/><div><b>Automated test:</b> ").Append(Esc(f.Name)).Append("</div>");
        if (f.AdoTestCaseId is int id)
            sb.Append($"<div><b>ADO Test Case:</b> #{id}</div>");

        sb.Append($"<br/><div><b>AI triage verdict:</b> {Esc(verdict)} ({Esc(triage.Confidence)} confidence)</div>");
        if (!string.IsNullOrWhiteSpace(triage.Reason))
            sb.Append($"<div><b>Why:</b> {Esc(triage.Reason)}</div>");

        if (!string.IsNullOrWhiteSpace(f.Message))
            sb.Append($"<br/><div><b>Automated check that failed:</b> {Esc(f.Message)}</div>");

        return sb.ToString();
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
}
