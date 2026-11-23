using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.ScriptsBoard;
using QaAutomation.Infrastructure.TextSearch;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>QLV2-302 — Scripts Board disk↔DB reconciliation. Seeds a temp scripts dir + a temp PostgreSQL
/// DB to cover the four reconcile states (see docs/progress/RalphLoop/qa-loop-v2/blueprint.md → SLAB 3).</summary>
public sealed class ScriptInventoryServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"sb_inv_{Guid.NewGuid():N}");
    private readonly PostgresTestDatabase _pg;
    private readonly ServiceProvider _provider;

    public ScriptInventoryServiceTests()
    {
        _pg = PostgresTestDatabase.Create("script_inventory");
        Directory.CreateDirectory(_dataDir);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<QaDbContext>(o => _pg.Configure(o));
        services.AddScoped<DatabaseInitializer>();
        services.AddSingleton<ITextSearchProvider, PostgresFtsSearch>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pg.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    private IScriptInventoryService CreateService(QaDbContext db, string? playwrightRoot = null, string? automationRoot = null) =>
        new ScriptInventoryService(
            db,
            Options.Create(new StorageOptions { DataDirectory = _dataDir, KnowledgebaseFolderName = "kb" }),
            new StaticOptionsMonitor(new QaLoopOptions
            {
                ScriptsFolderName = "scripts",
                PlaywrightRoot = playwrightRoot ?? "",
                AutomationRoot = automationRoot ?? "",
            }));

    private sealed class StaticOptionsMonitor(QaLoopOptions value) : IOptionsMonitor<QaLoopOptions>
    {
        public QaLoopOptions CurrentValue => value;
        public QaLoopOptions Get(string? name) => value;
        public IDisposable OnChange(Action<QaLoopOptions, string?> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private async Task<int> SeedSessionAsync(QaDbContext db, string module)
    {
        var session = new ApiTestSession
        {
            ProjectId = 1, Module = module, Status = ApiSessionStatus.Drafted,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.ApiTestSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<int> SeedDraftAsync(QaDbContext db, int sessionId, string specPath)
    {
        var draft = new ApiTestDraft
        {
            SessionId = sessionId, Level = TestLevel.Contract, Title = "GET /leads/1",
            Preconditions = "", StepsJson = "[]", SpecCode = "", SpecFilePath = specPath,
            Status = ApiDraftStatus.Draft, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.ApiTestDrafts.Add(draft);
        await db.SaveChangesAsync();
        return draft.Id;
    }

    [Fact]
    public async Task Stamped_spec_with_matching_draft_row_is_linked()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var sessionId = await SeedSessionAsync(db, "leads");

        var levelDir = Path.Combine(_dataDir, "scripts", "leads", "contracts");
        Directory.CreateDirectory(levelDir);
        var specPath = Path.Combine(levelDir, "get-leads-1.spec.ts");
        var meta = new QaMeta("leads", "api", "contract", null, null, null, DateTime.UtcNow, "hash1");
        await File.WriteAllTextAsync(specPath, QaMetaHeader.Upsert("import { test } from '@playwright/test';\n", meta));
        var draftId = await SeedDraftAsync(db, sessionId, specPath);

        var items = await CreateService(db).GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Equal(ScriptReconcileStates.Linked, item.ReconcileState);
        Assert.Equal(draftId, item.DraftId);
        Assert.Equal("leads", item.Module);
        Assert.Equal("contract", item.Level);
        Assert.NotNull(item.Meta);
    }

    [Fact]
    public async Task Unstamped_legacy_spec_with_draft_row_is_no_meta()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var sessionId = await SeedSessionAsync(db, "leads");

        var levelDir = Path.Combine(_dataDir, "scripts", "leads", "contracts");
        Directory.CreateDirectory(levelDir);
        var specPath = Path.Combine(levelDir, "legacy.spec.ts");
        await File.WriteAllTextAsync(specPath, "import { test } from '@playwright/test';\n// no header here");
        await SeedDraftAsync(db, sessionId, specPath);

        var items = await CreateService(db).GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Equal(ScriptReconcileStates.NoMeta, item.ReconcileState);
        Assert.Null(item.Meta);
    }

    [Fact]
    public async Task Hand_added_file_with_no_draft_row_is_orphan_disk()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var levelDir = Path.Combine(_dataDir, "scripts", "leads", "endpoints");
        Directory.CreateDirectory(levelDir);
        var specPath = Path.Combine(levelDir, "hand-added.spec.ts");
        await File.WriteAllTextAsync(specPath, "import { test } from '@playwright/test';\n// hand-written");

        var items = await CreateService(db).GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Equal(ScriptReconcileStates.OrphanDisk, item.ReconcileState);
        Assert.Null(item.DraftId);
        Assert.Equal("leads", item.Module);      // derived from path, not @qa-meta
        Assert.Equal("endpoint", item.Level);
    }

    [Fact]
    public async Task Draft_row_whose_file_is_missing_is_orphan_db()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var sessionId = await SeedSessionAsync(db, "leads");
        var missingPath = Path.Combine(_dataDir, "scripts", "leads", "contracts", "gone.spec.ts");
        var draftId = await SeedDraftAsync(db, sessionId, missingPath);

        var items = await CreateService(db).GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Equal(ScriptReconcileStates.OrphanDb, item.ReconcileState);
        Assert.Equal(draftId, item.DraftId);
        Assert.Null(item.Meta);
    }

    [Fact]
    public async Task Empty_PlaywrightRoot_skips_ui_scan_without_throwing()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var items = await CreateService(db, playwrightRoot: "").GetInventoryAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task Nonexistent_PlaywrightRoot_skips_ui_scan_without_throwing()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var items = await CreateService(db, playwrightRoot: Path.Combine(_dataDir, "no-such-dir")).GetInventoryAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task AutomationRoot_framework_layout_lists_deals_api_specs()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var autoRoot = Path.Combine(_dataDir, "product-automation");
        var contracts = Path.Combine(autoRoot, "modules", "deals", "api", "tests", "contracts");
        Directory.CreateDirectory(contracts);
        var endpoints = Path.Combine(autoRoot, "modules", "deals", "api", "tests", "endpoints");
        Directory.CreateDirectory(endpoints);
        await File.WriteAllTextAsync(Path.Combine(contracts, "pipelines.contract.spec.ts"),
            "import { test } from '@playwright/test';\n");
        await File.WriteAllTextAsync(Path.Combine(endpoints, "add-deal.endpoint.spec.ts"),
            "import { test } from '@playwright/test';\n");
        // schemas must not appear as inventory items
        var schemas = Path.Combine(autoRoot, "modules", "deals", "api", "schemas");
        Directory.CreateDirectory(schemas);
        await File.WriteAllTextAsync(Path.Combine(schemas, "deal.schema.ts"), "export const x = 1;\n");

        var items = await CreateService(db, automationRoot: autoRoot).GetInventoryAsync();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("deals", i.Module));
        Assert.All(items, i => Assert.Equal("api", i.Type));
        Assert.All(items, i => Assert.Equal(ScriptReconcileStates.OrphanDisk, i.ReconcileState));
        Assert.Contains(items, i => i.Level == "contract");
        Assert.Contains(items, i => i.Level == "endpoint");
    }

    [Fact]
    public async Task AutomationRoot_skips_legacy_private_scripts()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var legacyDir = Path.Combine(_dataDir, "scripts", "leads", "contracts");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "legacy.spec.ts"),
            "import { test } from '@playwright/test';\n");

        var autoRoot = Path.Combine(_dataDir, "product-automation-only");
        var contracts = Path.Combine(autoRoot, "modules", "deals", "api", "tests", "contracts");
        Directory.CreateDirectory(contracts);
        await File.WriteAllTextAsync(Path.Combine(contracts, "deal.contract.spec.ts"),
            "import { test } from '@playwright/test';\n");

        var items = await CreateService(db, automationRoot: autoRoot).GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Contains("product-automation-only", item.SpecFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(items, i => i.SpecFilePath.Contains("legacy.spec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Empty_AutomationRoot_keeps_legacy_scripts_only()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

        var levelDir = Path.Combine(_dataDir, "scripts", "leads", "contracts");
        Directory.CreateDirectory(levelDir);
        await File.WriteAllTextAsync(Path.Combine(levelDir, "legacy.spec.ts"),
            "import { test } from '@playwright/test';\n");

        // Framework tree exists but AutomationRoot unset → ignored
        var ghost = Path.Combine(_dataDir, "ghost-automation", "modules", "deals", "api", "tests", "contracts");
        Directory.CreateDirectory(ghost);
        await File.WriteAllTextAsync(Path.Combine(ghost, "ghost.spec.ts"), "import { test } from '@playwright/test';\n");

        var items = await CreateService(db, automationRoot: "").GetInventoryAsync();

        var item = Assert.Single(items);
        Assert.Equal("leads", item.Module);
        Assert.DoesNotContain(items, i => i.SpecFilePath.Contains("ghost", StringComparison.OrdinalIgnoreCase));
    }
}
