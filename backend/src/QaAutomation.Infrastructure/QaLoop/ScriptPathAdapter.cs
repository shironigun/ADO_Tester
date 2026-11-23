using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.QaLoop;

/// <summary>
/// Framework path rules under <c>AUTOMATION_ROOT/modules/{unit}/...</c> (INT-301).
/// </summary>
public sealed class ScriptPathAdapter(IOptionsMonitor<QaLoopOptions> opts) : IScriptPathAdapter
{
    public bool IsProductMode => !string.IsNullOrWhiteSpace(opts.CurrentValue.AutomationRoot);

    public string? AutomationRoot =>
        IsProductMode ? Path.GetFullPath(opts.CurrentValue.AutomationRoot.Trim()) : null;

    public string ApiSpecPath(string unit, TestLevel level, string fileSlug)
    {
        var root = RequireRoot();
        var u = Slug(unit);
        var levelDir = ApiLevelDir(level);
        var file = $"{Slug(fileSlug)}.spec.ts";
        return Path.GetFullPath(Path.Combine(root, "modules", u, "api", "tests", levelDir, file));
    }

    public string ApiSchemaPath(string unit, string fileSlug)
    {
        var root = RequireRoot();
        var u = Slug(unit);
        var file = $"{Slug(fileSlug)}.schema.ts";
        return Path.GetFullPath(Path.Combine(root, "modules", u, "api", "schemas", file));
    }

    public string UiSpecPath(string unit, string bucket, string fileSlug)
    {
        var root = RequireRoot();
        var u = Slug(unit);
        var b = NormalizeBucket(bucket);
        var file = $"{Slug(fileSlug)}.spec.ts";
        return Path.GetFullPath(Path.Combine(root, "modules", u, "ui", "tests", b, file));
    }

    public string RegistryPath(string unit)
    {
        var root = RequireRoot();
        return Path.GetFullPath(Path.Combine(root, "resources", $"registry.{Slug(unit)}.json"));
    }

    public string ToRootRelative(string absolutePath)
    {
        var root = RequireRoot();
        var full = Path.GetFullPath(absolutePath);
        EnsureUnderRoot(full);
        var rel = Path.GetRelativePath(root, full);
        return rel.Replace('\\', '/');
    }

    public void EnsureUnderRoot(string absolutePath)
    {
        var root = RequireRoot();
        var full = Path.GetFullPath(absolutePath);
        var rootFull = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Write path '{full}' is outside AUTOMATION_ROOT '{root}'.");
        }
    }

    internal static string ApiLevelDir(TestLevel level) => level switch
    {
        TestLevel.Contract => "contracts",
        TestLevel.Endpoint => "endpoints",
        TestLevel.Workflow => "workflows",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level,
            "API specs only support Contract / Endpoint / Workflow."),
    };

    internal static string NormalizeBucket(string bucket)
    {
        var b = (bucket ?? "").Trim().ToLowerInvariant();
        return b switch
        {
            "smoke" or "regression" or "e2e" => b,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket,
                "UI bucket must be smoke | regression | e2e."),
        };
    }

    /// <summary>Kebab-safe slug: lowercase, non-alnum → hyphen; <c>+</c> → <c>plus</c>.</summary>
    internal static string Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Slug/unit cannot be empty.", nameof(value));
        var s = value.Trim().ToLowerInvariant()
            .Replace("+", " plus ", StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal);
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        return collapsed.Trim('-');
    }

    private string RequireRoot()
    {
        if (!IsProductMode || AutomationRoot is null)
            throw new InvalidOperationException(
                "AUTOMATION_ROOT / qaloop.automation_root is not set — ScriptPathAdapter is product-mode only.");
        return AutomationRoot;
    }
}
