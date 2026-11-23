using QaAutomation.Core.Models;

namespace QaAutomation.Core.Abstractions;

/// <summary>
/// Maps automation unit + api/ui level|bucket + file slug → absolute paths under
/// <c>AUTOMATION_ROOT</c> (INT-301). Callers must not write outside these roots.
/// </summary>
public interface IScriptPathAdapter
{
    /// <summary>True when <c>qaloop.automation_root</c> / <c>AUTOMATION_ROOT</c> is configured.</summary>
    bool IsProductMode { get; }

    /// <summary>Absolute product <c>automation/</c> root, or null when not in product mode.</summary>
    string? AutomationRoot { get; }

    /// <summary>
    /// <c>modules/{unit}/api/tests/{contracts|endpoints|workflows}/{slug}.spec.ts</c>
    /// </summary>
    string ApiSpecPath(string unit, TestLevel level, string fileSlug);

    /// <summary><c>modules/{unit}/api/schemas/{slug}.schema.ts</c></summary>
    string ApiSchemaPath(string unit, string fileSlug);

    /// <summary><c>modules/{unit}/ui/tests/{smoke|regression|e2e}/{slug}.spec.ts</c></summary>
    string UiSpecPath(string unit, string bucket, string fileSlug);

    /// <summary><c>resources/registry.{unit}.json</c></summary>
    string RegistryPath(string unit);

    /// <summary>Repo-relative path from <see cref="AutomationRoot"/> (forward slashes).</summary>
    string ToRootRelative(string absolutePath);

    /// <summary>Throws if <paramref name="absolutePath"/> is outside <see cref="AutomationRoot"/>.</summary>
    void EnsureUnderRoot(string absolutePath);
}
