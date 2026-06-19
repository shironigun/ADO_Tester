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
