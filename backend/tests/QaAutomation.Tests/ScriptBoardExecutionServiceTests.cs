using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.ScriptsBoard;
using QaAutomation.Infrastructure.TextSearch;
using Xunit;

namespace QaAutomation.Tests;

/// <summary>QLV2-303 — ad-hoc Scripts Board execution: filter resolution and the negative-RunId /
/// status-lifecycle contract (see docs/progress/RalphLoop/qa-loop-v2/blueprint.md → SLAB 3).</summary>
public sealed class ScriptBoardExecutionServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"sb_exec_{Guid.NewGuid():N}");
    private readonly PostgresTestDatabase _pg;
    private readonly ServiceProvider _provider;
    private readonly IEventBus _bus = new EventBus();

    public ScriptBoardExecutionServiceTests()
    {
        _pg = PostgresTestDatabase.Create("script_board");
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

    private IScriptInventoryService CreateInventory(QaDbContext db) => new ScriptInventoryService(
        db,
        Options.Create(new StorageOptions { DataDirectory = _dataDir, KnowledgebaseFolderName = "kb" }),
        new FixedQaLoopOptions(new QaLoopOptions { ScriptsFolderName = "scripts" }));

    private sealed class FixedQaLoopOptions(QaLoopOptions value) : IOptionsMonitor<QaLoopOptions>
    {
        public QaLoopOptions CurrentValue => value;
        public QaLoopOptions Get(string? name) => value;
        public IDisposable OnChange(Action<QaLoopOptions, string?> listener) => Empty.Instance;
        private sealed class Empty : IDisposable
        {
            public static readonly Empty Instance = new();
            public void Dispose() { }
        }
    }
    private ScriptBoardExecutionService CreateSut(QaDbContext db, IScriptsBoardRunner runner) =>
        new(db, CreateInventory(db), runner, _bus);

    private async Task<(int SessionId, string SpecPath)> SeedStampedSpecAsync(QaDbContext db, string module, string bucket = "")
    {
        var session = new ApiTestSession
        {
            ProjectId = 1, Module = module, Status = ApiSessionStatus.Drafted,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.ApiTestSessions.Add(session);
        await db.SaveChangesAsync();

        var levelDir = Path.Combine(_dataDir, "scripts", module, "contracts");
        Directory.CreateDirectory(levelDir);
        var specPath = Path.Combine(levelDir, $"spec-{Guid.NewGuid():N}.spec.ts");
        await File.WriteAllTextAsync(specPath, "import { test } from '@playwright/test';\n");

        db.ApiTestDrafts.Add(new ApiTestDraft
        {
            SessionId = session.Id, Level = TestLevel.Contract, Title = "GET /x",
            Preconditions = "", StepsJson = "[]", SpecCode = "", SpecFilePath = specPath,
            Status = ApiDraftStatus.Draft, Bucket = bucket, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (session.Id, specPath);
    }

    [Fact]
    public async Task StartAsync_module_and_level_filter_narrows_to_matching_specs_only()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var (_, leadsPath) = await SeedStampedSpecAsync(db, "leads");
        await SeedStampedSpecAsync(db, "deals");

        var sut = CreateSut(db, new StubPlaywrightRunner());
        var runId = await sut.StartAsync(new ScriptBoardRunFilter(
            "leads", "contract", null, null, null, SpecFilePaths: null, Headed: false, Workers: null));

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.Equal("leads", run!.Module);
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(run.SpecFilePathsJson)!;
        Assert.Equal([leadsPath], paths);
    }

    [Fact]
    public async Task StartAsync_explicit_SpecFilePaths_bypasses_inventory_filtering()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");   // exists in inventory but NOT referenced explicitly

        var explicitPath = Path.Combine(_dataDir, "hand-picked.spec.ts");
        var sut = CreateSut(db, new StubPlaywrightRunner());
        var runId = await sut.StartAsync(new ScriptBoardRunFilter(
            null, null, null, null, null, SpecFilePaths: [explicitPath], Headed: false, Workers: null));

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(run!.SpecFilePathsJson)!;
        Assert.Equal([explicitPath], paths);
    }

    [Fact]
    public async Task StartAsync_throws_when_selection_resolves_to_zero_specs()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var sut = CreateSut(db, new StubPlaywrightRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.StartAsync(
            new ScriptBoardRunFilter("nonexistent-module", null, null, null, null, null, false, null)));
    }

    [Fact]
    public async Task ExecuteAsync_against_stub_runner_marks_row_Passed_with_stub_counts()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner { ResultToReturn = new PlaywrightRunResult { Passed = 3, Failed = 0, Skipped = 1 } };
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter("leads", null, null, null, null, null, false, null));

        await sut.ExecuteAsync(runId);

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal(TestRunStatus.Passed, run!.Status);
        Assert.Equal(3, run.Passed);
        Assert.Equal(1, run.Skipped);
        Assert.Equal(-runId, runner.LastSpec!.RunId);   // negative-namespace trick
    }

    [Fact]
    public async Task StartAsync_persists_Profile_and_Execute_puts_it_on_PlaywrightRunSpec()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner();
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter(
            "leads", null, null, null, null, null, false, null, Profile: "infinity"));

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal("infinity", run!.Profile);

        await sut.ExecuteAsync(runId);

        Assert.Equal("infinity", runner.LastSpec!.Profile);
    }

    [Fact]
    public async Task ExecuteAsync_with_failures_marks_row_Failed()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner { ResultToReturn = new PlaywrightRunResult { Passed = 1, Failed = 2 } };
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter("leads", null, null, null, null, null, false, null));

        await sut.ExecuteAsync(runId);

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal(TestRunStatus.Failed, run!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_nonzero_exit_with_zero_failures_marks_Error()
    {
        // Reproduces the Scripts Board console bug: Playwright crashes in auth.setup
        // (missing @playwright/test) → ExitCode=1, Failed=0 — must NOT show Passed.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner
        {
            ResultToReturn = new PlaywrightRunResult { ExitCode = 1, Passed = 0, Failed = 0, Skipped = 0 },
        };
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter("leads", null, null, null, null, null, false, null));

        await sut.ExecuteAsync(runId);

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal(TestRunStatus.Error, run!.Status);
        Assert.Contains("exited with code 1", run.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_exit_zero_with_no_tests_executed_marks_Error()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner
        {
            ResultToReturn = new PlaywrightRunResult { ExitCode = 0, Passed = 0, Failed = 0, Skipped = 0 },
        };
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter("leads", null, null, null, null, null, false, null));

        await sut.ExecuteAsync(runId);

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal(TestRunStatus.Error, run!.Status);
        Assert.Contains("no tests executed", run.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_cancelled_leaves_row_Cancelled()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        await SeedStampedSpecAsync(db, "leads");

        var runner = new StubPlaywrightRunner { ThrowCancelled = true };
        var sut = CreateSut(db, runner);
        var runId = await sut.StartAsync(new ScriptBoardRunFilter("leads", null, null, null, null, null, false, null));

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.ExecuteAsync(runId));

        var run = await db.ScriptBoardRuns.FindAsync(runId);
        Assert.Equal(TestRunStatus.Cancelled, run!.Status);
    }

    private sealed class StubPlaywrightRunner : IScriptsBoardRunner
    {
        public PlaywrightRunResult ResultToReturn { get; set; } = new();
        public bool ThrowCancelled { get; set; }
        public PlaywrightRunSpec? LastSpec { get; private set; }

        public Task<PlaywrightRunResult> RunWithLiveTermAsync(
            PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
        {
            LastSpec = spec;
            if (ThrowCancelled) throw new OperationCanceledException();
            return Task.FromResult(ResultToReturn);
        }

    }
}
