using System.Xml.Linq;
using QaAutomation.Core.Abstractions;

namespace QaAutomation.Infrastructure.QaRuns;

/// <summary>Shared JUnit parser for Playwright execution paths.</summary>
internal static class PlaywrightResultParser
{
    internal static (int Passed, int Failed, int Skipped, IReadOnlyList<FailedTestResult> FailedTests)
        ParseJUnit(string junitPath, Action<Exception> onError)
    {
        if (!File.Exists(junitPath))
            return (0, 0, 0, []);

        try
        {
            var doc = XDocument.Load(junitPath);
            var suites = doc.Descendants("testsuite").ToList();
            var passed = suites.Sum(s => IntAttribute(s, "tests"))
                         - suites.Sum(s => IntAttribute(s, "failures"))
                         - suites.Sum(s => IntAttribute(s, "skipped"))
                         - suites.Sum(s => IntAttribute(s, "errors"));
            var failed = suites.Sum(s => IntAttribute(s, "failures")) + suites.Sum(s => IntAttribute(s, "errors"));
            var skipped = suites.Sum(s => IntAttribute(s, "skipped"));
            var failures = doc.Descendants("testcase")
                .Where(tc => tc.Element("failure") is not null || tc.Element("error") is not null)
                .Select(tc =>
                {
                    var suiteFile = tc.Parent?.Attribute("file")?.Value;
                    if (suiteFile is null)
                    {
                        var suiteName = tc.Parent?.Attribute("name")?.Value ?? "";
                        var bracketIdx = suiteName.LastIndexOf(" [", StringComparison.Ordinal);
                        suiteFile = bracketIdx > 0 ? suiteName[..bracketIdx] : suiteName;
                    }

                    return new FailedTestResult
                    {
                        TestTitle = tc.Attribute("name")?.Value ?? "",
                        ErrorMessage = ExtractErrorMessage((tc.Element("failure") ?? tc.Element("error"))?.Value),
                        SpecFilePath = string.IsNullOrEmpty(suiteFile) ? null : suiteFile,
                    };
                })
                .ToList();

            return (Math.Max(passed, 0), failed, skipped, failures);
        }
        catch (Exception ex)
        {
            onError(ex);
            return (0, 0, 0, []);
        }
    }

    private static int IntAttribute(XElement element, string name) =>
        int.TryParse(element.Attribute(name)?.Value, out var value) ? value : 0;

    private static string? ExtractErrorMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = raw.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        static bool IsLocator(string line) => line.StartsWith('[') && (line.Contains('\u203a') || line.Contains(".spec.ts"));
        var meaningful = lines.Where(line => !IsLocator(line)).ToList();
        if (meaningful.Count == 0) meaningful = lines;
        var errorLine = meaningful.FirstOrDefault(line =>
            line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("expect(", StringComparison.OrdinalIgnoreCase));
        var expected = meaningful.FirstOrDefault(line => line.StartsWith("Expected:", StringComparison.OrdinalIgnoreCase));
        var received = meaningful.FirstOrDefault(line => line.StartsWith("Received:", StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (errorLine is not null) parts.Add(errorLine);
        if (expected is not null) parts.Add(expected);
        if (received is not null) parts.Add(received);
        return parts.Count > 0 ? string.Join("  ", parts) : meaningful[0];
    }
}
