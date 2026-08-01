using QaAutomation.Core.Models;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>QLV2-301 — the @qa-meta header baked into every generated spec so Scripts Board can
/// reconcile disk↔DB (see docs/progress/RalphLoop/qa-loop-v2/blueprint.md → SLAB 3).</summary>
public sealed class QaMetaHeaderTests
{
    private static readonly QaMeta SampleMeta = new(
        Module: "leads", Type: "api", Level: "contract", Bucket: null, TcId: 123, SourceDraftId: 45,
        GeneratedAt: new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc), SpecHash: "abc123def456");

    private const string LegacySpec = """
        import { test, expect } from '@playwright/test';

        test('GET /leads/1', async ({ request }) => {
          const res = await request.get('/leads/1');
          expect(res.status()).toBe(200);
        });
        """;

    [Fact]
    public void Build_then_TryParse_round_trips()
    {
        var header = QaMetaHeader.Build(SampleMeta);
        Assert.StartsWith("// @qa-meta ", header);

        var parsed = QaMetaHeader.TryParse(header + "import { test } from '@playwright/test';\n");
        Assert.NotNull(parsed);
        Assert.Equal(SampleMeta, parsed);
    }

    [Fact]
    public void Upsert_on_header_less_file_prepends_exactly_one_header()
    {
        var stamped = QaMetaHeader.Upsert(LegacySpec, SampleMeta);

        var lines = stamped.Split('\n');
        Assert.StartsWith("// @qa-meta ", lines[0]);
        Assert.Equal(1, stamped.Split('\n').Count(l => l.StartsWith("// @qa-meta ")));
        // Body is untouched (just moved down one line).
        Assert.Equal(LegacySpec, QaMetaHeader.StripHeader(stamped));
    }

    [Fact]
    public void Upsert_on_already_stamped_file_replaces_in_place_no_duplicate()
    {
        var firstStamp = QaMetaHeader.Upsert(LegacySpec, SampleMeta);
        var updatedMeta = SampleMeta with { TcId = 999, SpecHash = "newhash000001" };

        var restamped = QaMetaHeader.Upsert(firstStamp, updatedMeta);

        Assert.Equal(1, restamped.Split('\n').Count(l => l.StartsWith("// @qa-meta ")));
        var parsed = QaMetaHeader.TryParse(restamped);
        Assert.Equal(999, parsed!.TcId);
        Assert.Equal("newhash000001", parsed.SpecHash);
        // Body still untouched — only the header line changed.
        Assert.Equal(LegacySpec, QaMetaHeader.StripHeader(restamped));
    }

    [Fact]
    public void TryParse_on_legacy_spec_without_header_returns_null_not_throw()
    {
        Assert.Null(QaMetaHeader.TryParse(LegacySpec));
    }

    [Fact]
    public void TryParse_on_malformed_header_returns_null_not_throw()
    {
        var malformed = "// @qa-meta {not valid json\n" + LegacySpec;
        Assert.Null(QaMetaHeader.TryParse(malformed));
    }

    [Fact]
    public void StripHeader_on_header_less_file_is_a_no_op()
    {
        Assert.Equal(LegacySpec, QaMetaHeader.StripHeader(LegacySpec));
    }

    [Fact]
    public void ComputeHash_is_stable_for_identical_input_and_differs_for_different_input()
    {
        var h1 = QaMetaHeader.ComputeHash(LegacySpec);
        var h2 = QaMetaHeader.ComputeHash(LegacySpec);
        var h3 = QaMetaHeader.ComputeHash(LegacySpec + "\n// changed");

        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
    }

    [Fact]
    public void ExtractTags_pulls_feature_and_pack_tokens_from_first_test_line()
    {
        var spec = """
            import { test, expect } from '@playwright/test';

            test('GET /leads @smoke @feature-lead-list @contract', async ({ request }) => {
              const res = await request.get('/leads');
              expect(res.status()).toBe(200);
            });
            """;

        var tags = QaMetaHeader.ExtractTags(spec);

        Assert.Equal(["@smoke", "@feature-lead-list", "@contract"], tags);
    }

    [Fact]
    public void ExtractTags_skips_playwright_import_and_returns_empty_when_no_at_tokens()
    {
        Assert.Empty(QaMetaHeader.ExtractTags(LegacySpec));
    }

    [Fact]
    public void ExtractTags_dedupes_preserving_order()
    {
        var spec = "test('x @smoke @feature-a @smoke', async () => {});";
        Assert.Equal(["@smoke", "@feature-a"], QaMetaHeader.ExtractTags(spec));
    }

    [Fact]
    public void Build_omits_null_tags_keeps_legacy_shape_and_round_trips_with_tags()
    {
        var without = QaMetaHeader.Build(SampleMeta);
        Assert.DoesNotContain("\"tags\"", without);

        var withTags = SampleMeta with { Tags = ["@smoke", "@feature-x"] };
        var header = QaMetaHeader.Build(withTags);
        Assert.Contains("\"tags\"", header);

        var parsed = QaMetaHeader.TryParse(header + "import x;\n");
        Assert.Equal(["@smoke", "@feature-x"], parsed!.Tags);
    }
}
