namespace QaAutomation.Core.Models;

/// <summary>One HTTP call found in a frontend source file during QA Loop source discovery.</summary>
public sealed record FrontendApiCall(string Method, string Path, string SourceFile);

/// <summary>
/// Bounded, branch-specific source evidence selected before QA Loop inventory analysis. The serialized
/// form is persisted on the session so a tester can review how the module was grounded.
/// </summary>
public sealed record ModuleSourceDiscovery(
    string? RouteFile,
    string? RoutePath,
    IReadOnlyList<string> FrontendFiles,
    IReadOnlyList<FrontendApiCall> ApiCalls,
    IReadOnlyList<string> BackendFiles,
    IReadOnlyList<string> SourceDirectories,
    string Confidence,
    string Explanation,
    string SourceBundle);
