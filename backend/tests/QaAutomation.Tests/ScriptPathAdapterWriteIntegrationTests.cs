using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>INT-302 — product-mode API write paths land under AUTOMATION_ROOT.</summary>
public sealed class ScriptPathAdapterWriteIntegrationTests
{
    [Fact]
    public void PersistDraft_product_mode_paths_match_adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "auto_write_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var adapter = new ScriptPathAdapter(new StaticOptionsMonitor<QaLoopOptions>(
                new QaLoopOptions { AutomationRoot = root }));
            var unit = "deals";
            var slug = "get-deals";
            var spec = adapter.ApiSpecPath(unit, TestLevel.Endpoint, slug);
            var schema = adapter.ApiSchemaPath(unit, slug);

            Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
            Directory.CreateDirectory(Path.GetDirectoryName(schema)!);
            File.WriteAllText(spec, "import { test } from '@playwright/test';\nfrom '../../schemas/get-deals.schema';\n");
            File.WriteAllText(schema, "export const DealSchema = {};\n");

            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "modules", "deals")), spec);
            Assert.Contains($"{Path.DirectorySeparatorChar}api{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}endpoints{Path.DirectorySeparatorChar}", spec);
            Assert.Contains($"{Path.DirectorySeparatorChar}api{Path.DirectorySeparatorChar}schemas{Path.DirectorySeparatorChar}", schema);
            Assert.DoesNotContain("backend", adapter.ToRootRelative(spec), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("modules/deals/api/tests/endpoints/get-deals.spec.ts", adapter.ToRootRelative(spec));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
