using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Api.Controllers;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Contracts;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Agents;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.QaLoop;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

public sealed class QaLoopFailureAnalystAgentTests
{
    [Fact]
    public async Task ExplainAsync_returns_non_empty_string()
    {
        var llm = new StubLlmClient(
            "The locator for the Save button was not visible. Likely a selector drift after a UI rename. Re-inspect the page and heal the locator.");
        var agent = new QaLoopFailureAnalystAgent(llm);

        var text = await agent.ExplainAsync(
            "Error: expect(locator).toBeVisible() failed",
            "Save deal",
            """[{"action":"Click Save","expected":"Toast appears"}]""",
            screenshotDirectory: null);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("locator", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainAsync_accepts_null_screenshot_directory()
    {
        var llm = new StubLlmClient("Failure explained without screenshots.");
        var agent = new QaLoopFailureAnalystAgent(llm);

        var text = await agent.ExplainAsync("timeout", "Open inbox", "[]", screenshotDirectory: null);

        Assert.Equal("Failure explained without screenshots.", text);
        Assert.Contains("(none", QaLoopFailureAnalystAgent.BuildPrompt("timeout", "Open inbox", "[]", null));
    }

    [Fact]
    public void ComposeHealFailureMessage_appends_userContext()
    {
        var combined = PlaywrightAgentService.ComposeHealFailureMessage(
            "Error: timeout", "Use data-testid=save-btn");
        Assert.Contains("Error: timeout", combined);
        Assert.Contains("User context: Use data-testid=save-btn", combined);

        var prompt = PlaywrightAgentService.BuildHealPromptForTest(
            "/tmp/x.ui.spec.ts", "Error: timeout", "Use data-testid=save-btn");
        Assert.Contains("User context: Use data-testid=save-btn", prompt);
    }

    [Fact]
    public void ComposeHealFailureMessage_omits_blank_userContext()
    {
        Assert.Equal("Error: timeout",
            PlaywrightAgentService.ComposeHealFailureMessage("Error: timeout", null));
        Assert.Equal("Error: timeout",
            PlaywrightAgentService.ComposeHealFailureMessage("Error: timeout", "  "));
    }

    [Fact]
    public async Task ExplainFailure_endpoint_returns_404_when_draft_missing()
    {
        await using var h = await ExplainHarness.CreateAsync();
        var result = await h.Controller.ExplainFailure(h.SessionId, new ExplainFailureRequest(99999), CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task ExplainFailure_endpoint_returns_400_when_ErrorMessage_null()
    {
        await using var h = await ExplainHarness.CreateAsync();
        var draftId = await h.SeedUiScriptAsync(errorMessage: null);
        var result = await h.Controller.ExplainFailure(h.SessionId, new ExplainFailureRequest(draftId), CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
    }

    private sealed class FixedLlmFactory(ILlmClient client) : ILlmClientFactory
    {
        public Task<ILlmClient> CreateAsync(CancellationToken ct = default) => Task.FromResult(client);
    }

    private sealed class ExplainHarness : IAsyncDisposable
    {
        public required QaLoopController Controller { get; init; }
        public required int SessionId { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required IServiceScope Scope { get; init; }
        private string DbPath { get; init; } = "";

        public static async Task<ExplainHarness> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"qa_explain_{Guid.NewGuid():N}.db");
            var dataDir = Path.Combine(Path.GetTempPath(), $"qa_explain_data_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDir);

            var services = new ServiceCollection();
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
            services.AddScoped<DatabaseInitializer>();
            services.AddSingleton<ITextSearchProvider, SqliteFts5Search>();
            var provider = services.BuildServiceProvider();
            using (var init = provider.CreateScope())
                init.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            // Keep one scope alive for the controller's DbContext.
            var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var session = new ApiTestSession
            {
                ProjectId = 1,
                Module = "Deals",
                Status = ApiSessionStatus.ApiDone,
                UiStatus = ApiUiStatus.Drafted,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestSessions.Add(session);
            await db.SaveChangesAsync();

            var controller = new QaLoopController(
                orchestrator: null!,
                bus: new EventBus(),
                db: db,
                ado: null!,
                frontendModules: null!,
                llmFactory: new FixedLlmFactory(new StubLlmClient("ok")),
                opts: Options.Create(new QaLoopOptions { RunsFolderName = "runs" }),
                storage: Options.Create(new StorageOptions { DataDirectory = dataDir }),
                adoOptions: null!,
                log: NullLogger<QaLoopController>.Instance);

            return new ExplainHarness
            {
                Controller = controller,
                SessionId = session.Id,
                Provider = provider,
                Scope = scope,
                DbPath = dbPath,
            };
        }

        public async Task<int> SeedUiScriptAsync(string? errorMessage)
        {
            var db = Scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = SessionId,
                Title = "Case",
                Preconditions = "",
                StepsJson = "[]",
                SpecCode = "//",
                SpecFilePath = "x.ui.spec.ts",
                Status = ApiDraftStatus.Draft,
                Level = TestLevel.Ui,
                DraftKind = ApiDraftKind.UiScript,
                Bucket = "smoke",
                ErrorMessage = errorMessage,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        }

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Provider.DisposeAsync();
            try { File.Delete(DbPath); } catch { /* ignore */ }
        }
    }
}
