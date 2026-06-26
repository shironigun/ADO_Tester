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
                var actual = AppendArtifacts(bug.Actual, f);
                bugId = await _ado.CreateBugAsync(
                    bug.Title, bug.Severity, bug.Preconditions, bug.ReproSteps, bug.Expected, actual, bug.SuggestedFix, ct);

                // Link to the failing Test Case (preferred) and optionally the story. Best-effort.
                if (f.AdoTestCaseId is int tcId)
                    await TryLinkAsync(bugId.Value, tcId, "test case");
                if (request.StoryId is int story)
                    await TryLinkAsync(bugId.Value, story, "story");

                didFile = true;
                filed++;
                _log.LogInformation("Filed Bug #{Bug} for '{Test}' [{Sev}]", bugId, f.Name, bug.Severity);
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

    /// <summary>Append the run's failure artifacts (screenshot/video/trace paths) to the bug's actual text.
    /// NOTE: binary upload into the ADO Bug is a follow-up — the client has no attachment API yet.</summary>
    private static string AppendArtifacts(string actual, FailedTest f)
    {
        if (f.Attachments.Count == 0)
            return actual;
        var sb = new StringBuilder(actual);
        sb.Append("\n\nEvidence captured by the test run (attach to this bug):");
        foreach (var a in f.Attachments)
            sb.Append("\n- ").Append(a);
        return sb.ToString();
    }
}
