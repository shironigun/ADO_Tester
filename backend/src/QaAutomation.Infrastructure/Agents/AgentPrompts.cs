using System.Reflection;
using System.Text;

namespace QaAutomation.Infrastructure.Agents;

/// <summary>
/// The FROZEN agent system prompts (spec §5.7), loaded from embedded resources that were extracted
/// byte-exact from the live Python (<c>write_prompts.py</c>) — never hand-transcribed. Re-run that
/// script if a Python prompt changes.
/// </summary>
internal static class AgentPrompts
{
    public static string AnalystSystem { get; } = Load("analyst_system.txt");
    public static string TesterSystem { get; } = Load("tester_system.txt");
    public static string BugWriterSystem { get; } = Load("bug_writer_system.txt");
    public static string CodeVerifierSystem { get; } = Load("code_verifier_system.txt");
    public static string CodeVerifierStepSystem { get; } = Load("code_verifier_step_system.txt");
    public static string ActualRuleFull { get; } = Load("actual_rule_full.txt");
    public static string ActualRuleLite { get; } = Load("actual_rule_lite.txt");

    /// <summary>KB-extractor prompt — authored for Track A (NOT a Python port; safe to edit/iterate).</summary>
    public static string KbExtractorSystem { get; } = Load("kb_extractor_system.txt");

    /// <summary>E2E Generator prompt — authored for Track A (NOT a Python port; safe to edit/iterate).</summary>
    public static string GeneratorE2eSystem { get; } = Load("generator_e2e_system.txt");

    /// <summary>Test-case author prompt — authored for Track A: scenario+KB → one ADO Test Case (JSON).</summary>
    public static string TestCaseAuthorSystem { get; } = Load("testcase_author_system.txt");

    /// <summary>Failure-triage prompt — authored for Track A: a failed test → classification + bug (JSON).</summary>
    public static string FailureTriageSystem { get; } = Load("failure_triage_system.txt");

    /// <summary>Module-analyzer prompt — rewritten for QA Loop: code + existing cases → inventory + coverage matrix (JSON).</summary>
    public static string ModuleAnalyzerSystem { get; } = Load("module_analyzer_system.txt");

    // ── QA Loop generator + analyzer prompts ─────────────────────────────────

    /// <summary>Contract generator: route → Zod schema + Playwright spec (JSON).</summary>
    public static string ContractGeneratorSystem { get; } = Load("contract_generator_system.txt");

    /// <summary>Contract test-case: route → house-style ADO Test Case array (JSON).</summary>
    public static string ContractTestcaseSystem { get; } = Load("contract_testcase_system.txt");

    /// <summary>Endpoint generator: route → spec covering 200/401/400 cases (JSON).</summary>
    public static string EndpointGeneratorSystem { get; } = Load("endpoint_generator_system.txt");

    /// <summary>Endpoint test-case: per-case → house-style ADO Test Case array (JSON).</summary>
    public static string EndpointTestcaseSystem { get; } = Load("endpoint_testcase_system.txt");

    /// <summary>Workflow generator: workflow → chained spec with cleanup (JSON).</summary>
    public static string WorkflowGeneratorSystem { get; } = Load("workflow_generator_system.txt");

    /// <summary>Workflow test-case: workflow → one-step-per-leg ADO Test Case (JSON).</summary>
    public static string WorkflowTestcaseSystem { get; } = Load("workflow_testcase_system.txt");

    /// <summary>Impact analyzer: changed files + inventory → impacted draft ids (JSON).</summary>
    public static string ImpactAnalyzerSystem { get; } = Load("impact_analyzer_system.txt");

    private static string Load(string fileName)
    {
        var asm = typeof(AgentPrompts).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Prompts." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded prompt resource not found: {fileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
