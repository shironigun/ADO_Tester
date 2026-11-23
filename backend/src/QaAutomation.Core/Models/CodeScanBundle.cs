namespace QaAutomation.Core.Models;

/// <summary>One source file from the ADO repo, carrying both the path and full content.
/// Used inside CodeScanBundle for structured access without string concatenation.</summary>
public sealed record CodeScanFile(string Path, string Content);

/// <summary>Result of IQaLoopCodeScanService.ScanAsync — the bounded set of frontend + backend
/// files and extracted API call candidates used by both the API Analyst and UI Analyst.
/// Serialized into ApiTestSession.CodeScanJson — internal only, not surfaced in API responses.</summary>
public sealed record CodeScanBundle(
    CodeScanFile[] FrontendFiles,
    CodeScanFile[] BackendFiles,
    string[] ApiCallCandidates,
    /// <summary>"atlas" | "discovery" | "none" — which seed source produced the bundle.</summary>
    string DiscoverySource);
