using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.Data;
using QaAutomation.Infrastructure.Events;
using QaAutomation.Infrastructure.Orchestrators;
using QaAutomation.Infrastructure.QaLoop;
using QaAutomation.Infrastructure.TextSearch;

namespace QaAutomation.Tests;

/// <summary>
/// Integration tests for the QA Loop pipeline (QAL-051). Drives QaLoopOrchestrator against a
/// real PostgreSQL DB, a stub ADO client, and a stub LLM returning canned JSON.
/// </summary>
public sealed class QaLoopOrchestratorTests
{
    // ModuleAnalyzer output with an empty inventory — no generators will be invoked.
    private const string EmptyAnalyzerJson = """
        {"inventory":{"routes":[],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[]},"additionalFilesNeeded":[]}
        """;

    // Analyzer with 1 gap (GET /deals) + 1 covered (POST /deals) — only the gap triggers a generator.
    private const string OneGapAnalyzerJson = """
        {"inventory":{"routes":[
            {"method":"GET","route":"/deals","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]},
            {"method":"POST","route":"/deals","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]}
        ],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[
            {"id":"c1","level":"contract","label":"Contract GET /deals","status":"gap"},
            {"id":"c2","level":"contract","label":"Contract POST /deals","status":"covered"}
        ]},"additionalFilesNeeded":[]}
        """;

    // ContractGenerator LLM outputs (2 passes: spec+schema, then test-case array).
    private const string ContractSpecJson  = """{"spec":"","schema":"{}"}""";
    private const string ContractTcJson    = """
        [{"title":"[<tcId-pending>] Deals GET /deals contract","preconditions":"","steps":[{"action":"do x","expected":"see y"}]}]
        """;

    [Fact]
    public async Task Analyze_stops_at_AwaitingReview_when_AutoHandoff_is_false()
    {
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);
        Assert.NotNull(session.InventoryJson);
        Assert.NotNull(session.AnalysisJson);
    }

    [Fact]
    public async Task Analyze_populates_WorkItemTitle_from_the_linked_work_item()
    {
        // QAL-070: mirrors AgentOrchestrator/BugOrchestrator, which fetch and store WorkItemTitle at
        // session creation — QaLoopOrchestrator's session is pre-seeded by the controller instead, so
        // the fetch has to happen in the background pipeline itself.
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));
        h.Ado.WorkItemToReturn = new AdoWorkItem { Id = 100001, Title = "Deals: add lead endpoint" };

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false, workItemId: 100001);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);
        Assert.Equal("Deals: add lead endpoint", session.WorkItemTitle);
    }

    [Fact]
    public async Task Analyze_leaves_WorkItemTitle_null_and_still_completes_when_the_ADO_fetch_throws()
    {
        // A failed work-item fetch must degrade gracefully: catch-log, leave WorkItemTitle null,
        // never fail the scan.
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));
        h.Ado.ThrowOnGetWorkItem = true;

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false, workItemId: 100001);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.AwaitingReview, session.Status);   // scan still completed
        Assert.Null(session.WorkItemTitle);
        Assert.Null(session.ErrorMessage);   // the failure never bubbled up as a scan error
    }

    [Fact]
    public async Task Analyze_with_AutoHandoff_reaches_Drafted()
    {
        using var h = Harness.Create(new StubLlmClient(EmptyAnalyzerJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: true);
        await h.Orchestrator.StartAsync(sid);
        var session = await h.WaitForTerminalAsync(sid);

        Assert.Equal(ApiSessionStatus.Drafted, session.Status);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        Assert.Empty(db.ApiTestDrafts.Where(d => d.SessionId == sid).ToList());   // empty surface → 0 drafts
    }

    [Fact]
    public async Task GenerateDrafts_creates_draft_only_for_selected_gap_surfaces()
    {
        // Analyze with OneGap → AwaitingReview (1 gap, 1 covered); then generate → only 1 draft
        using var h = Harness.Create(new StubLlmClient(OneGapAnalyzerJson, ContractSpecJson, ContractTcJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        await h.WaitForTerminalAsync(sid);    // AwaitingReview

        await h.Orchestrator.GenerateDraftsAsync(sid);
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        Assert.Equal(ApiSessionStatus.Drafted, session.Status);
        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = db.ApiTestDrafts.Where(d => d.SessionId == sid).ToList();
        var draft = Assert.Single(drafts);    // only the gap, not the covered surface
        Assert.Equal(ApiDraftStatus.Draft, draft.Status);
    }

    [Fact]
    public async Task GenerateDrafts_rewrites_Contract_schema_import_to_a_deterministic_filename()
    {
        // QAL-071b: the model's own relative import path is untrustworthy — it has been observed
        // using both './x' and '../schemas/x' conventions, sometimes with no matching "schema"
        // field at all. A single unresolvable import aborts Playwright's test COLLECTION for every
        // spec in the run (not just the broken one), so the writer no longer trusts the model's
        // path: it rewrites the spec's import to a filename it controls — the same slug as the
        // spec's own .spec.ts file — and writes the schema there.
        const string specWithSchemaImport = """
            import { test, expect } from '@playwright/test';
            import { DealDetailsDtoSchema } from './get-lead-deal-dealId.schema';

            test('[<tcId-pending>] Contract: GET /deals/{dealId} - response shape matches schema', async () => {
              expect(DealDetailsDtoSchema).toBeDefined();
            });
            """;
        var specJson = System.Text.Json.JsonSerializer.Serialize(
            new { spec = specWithSchemaImport, schema = "{\"type\":\"object\"}" });

        using var h = Harness.Create(new StubLlmClient(OneGapAnalyzerJson, specJson, ContractTcJson));

        var sid = await h.SeedSessionAsync(module: "Deals", autoHandoff: false);
        await h.Orchestrator.StartAsync(sid);
        await h.WaitForTerminalAsync(sid);    // AwaitingReview

        await h.Orchestrator.GenerateDraftsAsync(sid);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = db.ApiTestDrafts.Single(d => d.SessionId == sid);
        Assert.NotNull(draft.SpecFilePath);

        // Slug = spec filename minus ".spec.ts" — the same slug PersistDraft derives the schema
        // filename from, regardless of whatever import path the model originally wrote.
        var slug = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(draft.SpecFilePath!));
        var schemaPath = Path.Combine(Path.GetDirectoryName(draft.SpecFilePath)!, $"{slug}.schema.ts");
        Assert.True(File.Exists(schemaPath), $"expected schema file at {schemaPath}");
        Assert.Equal("{\"type\":\"object\"}", File.ReadAllText(schemaPath));

        // The on-disk spec's import must be rewritten to the deterministic path, not left as the
        // model's original './get-lead-deal-dealId.schema'.
        var writtenSpec = File.ReadAllText(draft.SpecFilePath!);
        Assert.Contains($"from './{slug}.schema'", writtenSpec);
        Assert.Equal(writtenSpec, draft.SpecCode);
    }

    [Fact]
    public async Task Push_continues_after_draft_failure_and_stays_Drafted_not_Failed()
    {
        using var h = Harness.Create(new StubLlmClient());
        h.Ado.FailCreateTitles.Add("[<tcId-pending>] First fails");

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        var failId = await h.SeedDraftAsync(sid, "[<tcId-pending>] First fails", ApiDraftStatus.Approved);
        var okId = await h.SeedDraftAsync(sid, "[<tcId-pending>] Second ok", ApiDraftStatus.Approved);

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20);
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        Assert.Equal(ApiSessionStatus.Drafted, session.Status);
        Assert.Single(h.Ado.CreatedTestCases);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var fail = await db.ApiTestDrafts.FindAsync(failId);
        var ok = await db.ApiTestDrafts.FindAsync(okId);
        Assert.Equal(ApiDraftStatus.Approved, fail!.Status);
        Assert.NotNull(fail.ErrorMessage);
        Assert.Equal(ApiDraftStatus.Pushed, ok!.Status);
    }

    [Fact]
    public async Task Push_api_lane_skips_ui_drafts()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] API case", ApiDraftStatus.Approved);
        await h.SeedUiScriptDraftAsync(sid, "[<tcId-pending>] UI case", bucket: "smoke");

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20, draftKindFilter: "api");
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);
        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var ui = db.ApiTestDrafts.Single(d => d.DraftKind == ApiDraftKind.UiScript);
        Assert.Equal(ApiDraftStatus.Approved, ui.Status);
    }

    [Fact]
    public async Task GenerateDrafts_product_mode_slug_matches_ScriptPathAdapter()
    {
        const string cpqRouteJson = """
            {"inventory":{"routes":[
                {"method":"GET","route":"/cpq+/items","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]}
            ],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[
                {"id":"c1","level":"contract","label":"Contract GET /cpq+/items","status":"gap"}
            ]},"additionalFilesNeeded":[]}
            """;
        const string specJson = """{"spec":"import x from './old.schema';","schema":"{}"}""";
        const string tcJson = """[{"title":"[<tcId-pending>] CPQ item contract","preconditions":"","steps":[{"action":"x","expected":"y"}]}]""";

        var autoRoot = Path.Combine(Path.GetTempPath(), "auto_slug_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(autoRoot);
        try
        {
            using var h = Harness.Create(
                new StubLlmClient(cpqRouteJson, specJson, tcJson),
                qaLoop: new QaLoopOptions
                {
                    AutomationRoot = autoRoot,
                    ScriptsFolderName = "scripts",
                    RunsFolderName = "runs",
                });

            var sid = await h.SeedSessionAsync(module: "cpq-plus", autoHandoff: false);
            await h.Orchestrator.StartAsync(sid);
            await h.WaitForTerminalAsync(sid, ApiSessionStatus.AwaitingReview);
            await h.Orchestrator.GenerateDraftsAsync(sid);
            await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

            using var scope = h.Provider.CreateScope();
            var draft = scope.ServiceProvider.GetRequiredService<QaDbContext>()
                .ApiTestDrafts.Single(d => d.SessionId == sid);
            var expectedSlug = ScriptPathAdapter.Slug("GET /cpq+/items");
            Assert.Contains($"{expectedSlug}.spec.ts", draft.SpecFilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("get-cpq-plus-items", expectedSlug);
        }
        finally
        {
            try { Directory.Delete(autoRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PushDraft_with_HasUiPhase_does_not_set_Done_until_ui_complete()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.HasUiPhase = true;
            sess.Status = ApiSessionStatus.Drafted;
            await db.SaveChangesAsync();
        }

        var draftId = await h.SeedDraftAsync(sid, "[<tcId-pending>] API only", ApiDraftStatus.Approved);
        await h.Orchestrator.PushDraftAsync(draftId, testPlanId: 10, testSuiteId: 20);

        using var check = h.Provider.CreateScope();
        var session = await check.ServiceProvider.GetRequiredService<QaDbContext>()
            .ApiTestSessions.AsNoTracking().FirstAsync(s => s.Id == sid);
        Assert.Equal(ApiSessionStatus.ApiDone, session.Status);
        Assert.NotEqual(ApiSessionStatus.Done, session.Status);
    }

    [Fact]
    public async Task Push_rewrites_pending_tag_and_sets_AdoTestCaseId()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: 1001);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] My first test case", ApiDraftStatus.Approved);
        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20, storyId: null);
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Equal(ApiSessionStatus.Done, session.Status);
        Assert.Single(h.Ado.CreatedTestCases);
        Assert.Single(h.Ado.SuiteAdds);
        Assert.Single(h.Ado.TestedByLinks);   // WorkItemId set → linked

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var draft = db.ApiTestDrafts.Single(d => d.SessionId == sid);
        Assert.Equal(ApiDraftStatus.Pushed, draft.Status);
        Assert.NotNull(draft.AdoTestCaseId);
        Assert.DoesNotContain("[<tcId-pending>]", draft.Title);    // tag rewritten
        Assert.Contains($"[{draft.AdoTestCaseId}]", draft.Title);  // replaced with real id
    }

    [Fact]
    public async Task PushDraft_with_AdoTestCaseId_patches_instead_of_create()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: 100002);
        var draftId = await h.SeedDraftAsync(sid, "Thin import case", ApiDraftStatus.Approved);
        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var d = await db.ApiTestDrafts.FindAsync(draftId);
            d!.AdoTestCaseId = 100002;
            d.StepsJson = """[{"action":"Open","expected":"Ok"}]""";
            await db.SaveChangesAsync();
        }

        var pushed = await h.Orchestrator.PushDraftAsync(draftId, testPlanId: 10, testSuiteId: 20);
        Assert.Equal(100002, pushed.AdoTestCaseId);
        Assert.Equal(ApiDraftStatus.Pushed, pushed.Status);
        Assert.Empty(h.Ado.CreatedTestCases);
        Assert.Contains(h.Ado.UpdatedTestCases, u => u.Id == 100002 && u.Title.Contains("Thin import"));
    }

    [Fact]
    public async Task Push_of_shared_multicase_spec_in_scrambled_order_rewrites_each_cases_own_tag_only()
    {
        // QAL-067: a shared Endpoint spec file has 3 test( ) blocks, one per case, each carrying its
        // own INDEXED placeholder. Pushing case 1 before case 0 before case 2 must still land each
        // case's real ADO id on its own line — a global unscoped string replace would let whichever
        // case pushes FIRST stomp every other case's still-pending placeholder in the shared file.
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);

        var specPath = Path.Combine(Path.GetTempPath(), $"qal067_shared_{Guid.NewGuid():N}.spec.ts");
        const string sharedSpec = """
            import { test, expect } from '@playwright/test';

            test('[<tcId-pending:0>] Endpoint: GET /deals - 200 happy path', async () => {
              expect(200).toBe(200);
            });

            test('[<tcId-pending:1>] Endpoint: GET /deals - 401 without bearer', async () => {
              expect(401).toBe(401);
            });

            test('[<tcId-pending:2>] Endpoint: GET /deals - 400 on invalid input', async () => {
              expect(400).toBe(400);
            });
            """;
        File.WriteAllText(specPath, sharedSpec);
        try
        {
            var d0 = await h.SeedDraftAsync(sid, "[<tcId-pending:0>] Endpoint: GET /deals - 200 happy path",
                ApiDraftStatus.Approved, caseIndex: 0, specFilePath: specPath);
            var d1 = await h.SeedDraftAsync(sid, "[<tcId-pending:1>] Endpoint: GET /deals - 401 without bearer",
                ApiDraftStatus.Approved, caseIndex: 1, specFilePath: specPath);
            var d2 = await h.SeedDraftAsync(sid, "[<tcId-pending:2>] Endpoint: GET /deals - 400 on invalid input",
                ApiDraftStatus.Approved, caseIndex: 2, specFilePath: specPath);

            // Scrambled push order: 1, 0, 2.
            var pushed1 = await h.Orchestrator.PushDraftAsync(d1, testPlanId: 10, testSuiteId: 20);
            var pushed0 = await h.Orchestrator.PushDraftAsync(d0, testPlanId: 10, testSuiteId: 20);
            var pushed2 = await h.Orchestrator.PushDraftAsync(d2, testPlanId: 10, testSuiteId: 20);

            var ids = new[] { pushed0.AdoTestCaseId, pushed1.AdoTestCaseId, pushed2.AdoTestCaseId };
            Assert.All(ids, id => Assert.NotNull(id));
            Assert.Equal(3, ids.Distinct().Count());   // three distinct real ADO ids, one per case

            // Each draft's own title carries only ITS id — never another case's placeholder or id.
            Assert.Contains($"[{pushed0.AdoTestCaseId}]", pushed0.Title);
            Assert.Contains($"[{pushed1.AdoTestCaseId}]", pushed1.Title);
            Assert.Contains($"[{pushed2.AdoTestCaseId}]", pushed2.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed0.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed1.Title);
            Assert.DoesNotContain("[<tcId-pending", pushed2.Title);

            // The physical file (read once, after all 3 pushes) must show each case's OWN id on its
            // OWN test( ) line, with no leftover placeholders and no cross-contamination.
            var finalText = File.ReadAllText(specPath);
            var lines = finalText.Split('\n');
            var line0 = Assert.Single(lines, l => l.Contains("200 happy path"));
            var line1 = Assert.Single(lines, l => l.Contains("401 without bearer"));
            var line2 = Assert.Single(lines, l => l.Contains("400 on invalid input"));
            Assert.Contains($"[{pushed0.AdoTestCaseId}]", line0);
            Assert.Contains($"[{pushed1.AdoTestCaseId}]", line1);
            Assert.Contains($"[{pushed2.AdoTestCaseId}]", line2);
            Assert.DoesNotContain("tcId-pending", finalText);   // no placeholder survives
        }
        finally
        {
            try { File.Delete(specPath); } catch { }
        }
    }

    [Fact]
    public async Task Push_skips_LinkTestedBy_when_no_WorkItemId()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] No story test", ApiDraftStatus.Approved);
        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);
        Assert.Empty(h.Ado.TestedByLinks);   // no story → no link
    }

    [Fact]
    public async Task Repress_push_skips_already_pushed_drafts()
    {
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        // Draft 1 already pushed; Draft 2 still approved
        await h.SeedDraftAsync(sid, "[123] Already pushed", ApiDraftStatus.Pushed, adoId: 123);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] Still pending", ApiDraftStatus.Approved);

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);   // only the Approved draft
        Assert.Single(h.Ado.SuiteAdds);
    }

    // ── QLV2-205: UI bucket suite routing ────────────────────────────────────

    [Fact]
    public async Task Push_routes_UiScript_draft_to_bucket_sub_suite_not_level_suite()
    {
        // Golden test: a UiScript draft with Bucket=smoke must land in a Smoke sub-suite
        // under the Ui level suite, not in the root suite (testSuiteId=20).
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        await h.SeedUiScriptDraftAsync(sid, "[<tcId-pending>] Smoke: verify dashboard loads", bucket: "smoke");

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20, storyId: null);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);

        // Suite tree: Deals (module) → Ui (level) → Smoke/Regression/E2E (buckets).
        // Suites are allocated from NextSuiteId=900_000 in creation order.
        Assert.Contains(h.Ado.CreatedSuites, s => s.Name == "Smoke");
        var smokeIdx = h.Ado.CreatedSuites.FindIndex(s => s.Name == "Smoke");
        var smokeId = 900_000 + smokeIdx;

        // The test case must go to the Smoke bucket suite, not the root (20) or the Ui level suite.
        var add = Assert.Single(h.Ado.SuiteAdds);
        Assert.Equal(smokeId, add.SuiteId);
        Assert.NotEqual(20, add.SuiteId);
    }

    [Fact]
    public async Task Push_api_drafts_do_not_get_bucket_routing()
    {
        // API-level drafts (no DraftKind set) must still go to their level suite — no regression.
        using var h = Harness.Create(new StubLlmClient());

        var sid = await h.SeedSessionAsync(module: "Deals", workItemId: null);
        await h.SeedDraftAsync(sid, "[<tcId-pending>] Contract GET /deals", ApiDraftStatus.Approved);

        await h.Orchestrator.PushAllAsync(sid, testPlanId: 10, testSuiteId: 20, storyId: null);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Done);

        Assert.Single(h.Ado.CreatedTestCases);
        // No bucket suites should have been created (Smoke/Regression/E2E are UI-only)
        Assert.DoesNotContain(h.Ado.CreatedSuites, s => s.Name is "Smoke" or "Regression" or "E2E");
    }

    // ── QLV2-112: refine-merge and analyzer-refine ────────────────────────────

    [Fact]
    public async Task RefineDrafts_updates_targeted_removes_flagged_leaves_approved_and_pushed_untouched()
    {
        // Stub LLM: RefineAsync calls two passes each (spec + testcase) — 6 calls for 3 targeted drafts,
        // but we only target 1 here. Analyzer pass returns the usual EmptyAnalyzerJson (not invoked).
        var llm = new StubLlmClient(
            ContractSpecJson,                     // ContractGenerator pass 1 (spec)
            ContractTcJson                        // ContractGenerator pass 2 (test-case)
        );
        using var h = Harness.Create(llm);

        var sid = await h.SeedSessionAsync("Deals");
        // Seed InventoryJson so RefineDraftsInnerAsync can resolve the route for the targeted draft.
        await h.SetInventoryAsync(sid, OneGapAnalyzerJson);

        var draftDraft    = await h.SeedDraftAsync(sid, "[<tcId-pending>] Draft title",    ApiDraftStatus.Draft,    sourceRoutes: "GET /deals");
        var draftApproved = await h.SeedDraftAsync(sid, "[<tcId-pending>] Approved title", ApiDraftStatus.Approved, sourceRoutes: "GET /deals");
        var draftPushed   = await h.SeedDraftAsync(sid, "[123] Pushed title",              ApiDraftStatus.Pushed,   adoId: 123);
        var draftToRemove = await h.SeedDraftAsync(sid, "[<tcId-pending>] Remove me",      ApiDraftStatus.Draft,    sourceRoutes: "GET /deals");

        var perDraft = new List<QaAutomation.Core.Contracts.ApiDraftFeedback>
        {
            new() { DraftId = draftDraft,    Feedback = "Add 401 coverage" },
            new() { DraftId = draftToRemove, Remove = true },
        };
        await h.Orchestrator.RefineDraftsAsync(sid, overallFeedback: null, perDraft: perDraft);
        await h.WaitForTerminalAsync(sid, ApiSessionStatus.Drafted);

        using var scope = h.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = await db.ApiTestDrafts.Where(d => d.SessionId == sid).ToListAsync();

        // Targeted Draft → still in the DB, updated (status stays Draft — refine doesn't change status)
        Assert.True(drafts.Any(d => d.Id == draftDraft && d.Status == ApiDraftStatus.Draft));

        // Approved and Pushed must be untouched
        Assert.True(drafts.Any(d => d.Id == draftApproved && d.Status == ApiDraftStatus.Approved));
        Assert.True(drafts.Any(d => d.Id == draftPushed   && d.Status == ApiDraftStatus.Pushed));

        // Flagged Draft removed
        Assert.False(drafts.Any(d => d.Id == draftToRemove));
    }

    [Fact]
    public async Task RerunAnalyze_returns_session_to_AwaitingReview_with_refreshed_matrix()
    {
        // Stub LLM: RefineInventoryAsync is one pass → return a revised analyzer JSON.
        var refinedJson = """
            {"inventory":{"routes":[{"method":"GET","route":"/deals","requestDto":null,"responseDto":null,"validationRules":[],"authRequirements":[]}],"dtos":[],"workflows":[]},"surfaceMatrix":{"rows":[{"id":"c1","level":"contract","label":"Contract GET /deals (refined)","status":"gap"}]},"additionalFilesNeeded":[]}
            """;
        using var h = Harness.Create(new StubLlmClient(refinedJson));

        var sid = await h.SeedSessionAsync("Deals");
        await h.SetInventoryAsync(sid, OneGapAnalyzerJson);

        await h.Orchestrator.RerunAnalyzeAsync(sid, "Please add GET /deals coverage");
        var session = await h.WaitForTerminalAsync(sid, ApiSessionStatus.AwaitingReview);

        // Matrix should reflect the refined output
        Assert.Contains("refined", session.AnalysisJson ?? "");
        Assert.Equal("Please add GET /deals coverage", session.LastFeedback);
    }

    // ── QLV2-208: UI phase orchestrator tests ────────────────────────────────────

    private const string UiCaseAuthorJson = """
        [{"title":"Verify dashboard loads","preconditions":"User is logged in","steps":[{"action":"Navigate to /dashboard","expected":"Dashboard page is displayed"}],"bucket":"smoke"}]
        """;

    [Fact]
    public async Task AuthorUiCases_creates_UiCase_drafts_with_bucket()
    {
        using var h = Harness.Create(new StubLlmClient(UiCaseAuthorJson));
        var sid = await h.SeedSessionAsync("Deals");
        await h.SetInventoryAsync(sid, EmptyAnalyzerJson);

        await h.Orchestrator.AuthorUiCasesAsync(sid);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ApiUiStatus? uiStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
            if (sess?.UiStatus is not ApiUiStatus.Authoring and not null) { uiStatus = sess.UiStatus; break; }
            await Task.Delay(40);
        }

        using var checkScope = h.Provider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<QaDbContext>();
        var drafts = await checkDb.ApiTestDrafts.Where(d => d.SessionId == sid).ToListAsync();

        Assert.Equal(ApiUiStatus.AwaitingReview, uiStatus);
        Assert.Contains(drafts, d => d.DraftKind == ApiDraftKind.UiCase && d.Bucket == "smoke");
    }

    private const string UiAnalystOutputJson = """
        {"features":[{"id":"inbox","name":"Inbox","description":"Message list"}],"flows":[],"screens":[],"openQuestions":[],"mustTest":["Open inbox"],"mustNotTest":["API contracts"],"authPermissionsSummary":"Admin","tenantVariantsSummary":"none observed"}
        """;

    private const string RefinedUiAnalystOutputJson = """
        {"features":[{"id":"inbox","name":"Inbox refined","description":"Improved"}],"flows":[{"name":"Send","trigger":"Compose","steps":["Click compose"],"breaks":[]}],"screens":[],"openQuestions":[],"mustTest":["Send message"],"mustNotTest":[],"authPermissionsSummary":"Admin","tenantVariantsSummary":"none observed"}
        """;

    [Fact]
    public async Task AuthorUiCases_uses_QaLoopUiCaseAuthor_when_UiAnalysisJson_set()
    {
        // New FRS-grounded path: UiAnalysisJson present → QaLoopUiCaseAuthorAgent (array response).
        using var h = Harness.Create(new StubLlmClient(UiCaseAuthorJson));
        var sid = await h.SeedSessionAsync("messaging");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingUiAnalysisReview;
            sess.UiAnalysisJson = UiAnalystOutputJson;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.AuthorUiCasesAsync(sid);
        await WaitUiStatusAsync(h, sid, ApiUiStatus.AwaitingReview, TimeSpan.FromSeconds(10));

        using var check = h.Provider.CreateScope();
        var drafts = await check.ServiceProvider.GetRequiredService<QaDbContext>()
            .ApiTestDrafts.Where(d => d.SessionId == sid && d.DraftKind == ApiDraftKind.UiCase).ToListAsync();
        Assert.Contains(drafts, d => d.Bucket == "smoke" && d.Title.Contains("dashboard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthorUiCases_falls_back_to_inventory_agent_when_UiAnalysisJson_null()
    {
        // Backward-compat: no UiAnalysisJson → deprecated UiCaseAuthorAgent(inventory).
        using var h = Harness.Create(new StubLlmClient(UiCaseAuthorJson));
        var sid = await h.SeedSessionAsync("Deals");
        await h.SetInventoryAsync(sid, EmptyAnalyzerJson);

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = null;
            sess.UiAnalysisJson = null;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.AuthorUiCasesAsync(sid);
        await WaitUiStatusAsync(h, sid, ApiUiStatus.AwaitingReview, TimeSpan.FromSeconds(10));

        using var check = h.Provider.CreateScope();
        var drafts = await check.ServiceProvider.GetRequiredService<QaDbContext>()
            .ApiTestDrafts.Where(d => d.SessionId == sid && d.DraftKind == ApiDraftKind.UiCase).ToListAsync();
        Assert.NotEmpty(drafts);
    }

    [Fact]
    public async Task RerunUiAnalysis_replaces_UiAnalysisJson()
    {
        using var h = Harness.Create(new StubLlmClient(RefinedUiAnalystOutputJson));
        var sid = await h.SeedSessionAsync("messaging");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingUiAnalysisReview;
            sess.UiAnalysisJson = UiAnalystOutputJson;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.RerunUiAnalysisAsync(sid, "Add the send flow");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string? json = null;
        ApiUiStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.AsNoTracking().FirstAsync(s => s.Id == sid);
            if (sess.UiStatus == ApiUiStatus.AwaitingUiAnalysisReview
                && sess.UiAnalysisJson is { Length: > 0 }
                && sess.UiAnalysisJson.Contains("Inbox refined", StringComparison.Ordinal))
            {
                json = sess.UiAnalysisJson;
                status = sess.UiStatus;
                break;
            }
            await Task.Delay(40);
        }

        Assert.Equal(ApiUiStatus.AwaitingUiAnalysisReview, status);
        Assert.NotNull(json);
        // Persisted for the SPA — must be camelCase (not System.Text.Json PascalCase default).
        Assert.Contains("\"features\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Features\"", json, StringComparison.Ordinal);
        Assert.Contains("Inbox refined", json!);
        Assert.DoesNotContain("\"name\":\"Inbox\",\"description\":\"Message list\"", json!);
    }

    [Fact]
    public async Task GenerateUiScripts_creates_UiScript_drafts()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync("Deals");
        var caseId = await h.SeedUiCaseDraftAsync(sid, "Verify login", bucket: "smoke");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var d = await db.ApiTestDrafts.FindAsync(caseId);
            d!.Status = ApiDraftStatus.Approved;
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingReview;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.GenerateUiScriptsAsync(sid);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
            if (sess?.UiStatus == ApiUiStatus.Drafted) break;
            await Task.Delay(40);
        }

        using var checkScope = h.Provider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<QaDbContext>();
        var scripts = await checkDb.ApiTestDrafts
            .Where(d => d.SessionId == sid && d.DraftKind == ApiDraftKind.UiScript)
            .ToListAsync();

        Assert.NotEmpty(scripts);
        Assert.All(scripts, d => Assert.NotNull(d.SpecFilePath));
    }

    [Fact]
    public async Task GenerateUiScripts_caps_concurrency_at_2()
    {
        var stub = new StubPlaywrightAgentService(Path.GetTempPath(), delayMs: 150);
        using var h = Harness.Create(new StubLlmClient(), playwright: stub);
        var sid = await h.SeedSessionAsync("Deals");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingReview;
            for (var i = 0; i < 4; i++)
            {
                db.ApiTestDrafts.Add(new ApiTestDraft
                {
                    SessionId = sid,
                    Title = $"Case {i}",
                    Preconditions = "",
                    StepsJson = "[]",
                    Status = ApiDraftStatus.Approved,
                    Level = TestLevel.Ui,
                    DraftKind = ApiDraftKind.UiCase,
                    Bucket = "smoke",
                    SortOrder = i,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.GenerateUiScriptsAsync(sid);
        await WaitUiStatusAsync(h, sid, ApiUiStatus.Drafted, TimeSpan.FromSeconds(15));

        Assert.True(stub.MaxConcurrent <= 2, $"MaxConcurrent was {stub.MaxConcurrent}");
        Assert.Equal(4, stub.CallCount);
    }

    [Fact]
    public async Task GenerateUiScripts_timeout_sets_ErrorMessage_and_continues()
    {
        var stub = new StubPlaywrightAgentService(Path.GetTempPath(), delayMs: 5_000);
        using var h = Harness.Create(
            new StubLlmClient(),
            playwright: stub,
            qaLoop: new QaLoopOptions { ScriptsFolderName = "scripts", RunsFolderName = "runs", UiScriptGenerationTimeoutSeconds = 1 });
        var sid = await h.SeedSessionAsync("Deals");

        int fastId, slowId;
        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingReview;
            // First call hangs past timeout; second is also slow but we only need one timeout + continue.
            // Use HangTitles so only "Slow case" hangs; "Fast case" completes immediately.
            stub.HangTitles.Add("Slow case");
            var slow = new ApiTestDraft
            {
                SessionId = sid, Title = "Slow case", Preconditions = "", StepsJson = "[]",
                Status = ApiDraftStatus.Approved, Level = TestLevel.Ui, DraftKind = ApiDraftKind.UiCase,
                Bucket = "smoke", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var fast = new ApiTestDraft
            {
                SessionId = sid, Title = "Fast case", Preconditions = "", StepsJson = "[]",
                Status = ApiDraftStatus.Approved, Level = TestLevel.Ui, DraftKind = ApiDraftKind.UiCase,
                Bucket = "smoke", SortOrder = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.AddRange(slow, fast);
            await db.SaveChangesAsync();
            slowId = slow.Id;
            fastId = fast.Id;
        }

        await h.Orchestrator.GenerateUiScriptsAsync(sid);
        await WaitUiStatusAsync(h, sid, ApiUiStatus.Drafted, TimeSpan.FromSeconds(15));

        using var checkScope = h.Provider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<QaDbContext>();
        var slowDraft = await checkDb.ApiTestDrafts.AsNoTracking().FirstAsync(d => d.Id == slowId);
        var scripts = await checkDb.ApiTestDrafts
            .Where(d => d.SessionId == sid && d.DraftKind == ApiDraftKind.UiScript)
            .ToListAsync();

        Assert.Equal("Generation timed out", slowDraft.ErrorMessage);
        Assert.Contains(scripts, d => d.Title == "Fast case");
        Assert.DoesNotContain(scripts, d => d.Title == "Slow case");
        _ = fastId;
    }

    [Fact]
    public async Task GenerateUiScripts_passes_moduleRoute_from_atlas()
    {
        var stub = new StubPlaywrightAgentService(Path.GetTempPath());
        using var h = Harness.Create(new StubLlmClient(), playwright: stub);
        var sid = await h.SeedSessionAsync("messaging");
        var caseId = await h.SeedUiCaseDraftAsync(sid, "Open inbox", bucket: "smoke");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.AwaitingReview;
            sess.FrsSlug = "messaging";
            sess.KbContextJson = JsonSerializer.Serialize(new FrsModuleContext(
                Slug: "messaging",
                ModuleMarkdown: "# Messaging",
                AtlasMarkdown: "---\nslug: messaging\nroutes:\n  - /crm/messaging\n  - /crm/messaging/:id\n---\n# Atlas\n",
                FrontendPathIncludes: [],
                BackendPathIncludes: [],
                KeywordsExclusive: [],
                RulesMarkdown: "",
                FeBackLinksMarkdown: "",
                FeaturesJson: "[]"));
            var d = await db.ApiTestDrafts.FindAsync(caseId);
            d!.Status = ApiDraftStatus.Approved;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.GenerateUiScriptsAsync(sid);
        await WaitUiStatusAsync(h, sid, ApiUiStatus.Drafted, TimeSpan.FromSeconds(10));

        Assert.Equal("/crm/messaging", stub.LastModuleRoute);
    }

    [Fact]
    public async Task GenerateUiScripts_noop_when_UiStatus_not_AwaitingReview()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync("Deals");
        await h.SeedUiCaseDraftAsync(sid, "Verify login", bucket: "smoke");
        // UiStatus left null / not AwaitingReview

        await h.Orchestrator.GenerateUiScriptsAsync(sid);
        await Task.Delay(200);

        using var checkScope = h.Provider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<QaDbContext>();
        var sess = await checkDb.ApiTestSessions.AsNoTracking().FirstAsync(s => s.Id == sid);
        var scripts = await checkDb.ApiTestDrafts
            .Where(d => d.SessionId == sid && d.DraftKind == ApiDraftKind.UiScript)
            .ToListAsync();

        Assert.Null(sess.UiStatus);
        Assert.Empty(scripts);
    }

    private static async Task WaitUiStatusAsync(Harness h, int sid, ApiUiStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
            if (sess?.UiStatus == expected) return;
            await Task.Delay(40);
        }
    }

    [Fact]
    public async Task RunUiScripts_maps_JUnit_failures_to_draft_ErrorMessage_and_clears_passes()
    {
        var pwRoot = Path.Combine(Path.GetTempPath(), $"pw_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(pwRoot);
        var failRel = Path.Combine("generated", "ui", "smoke", "fail-case.ui.spec.ts");
        var passRel = Path.Combine("generated", "ui", "smoke", "pass-case.ui.spec.ts");
        var failAbs = Path.Combine(pwRoot, failRel);
        var passAbs = Path.Combine(pwRoot, passRel);
        Directory.CreateDirectory(Path.GetDirectoryName(failAbs)!);
        await File.WriteAllTextAsync(failAbs, "// fail");
        await File.WriteAllTextAsync(passAbs, "// pass");

        var runner = new StubPlaywrightRunner
        {
            Result = new PlaywrightRunResult
            {
                ExitCode = 1,
                Passed = 1,
                Failed = 1,
                FailedTests =
                [
                    new FailedTestResult
                    {
                        TestTitle = "fail case",
                        ErrorMessage = "Error: expect(locator).toBeVisible()",
                        SpecFilePath = failRel.Replace('\\', '/'),
                    },
                ],
            },
        };

        using var h = Harness.Create(
            new StubLlmClient(),
            qaLoop: new QaLoopOptions
            {
                ScriptsFolderName = "scripts",
                RunsFolderName = "runs",
                PlaywrightRoot = pwRoot,
            },
            playwrightRunner: runner);
        var sid = await h.SeedSessionAsync("Deals");

        int failId, passId;
        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.Drafted;
            sess.FrsSlug = "deals";
            var fail = new ApiTestDraft
            {
                SessionId = sid, Title = "Fail case", Preconditions = "", StepsJson = "[]",
                SpecCode = "//", SpecFilePath = failAbs, Status = ApiDraftStatus.Approved,
                Level = TestLevel.Ui, DraftKind = ApiDraftKind.UiScript, Bucket = "smoke",
                ErrorMessage = null, SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var pass = new ApiTestDraft
            {
                SessionId = sid, Title = "Pass case", Preconditions = "", StepsJson = "[]",
                SpecCode = "//", SpecFilePath = passAbs, Status = ApiDraftStatus.Approved,
                Level = TestLevel.Ui, DraftKind = ApiDraftKind.UiScript, Bucket = "smoke",
                ErrorMessage = "stale failure", SortOrder = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.AddRange(fail, pass);
            await db.SaveChangesAsync();
            failId = fail.Id;
            passId = pass.Id;
        }

        await h.Orchestrator.RunUiScriptsAsync(sid);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
            var fail = await db.ApiTestDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == failId);
            if (sess?.LastUiRunId is not null
                && sess.UiStatus == ApiUiStatus.Drafted
                && fail?.ErrorMessage is not null)
                break;
            await Task.Delay(40);
        }

        using var check = h.Provider.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<QaDbContext>();
        var failDraft = await cdb.ApiTestDrafts.AsNoTracking().FirstAsync(d => d.Id == failId);
        var passDraft = await cdb.ApiTestDrafts.AsNoTracking().FirstAsync(d => d.Id == passId);
        var sessOut = await cdb.ApiTestSessions.AsNoTracking().FirstAsync(s => s.Id == sid);

        Assert.Equal("Error: expect(locator).toBeVisible()", failDraft.ErrorMessage);
        Assert.Null(passDraft.ErrorMessage);
        Assert.Equal(ApiUiStatus.Drafted, sessOut.UiStatus);
        Assert.NotNull(sessOut.LastUiRunId);
        // Auto-heal re-runs failed specs up to 3 times when they keep failing.
        Assert.Equal(sid * 10_000 + 3, sessOut.LastUiRunId);
        Assert.Equal(3, runner.RunCount);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal("deals", runner.LastSpec!.Module);
        Assert.True(runner.LastSpec.EnableCapture);
        Assert.True(runner.LastSpec.IsUiRun);
        Assert.False(runner.LastSpec.Headed);
    }

    [Fact]
    public async Task RunUiScripts_noop_when_already_RunningUiScripts()
    {
        var runner = new StubPlaywrightRunner();
        using var h = Harness.Create(new StubLlmClient(), playwrightRunner: runner);
        var sid = await h.SeedSessionAsync("Deals");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var sess = await db.ApiTestSessions.FindAsync(sid);
            sess!.UiStatus = ApiUiStatus.RunningUiScripts;
            sess.LastUiRunId = 42;
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.RunUiScriptsAsync(sid);
        await Task.Delay(300);

        using var check = h.Provider.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<QaDbContext>();
        var sessOut = await cdb.ApiTestSessions.AsNoTracking().FirstAsync(s => s.Id == sid);

        Assert.Equal(ApiUiStatus.RunningUiScripts, sessOut.UiStatus);
        Assert.Equal(42, sessOut.LastUiRunId);
        Assert.Null(runner.LastSpec);
    }

    [Fact]
    public async Task HealSpecs_resets_status_to_Draft()
    {
        using var h = Harness.Create(new StubLlmClient());
        var sid = await h.SeedSessionAsync("Deals");
        var (draftId, _) = await h.SeedUiScriptDraftWithFileAsync(sid, "Verify dashboard");

        using (var scope = h.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var d = await db.ApiTestDrafts.FindAsync(draftId);
            d!.Status = ApiDraftStatus.Rejected;
            d.ErrorMessage = "Selector not found";
            await db.SaveChangesAsync();
        }

        await h.Orchestrator.HealSpecsAsync(sid, [draftId], userContext: "Prefer role=button name=Save");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(40);
            using var scope = h.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var d = await db.ApiTestDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId);
            if (d?.Status == ApiDraftStatus.Draft) break;
        }

        using var checkScope = h.Provider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<QaDbContext>();
        var healed = await checkDb.ApiTestDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId);

        Assert.Equal(ApiDraftStatus.Draft, healed!.Status);
        Assert.Null(healed.ErrorMessage);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required QaLoopOrchestrator Orchestrator { get; init; }
        public required QaLoopAdoStub Ado { get; init; }
        private PostgresTestDatabase Pg { get; init; } = null!;
        public string DataDir { get; init; } = "";

        public static Harness Create(
            StubLlmClient llm,
            IPlaywrightAgentService? playwright = null,
            QaLoopOptions? qaLoop = null,
            IPlaywrightRunner? playwrightRunner = null)
        {
            var pg = PostgresTestDatabase.Create("qa_loop");
            var dataDir = Path.Combine(Path.GetTempPath(), $"qa_loop_data_{Guid.NewGuid():N}");

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddDbContext<QaDbContext>(o => pg.Configure(o));
            services.AddScoped<DatabaseInitializer>();
            services.AddSingleton<ITextSearchProvider, PostgresFtsSearch>();
            services.AddSingleton<IPlaywrightAgentService>(
                playwright ?? new StubPlaywrightAgentService(dataDir));
            // KBG-107: null stubs so the orchestrator degrades gracefully in tests (no FRS KB)
            services.AddScoped<IFrsKbService, NullFrsKbService>();
            services.AddScoped<IQaLoopCodeScanService, NullCodeScanService>();
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().Initialize();

            var ado = new QaLoopAdoStub();
            var opts = qaLoop ?? new QaLoopOptions { ScriptsFolderName = "scripts", RunsFolderName = "runs" };
            // Ensure scripts land under this harness dataDir when caller only overrides timeout.
            if (string.IsNullOrEmpty(opts.ScriptsFolderName)) opts.ScriptsFolderName = "scripts";
            if (string.IsNullOrEmpty(opts.RunsFolderName)) opts.RunsFolderName = "runs";

            var orchestrator = new QaLoopOrchestrator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EventBus(),
                new PipelineConcurrency(),
                new CancellationRegistry(),
                ado,
                new StubLlmClientFactory(llm),
                playwrightRunner ?? new StubPlaywrightRunner(),
                new ScriptPathAdapter(new StaticOptionsMonitor<QaLoopOptions>(opts)),
                Options.Create(new StorageOptions { DataDirectory = dataDir, KnowledgebaseFolderName = "kb" }),
                Options.Create(opts),
                NullLogger<QaLoopOrchestrator>.Instance);

            return new Harness { Provider = provider, Orchestrator = orchestrator, Ado = ado, Pg = pg, DataDir = dataDir };
        }

        public async Task<int> SeedSessionAsync(
            string module, bool autoHandoff = false, int? workItemId = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var session = new ApiTestSession
            {
                ProjectId = 1,
                Module = module,
                AutoHandoff = autoHandoff,
                WorkItemId = workItemId,
                Status = ApiSessionStatus.Analyzing,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        }

        public async Task SetInventoryAsync(int sessionId, string analyzerJson)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var session = await db.ApiTestSessions.FindAsync(sessionId);
            if (session is null) return;
            // Parse only the inventory and surface sub-documents — avoid depending on ModuleAnalysis record.
            var doc = System.Text.Json.JsonDocument.Parse(analyzerJson);
            session.InventoryJson = doc.RootElement.GetProperty("inventory").GetRawText();
            session.AnalysisJson  = doc.RootElement.GetProperty("surfaceMatrix").GetRawText();
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        public async Task<int> SeedUiCaseDraftAsync(int sessionId, string title, string bucket)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"open app\",\"expected\":\"dashboard visible\"}]",
                Status = ApiDraftStatus.Draft,
                Level = TestLevel.Ui,
                DraftKind = ApiDraftKind.UiCase,
                Bucket = bucket,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        }

        public async Task<(int DraftId, string SpecPath)> SeedUiScriptDraftWithFileAsync(int sessionId, string title)
        {
            var dir = Path.Combine(DataDir, "generated", "ui", "smoke");
            Directory.CreateDirectory(dir);
            var specPath = Path.Combine(dir, $"test-{Guid.NewGuid():N}.ui.spec.ts");
            await File.WriteAllTextAsync(specPath, "// original spec");

            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"open app\",\"expected\":\"dashboard visible\"}]",
                Status = ApiDraftStatus.Approved,
                Level = TestLevel.Ui,
                DraftKind = ApiDraftKind.UiScript,
                Bucket = "smoke",
                SpecCode = "// original spec",
                SpecFilePath = specPath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return (draft.Id, specPath);
        }

        public async Task<int> SeedUiScriptDraftAsync(int sessionId, string title, string bucket)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"open app\",\"expected\":\"dashboard visible\"}]",
                Status = ApiDraftStatus.Approved,
                Level = TestLevel.Ui,
                DraftKind = ApiDraftKind.UiScript,
                Bucket = bucket,
                SpecFilePath = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        }

        public async Task<int> SeedDraftAsync(
            int sessionId, string title, ApiDraftStatus status, int? adoId = null,
            int? caseIndex = null, string? specFilePath = null, string? sourceRoutes = null)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var draft = new ApiTestDraft
            {
                SessionId = sessionId,
                Title = title,
                Preconditions = "",
                StepsJson = "[{\"action\":\"do x\",\"expected\":\"see y\"}]",
                Status = status,
                AdoTestCaseId = adoId,
                Level = TestLevel.Contract,
                CaseIndex = caseIndex,
                SpecFilePath = specFilePath,
                SourceRoutesJson = sourceRoutes is null ? null : System.Text.Json.JsonSerializer.Serialize(new[] { sourceRoutes }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ApiTestDrafts.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        }

        public async Task<ApiTestSession> WaitForTerminalAsync(
            int sessionId, ApiSessionStatus? expected = null)
        {
            ApiSessionStatus[] terminal =
            [
                ApiSessionStatus.Done, ApiSessionStatus.Drafted, ApiSessionStatus.AwaitingReview,
                ApiSessionStatus.Failed, ApiSessionStatus.Cancelled,
            ];
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = Provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                var session = await db.ApiTestSessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == sessionId);
                if (session is not null && (expected is { } e
                        ? session.Status == e : terminal.Contains(session.Status)))
                    return session;
                await Task.Delay(40);
            }
            throw new TimeoutException($"Session {sessionId} did not reach terminal status in time.");
        }

        public void Dispose()
        {
            Provider.Dispose();
            Pg.Dispose();
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }

    private sealed class StubLlmClientFactory : ILlmClientFactory
    {
        private readonly ILlmClient _client;
        public StubLlmClientFactory(ILlmClient client) => _client = client;
        public Task<ILlmClient> CreateAsync(CancellationToken ct = default) => Task.FromResult(_client);
    }

    private sealed class StubPlaywrightRunner : IPlaywrightRunner
    {
        public PlaywrightRunResult Result { get; set; } = new() { ExitCode = 0, Passed = 1 };
        public PlaywrightRunSpec? LastSpec { get; private set; }
        public int RunCount { get; private set; }

        public Task<PlaywrightRunResult> RunAsync(PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
        {
            LastSpec = spec;
            RunCount++;
            return Task.FromResult(Result);
        }

        public Task<string?> RebuildReportAsync(PlaywrightRunSpec spec, string eventKey, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void DeleteRunArtifacts(int runId) { }

        public string GetHistoryFilePath(string? module, int? pipelineId) =>
            Path.Combine(Path.GetTempPath(), "allure-history-stub", module ?? "_all",
                pipelineId?.ToString() ?? "manual", "history.jsonl");
    }

    private sealed class StubPlaywrightAgentService : IPlaywrightAgentService
    {
        private readonly string _dataDir;
        private readonly int _delayMs;
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public int CallCount { get; private set; }
        public string? LastModuleRoute { get; private set; }
        public HashSet<string> HangTitles { get; } = new(StringComparer.Ordinal);

        public StubPlaywrightAgentService(string dataDir, int delayMs = 0)
        {
            _dataDir = dataDir;
            _delayMs = delayMs;
        }

        public async Task<string> GenerateScriptAsync(
            string caseTitle, string stepsJson, string bucket, int tcIdPending,
            IProgress<string>? progress, CancellationToken ct, string? moduleRoute = null)
        {
            LastModuleRoute = moduleRoute;
            CallCount++;
            var current = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            try
            {
                if (HangTitles.Contains(caseTitle) || (_delayMs > 0 && HangTitles.Count == 0))
                {
                    // Hang until cancelled (timeout test) or delay (concurrency test).
                    if (HangTitles.Contains(caseTitle))
                        await Task.Delay(Timeout.Infinite, ct);
                    else
                        await Task.Delay(_delayMs, ct);
                }

                var dir = Path.Combine(_dataDir, "generated", "ui", bucket);
                Directory.CreateDirectory(dir);
                var slug = new string(caseTitle.ToLowerInvariant()
                    .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                var path = Path.Combine(dir, $"{slug}-{tcIdPending}.ui.spec.ts");
                await File.WriteAllTextAsync(path, $"// stub spec for {caseTitle}", ct);
                return path;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public async Task HealAsync(
            string specFilePath, string failureMessage,
            IProgress<string>? progress, CancellationToken ct,
            string? userContext = null)
        {
            LastUserContext = userContext;
            var existing = File.Exists(specFilePath)
                ? await File.ReadAllTextAsync(specFilePath, ct)
                : "";
            await File.WriteAllTextAsync(specFilePath, existing + "\n// healed", ct);
        }

        public string? LastUserContext { get; private set; }
    }

    /// <summary>Stub ADO client for QA Loop push tests: records write calls; reads throw unless
    /// configured (GetWorkItemAsync — QAL-070 — returns WorkItemToReturn, or throws if configured to).</summary>
    internal sealed class QaLoopAdoStub : IAzureDevOpsClient
    {
        private int _nextTcId = 7000;
        public List<string> CreatedTestCases { get; } = [];
        public HashSet<string> FailCreateTitles { get; } = new(StringComparer.Ordinal);
        public int TransientFailuresRemaining { get; set; }
        public List<(int PlanId, int SuiteId, int TcId)> SuiteAdds { get; } = [];
        public List<(int StoryId, int TcId)> TestedByLinks { get; } = [];
        public AdoWorkItem? WorkItemToReturn { get; set; }
        public bool ThrowOnGetWorkItem { get; set; }

        public Task<int> CreateTestCaseAsync(
            string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default)
        {
            if (TransientFailuresRemaining > 0)
            {
                TransientFailuresRemaining--;
                throw new HttpRequestException("simulated transient push failure");
            }
            if (FailCreateTitles.Contains(title))
                throw new InvalidOperationException("simulated permanent push failure");

            CreatedTestCases.Add(title);
            return Task.FromResult(_nextTcId++);
        }

        public List<(int Id, string Title)> UpdatedTestCases { get; } = [];
        public Task UpdateTestCaseAsync(int testCaseId, string title, string preconditions, IReadOnlyList<TestCaseStep> steps, CancellationToken ct = default)
        {
            UpdatedTestCases.Add((testCaseId, title));
            return Task.CompletedTask;
        }

        public Task AddTestCaseToSuiteAsync(int planId, int suiteId, int tcId, CancellationToken ct = default)
        {
            SuiteAdds.Add((planId, suiteId, tcId));
            return Task.CompletedTask;
        }

        public int NextSuiteId = 900_000;
        public List<(int PlanId, int ParentId, string Name)> CreatedSuites { get; } = [];
        public Task<int> CreateStaticTestSuiteAsync(int planId, int parentSuiteId, string name, CancellationToken ct = default)
        {
            CreatedSuites.Add((planId, parentSuiteId, name));
            return Task.FromResult(NextSuiteId++);
        }

        public Task LinkTestedByAsync(int storyId, int tcId, CancellationToken ct = default)
        {
            TestedByLinks.Add((storyId, tcId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LinkedTestCase>> GetSuiteTestCasesAsync(int planId, int suiteId, bool recurse = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);

        public Task<AdoWorkItem> GetWorkItemAsync(int workItemId, bool forceRefresh = false, CancellationToken ct = default) =>
            ThrowOnGetWorkItem
                ? throw new InvalidOperationException("simulated ADO failure")
                : Task.FromResult(WorkItemToReturn ?? throw new NotImplementedException());

        // ── unused ──
        public Task<IReadOnlyList<string>> DiscoverOrganizationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> DiscoverProjectsAsync(string organization, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverAreaPathsAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverWorkItemTypesAsync(string organization, string project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> DiscoverBranchesAsync(string? repository = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoWorkItem>> SearchWorkItemsAsync(string query, int top = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedTestCase>> GetLinkedTestCasesAsync(int storyId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LinkedTestCase>>([]);
        public Task<(IReadOnlyList<AdoWorkItem> Items, bool Truncated)> GetChildWorkItemsAsync(int parentId, bool recursive = true, int maxDepth = 5, int maxNodes = 500, int maxLeaves = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestPlan>> GetTestPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoTestSuite>> GetTestSuitesAsync(int planId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AdoTestSuite>>([]);
        public Task<IReadOnlyList<ReportTicket>> GetTicketsInRangeAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> boards, IReadOnlyList<string>? types = null, IReadOnlyList<string>? dateFields = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetWorkItemCommentsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TimelineEvent>> GetWorkItemUpdatesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReportTicket>> EnrichTicketsForReportAsync(IReadOnlyList<ReportTicket> tickets, int limit = 150, int concurrency = 8, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateBugAsync(string title, string severity, string preconditions, IReadOnlyList<string> reproSteps, string expected, string actual, string suggestedFix, string systemInfo = "", string reproHtml = "", string foundWith = "Manual Test", CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkRelatedAsync(int fromId, int toId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LinkParentAsync(int childId, int parentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> UploadAttachmentAsync(string fileName, byte[] content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddWorkItemAttachmentLinkAsync(int workItemId, string attachmentUrl, string? comment = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoNamedRef>> ListRepositoriesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ResolveRepositoryIdAsync(string? name = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitRefs> GetWorkItemGitRefsAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LinkedChange>> GetLinkedChangesAsync(int workItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetCommitChangedFilesAsync(string sha, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetPullRequestDiffAsync(string prId, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChangedFile>> GetCommitDiffAsync(string sha, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetCommitsForPathAsync(string path, int top = 5, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetFileContentAsync(string path, string? @ref = null, string? repoId = null, string versionType = "branch", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListRepoFilePathsAsync(string scopePath, string? branch = null, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> SearchCodeAsync(string text, int top = 20, string? repoId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAdoTestRunAsync(int planId, int suiteId, string name, IReadOnlyList<int> pointIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetAdoTestResultIdsAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAdoTestResultsAsync(int adoTestRunId, IReadOnlyList<AdoTestResultUpdate> updates, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteAdoTestRunAsync(int adoTestRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<int, int>> GetTestPointsAsync(int planId, int suiteId, IReadOnlyList<int> testCaseIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuild>> GetPipelineBuildsAsync(int pipelineId, int? minBuildId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AdoBuildChange>> GetBuildChangesAsync(int buildId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    // ── KBG-107: null stubs for new services (tests degrade gracefully without FRS) ─────────────
    private sealed class NullFrsKbService : IFrsKbService
    {
        public Task<FrsModuleContext?> ResolveModuleAsync(string moduleName, int projectId, CancellationToken ct = default)
            => Task.FromResult<FrsModuleContext?>(null);
        public Task<IReadOnlyList<string>> ListSlugsAsync(int projectId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NullCodeScanService : IQaLoopCodeScanService
    {
        public Task<CodeScanBundle> ScanAsync(
            string module, string repository, string branch,
            FrsModuleContext? frsContext, string sessionKey, CancellationToken ct = default)
            => Task.FromResult(new CodeScanBundle([], [], [], "none"));
    }
}
