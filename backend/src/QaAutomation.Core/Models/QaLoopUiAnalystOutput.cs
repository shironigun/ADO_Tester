namespace QaAutomation.Core.Models;

/// <summary>One discovered UI feature from QaLoopUiAnalystAgent.</summary>
public sealed record UiAnalystFeature(string Id, string Name, string Description);

/// <summary>One UI flow discovered from the module.</summary>
public sealed record UiAnalystFlow(string Name, string Trigger, string[] Steps, string[] Breaks);

/// <summary>One UI screen/component surface discovered from the module.</summary>
public sealed record UiAnalystScreen(string Name, string Route, string[] Components);

/// <summary>Output of QaLoopUiAnalystAgent — a module-scoped UI analysis.
/// NOT the same as AnalystOutput from Author Tests (separate type, no import dependency).
/// Serialized into ApiTestSession.UiAnalysisJson and surfaced in ApiSessionDetail.</summary>
public sealed record QaLoopUiAnalystOutput(
    UiAnalystFeature[] Features,
    UiAnalystFlow[] Flows,
    UiAnalystScreen[] Screens,
    string[] OpenQuestions,
    string[] MustTest,
    string[] MustNotTest,
    string AuthPermissionsSummary,
    string DmsVariantsSummary);
