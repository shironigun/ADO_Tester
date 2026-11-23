using QaAutomation.Infrastructure.Agents;
using Xunit;

namespace QaAutomation.Tests;

public sealed class TitleTagApiTitleTests
{
    [Fact]
    public void BuildApiTitle_matches_house_format_with_pending_tag()
    {
        var title = TitleTag.BuildApiTitle(
            "deals", "Create Deal", "Contract",
            "POST /lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId} - response shape matches schema");

        Assert.Equal(
            "[<tcId-pending>] Deals - Create Deal - Contract - POST /lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId} - response shape matches schema",
            title);
    }

    [Fact]
    public void ResolveFeatureName_prefers_FRS_alias_when_api_matches()
    {
        var ctx = """
            ## Features and suggested packs
            [{"id":"deal-create","aliases":["Create Deal"],"apis":["POST /lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId}"]}]
            """;
        var feature = TitleTag.ResolveFeatureName(
            "POST", "/lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId}", ctx);
        Assert.Equal("Create Deal", feature);
    }

    [Fact]
    public void ResolveFeatureName_infers_from_route_when_no_FRS_match()
    {
        var feature = TitleTag.ResolveFeatureName("POST", "/lead/deal/pipelineDeals/{id}", null);
        Assert.Equal("Create Pipeline Deals", feature);
    }

    [Fact]
    public void BuildApiTitle_endpoint_uses_case_index_tag()
    {
        var title = TitleTag.BuildApiTitle(
            "deals", "Create Deal", "Endpoint",
            "POST /lead/deal - returns 401 when the bearer token is missing",
            caseIndex: 1);
        Assert.StartsWith("[<tcId-pending:1>]", title);
        Assert.Contains("Deals - Create Deal - Endpoint - POST /lead/deal - returns 401", title);
    }
}
