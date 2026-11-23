using System.Text.Json;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

public sealed class RegistryFileMergeTests
{
    [Fact]
    public void Merge_preserves_existing_entries_and_adds_spec_paths()
    {
        const string existing = """
            {
              "unit": "deals",
              "note": "CRM registry",
              "entries": {
                "GET /deals/{dealId}": {
                  "schemaFile": "modules/deals/api/schemas/old.schema.ts",
                  "contractTest": "modules/deals/api/tests/contracts/old.spec.ts"
                }
              }
            }
            """;

        var endpointSpec = "modules/deals/api/tests/endpoints/get-deals-dealid.spec.ts";
        var merged = RegistryFileMerge.Merge(existing, "deals", endpointSpec);
        using var doc = JsonDocument.Parse(merged);
        var root = doc.RootElement;

        Assert.Equal("deals", root.GetProperty("unit").GetString());
        Assert.Equal("CRM registry", root.GetProperty("note").GetString());

        var entries = root.GetProperty("entries");
        Assert.True(entries.TryGetProperty("GET /deals/{dealId}", out var oldEntry));
        Assert.Equal("modules/deals/api/schemas/old.schema.ts",
            oldEntry.GetProperty("schemaFile").GetString());
        Assert.Equal("modules/deals/api/tests/contracts/old.spec.ts",
            oldEntry.GetProperty("contractTest").GetString());

        var newKey = RegistryFileMerge.InferEntryKey(endpointSpec);
        Assert.True(entries.TryGetProperty(newKey, out var newEntry));
        Assert.Equal(endpointSpec, newEntry.GetProperty("endpointTest").GetString());

        Assert.Contains(endpointSpec,
            root.GetProperty("specs").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void InferField_maps_api_levels()
    {
        Assert.Equal("contractTest",
            RegistryFileMerge.InferField("modules/deals/api/tests/contracts/x.spec.ts"));
        Assert.Equal("endpointTest",
            RegistryFileMerge.InferField("modules/deals/api/tests/endpoints/x.spec.ts"));
        Assert.Equal("workflowTest",
            RegistryFileMerge.InferField("modules/deals/api/tests/workflows/x.spec.ts"));
        Assert.Equal("schemaFile",
            RegistryFileMerge.InferField("modules/deals/api/schemas/x.schema.ts"));
        Assert.Null(RegistryFileMerge.InferField("modules/deals/ui/tests/smoke/x.spec.ts"));
    }
}
