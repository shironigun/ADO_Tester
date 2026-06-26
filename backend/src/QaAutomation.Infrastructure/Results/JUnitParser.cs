using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QaAutomation.Infrastructure.Results;

/// <summary>One failed test extracted from a Playwright JUnit report.</summary>
public sealed record FailedTest(
    string Name, int? AdoTestCaseId, string Message, string Details, List<string> Attachments);

/// <summary>
/// Parses a Playwright JUnit XML report (the <c>junit</c> reporter's output). Pulls failed test cases,
/// the ADO test-case id from the <c>[id]</c> in the title, the failure message/details, and any
/// <c>[[ATTACHMENT|path]]</c> markers (screenshot/video/trace) Playwright embeds in <c>system-out</c>.
/// </summary>
public static partial class JUnitParser
{
    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"\[\[ATTACHMENT\|([^\]]+)\]\]")]
    private static partial Regex AttachmentRegex();

    public static (int Total, List<FailedTest> Failures) Parse(string xml, string? artifactsRoot)
    {
        var doc = XDocument.Parse(xml);
        var testcases = doc.Descendants("testcase").ToList();
        var failures = new List<FailedTest>();

        foreach (var tc in testcases)
        {
            var failureNodes = tc.Elements("failure").Concat(tc.Elements("error")).ToList();
            if (failureNodes.Count == 0)
                continue;

            var name = (string?)tc.Attribute("name") ?? "";
            var classname = (string?)tc.Attribute("classname") ?? "";
            var idSource = $"{name} {classname}";
            int? id = IdRegex().Match(idSource) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v)
                ? v : null;

            var message = string.Join(
                " | ",
                failureNodes.Select(f => ((string?)f.Attribute("message") ?? "").Trim()).Where(s => s.Length > 0));
            var details = string.Join(
                "\n",
                failureNodes.Select(f => f.Value).Where(s => !string.IsNullOrWhiteSpace(s)));
            var sysOut = string.Join("\n", tc.Elements("system-out").Select(e => e.Value));

            var attachments = AttachmentRegex()
                .Matches($"{details}\n{sysOut}")
                .Select(a => ResolvePath(a.Groups[1].Value.Trim(), artifactsRoot))
                .Distinct()
                .ToList();

            failures.Add(new FailedTest(
                Name: string.IsNullOrEmpty(name) ? classname : name,
                AdoTestCaseId: id,
                Message: message,
                Details: Truncate(details, 4000),
                Attachments: attachments));
        }

        return (testcases.Count, failures);
    }

    private static string ResolvePath(string path, string? artifactsRoot)
    {
        if (string.IsNullOrWhiteSpace(artifactsRoot) || Path.IsPathRooted(path))
            return path;
        try { return Path.GetFullPath(Path.Combine(artifactsRoot, path)); }
        catch { return path; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…(truncated)";
}
