using System.Text.Json;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Orchestrators;
using Xunit;

namespace QaAutomation.Tests;

public sealed class ScanContextTests
{
    [Fact]
    public void FormatScanContext_null_when_empty()
    {
        var session = new ApiTestSession { Module = "deals" };
        Assert.Null(QaLoopOrchestrator.FormatScanContext(session));
    }

    [Fact]
    public void FormatScanContext_merges_text_and_attachments()
    {
        var attachments = new List<ContextAttachmentDto>
        {
            new() { RelativePath = "notes/scope.md", Content = "# Scope\nFocus on POST deals." },
        };
        var session = new ApiTestSession
        {
            Module = "deals",
            AdditionalContext = "Skip legacy endpoints.",
            ContextAttachmentsJson = JsonSerializer.Serialize(attachments),
        };

        var ctx = QaLoopOrchestrator.FormatScanContext(session);

        Assert.NotNull(ctx);
        Assert.Contains("Skip legacy endpoints.", ctx);
        Assert.Contains("## Scan context attachments", ctx);
        Assert.Contains("### notes/scope.md", ctx);
        Assert.Contains("# Scope", ctx);
    }

    [Fact]
    public void BuildGeneratorExtraContext_includes_scan_context()
    {
        var session = new ApiTestSession
        {
            Module = "deals",
            AdditionalContext = "Prefer happy path.",
            GenerateContext = "Add 401 cases.",
        };

        var extra = QaLoopOrchestrator.BuildGeneratorExtraContext(session);

        Assert.NotNull(extra);
        Assert.Contains("Prefer happy path.", extra);
        Assert.Contains("Add 401 cases.", extra);
    }

    [Fact]
    public void SpecNormalizer_product_mode_rewrites_playwright_import()
    {
        const string raw =
            "import { test, expect } from '@playwright/test';\n" +
            "import { request } from '@playwright/test';\n" +
            "const BASE = process.env.AUTOMATION_BASE_URL ?? '';\n" +
            "test('x', async () => {});\n";

        var normalized = SpecNormalizer.Normalize(raw, isProductMode: true);

        Assert.Contains("common/fixtures/api.fixture", normalized);
        Assert.DoesNotContain("@playwright/test", normalized);
        Assert.DoesNotContain("AUTOMATION_BASE_URL", normalized);
    }
}
