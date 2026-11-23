using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>INT-301 — ScriptPathAdapter deals path golden cases.</summary>
public sealed class ScriptPathAdapterTests
{
    private static ScriptPathAdapter Adapter(string root)
    {
        var opts = new QaLoopOptions { AutomationRoot = root };
        return new ScriptPathAdapter(new StaticOptionsMonitor<QaLoopOptions>(opts));
    }

    [Fact]
    public void Deals_api_contract_endpoint_workflow_and_schema_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "auto_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Adapter(root);
            Assert.True(a.IsProductMode);

            var contract = a.ApiSpecPath("deals", TestLevel.Contract, "DealDto");
            var endpoint = a.ApiSpecPath("Deals", TestLevel.Endpoint, "get-deals");
            var workflow = a.ApiSpecPath("deals", TestLevel.Workflow, "create-deal");
            var schema = a.ApiSchemaPath("deals", "DealDto");

            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "modules", "deals", "api", "tests", "contracts", "dealdto.spec.ts")),
                contract);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "modules", "deals", "api", "tests", "endpoints", "get-deals.spec.ts")),
                endpoint);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "modules", "deals", "api", "tests", "workflows", "create-deal.spec.ts")),
                workflow);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "modules", "deals", "api", "schemas", "dealdto.schema.ts")),
                schema);

            Assert.Equal("modules/deals/api/tests/contracts/dealdto.spec.ts", a.ToRootRelative(contract));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "resources", "registry.deals.json")),
                a.RegistryPath("deals"));
        }
        finally { try { Directory.Delete(root, true); } catch { /* ignore */ } }
    }

    [Fact]
    public void Deals_ui_bucket_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "auto_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Adapter(root);
            var smoke = a.UiSpecPath("deals", "smoke", "open-deals-list");
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "modules", "deals", "ui", "tests", "smoke", "open-deals-list.spec.ts")),
                smoke);
            Assert.Equal("modules/deals/ui/tests/smoke/open-deals-list.spec.ts", a.ToRootRelative(smoke));

            Assert.Throws<ArgumentOutOfRangeException>(() => a.UiSpecPath("deals", "nightly", "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => a.ApiSpecPath("deals", TestLevel.Ui, "x"));
        }
        finally { try { Directory.Delete(root, true); } catch { /* ignore */ } }
    }

    [Fact]
    public void EnsureUnderRoot_rejects_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "auto_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Adapter(root);
            var ok = a.ApiSpecPath("deals", TestLevel.Contract, "x");
            a.EnsureUnderRoot(ok);
            Assert.Throws<InvalidOperationException>(() =>
                a.EnsureUnderRoot(Path.Combine(Path.GetTempPath(), "outside.spec.ts")));
        }
        finally { try { Directory.Delete(root, true); } catch { /* ignore */ } }
    }

    [Fact]
    public void Without_AutomationRoot_product_mode_false()
    {
        var a = Adapter("");
        Assert.False(a.IsProductMode);
        Assert.Throws<InvalidOperationException>(() => a.ApiSpecPath("deals", TestLevel.Contract, "x"));
    }

    [Fact]
    public void Slug_and_level_helpers()
    {
        Assert.Equal("cpq-plus", ScriptPathAdapter.Slug("CPQ+"));
        Assert.Equal("contracts", ScriptPathAdapter.ApiLevelDir(TestLevel.Contract));
        Assert.Equal("endpoints", ScriptPathAdapter.ApiLevelDir(TestLevel.Endpoint));
        Assert.Equal("workflows", ScriptPathAdapter.ApiLevelDir(TestLevel.Workflow));
        Assert.Equal("smoke", ScriptPathAdapter.NormalizeBucket("Smoke"));
    }
}
