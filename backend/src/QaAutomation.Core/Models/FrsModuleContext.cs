namespace QaAutomation.Core.Models;

/// <summary>FRS Knowledge Base context resolved for a module. Carries the full module brief,
/// atlas markdown, parsed glob lists, rules section, and features inventory.
/// Serialized into ApiTestSession.KbContextJson — internal only, not surfaced in API responses.</summary>
public sealed record FrsModuleContext(
    string Slug,
    string ModuleMarkdown,
    string AtlasMarkdown,
    string[] FrontendPathIncludes,
    string[] BackendPathIncludes,
    string[] KeywordsExclusive,
    string RulesMarkdown,
    string FeBackLinksMarkdown,
    /// <summary>Compact JSON array of {id,aliases,apis} — trimmed from atlas features[] for context budget.</summary>
    string FeaturesJson);
