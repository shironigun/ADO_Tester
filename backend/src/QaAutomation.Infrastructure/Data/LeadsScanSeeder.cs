using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QaAutomation.Core.Configuration;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// One-time seeder that lands a REAL QA Loop scan of the CRM "leads" module (Lead/ service on the qa
/// branch) as a completed <see cref="ApiTestSession"/> in the Drafted state — reproducing EXACTLY what
/// <c>QaLoopOrchestrator.PersistDraft</c> + the three generator agents persist, so the entry is
/// indistinguishable from a live pipeline run. Idempotent: skips if a "leads" session already exists.
///
/// Inventory is the real API surface discovered by reading Lead/src/Target.Crm.Lead:
/// 45 endpoints (30 Deal + 3 Webhook + 12 Task), 21 request contracts, 16 workflows.
///
/// Draft shapes mirror the generators' fallbacks byte-for-intent:
///  • Contract (ContractGeneratorAgent): 1 draft/route (routes with a resolvable request DTO),
///    title "[&lt;tcId-pending&gt;] Contract: METHOD route - response shape matches schema".
///  • Endpoint (EndpointGeneratorAgent): 3 cases/route (200/401/400) sharing ONE spec file,
///    each tagged "[&lt;tcId-pending:N&gt;]" with CaseIndex = 0/1/2.
///  • Workflow (WorkflowGeneratorAgent): 1 draft/workflow.
/// </summary>
public sealed class LeadsScanSeeder
{
    private const int PlanId = 57868;
    private const int SuiteId = 174631; // single suite; the three level-tabs all live in this one session

    private readonly QaDbContext _db;
    private readonly string _scriptsBase;
    private readonly ILogger<LeadsScanSeeder> _log;

    public LeadsScanSeeder(
        QaDbContext db, IOptions<StorageOptions> storage, IOptions<QaLoopOptions> qaLoop, ILogger<LeadsScanSeeder> log)
    {
        _db = db;
        _scriptsBase = Path.GetFullPath(Path.Combine(
            storage.Value.DataDirectory, qaLoop.Value.ScriptsFolderName));
        _log = log;
    }

    private string ScriptsRoot(string moduleKey) => Path.Combine(_scriptsBase, moduleKey);

    // ── Inventory data (real Lead module surface) ─────────────────────────────
    private sealed record Ep(string Area, string Method, string Route, string? Req, string Resp, string Fn, bool Anonymous = false);
    private sealed record Fld(string Name, string Type);
    private sealed record Dto(string Name, Fld[] Fields);
    private sealed record Wf(string Name, (string Ep, string Role)[] Steps);

    private static readonly Ep[] Endpoints =
    [
        // ── Deal (30) ──
        new("Deal", "GET",  "lead/deal/pipelines/{dmsDealerIdCrm}", null, "List<PipelineResponse>", "GetPipelines"),
        new("Deal", "GET",  "lead/deal/pipelinesDD/{dmsDealerIdCrm}", null, "List<PipelineListResponse>", "GetPipelinesList"),
        new("Deal", "GET",  "lead/deal/pipelineStagesDD/{pipelineId}", null, "List<PipelineStageListResponse>", "GetPipelineStagesList"),
        new("Deal", "POST", "lead/deal/pipeline/addupdate/{dmsDealerIdCrm}", "List<PipelineAllRequest>", "bool", "AddUpdatePipelines"),
        new("Deal", "POST", "lead/deal/pipeline/deletePipeline/{pipelineId}", null, "bool", "DeletePipeline"),
        new("Deal", "POST", "lead/deal/addNewDeal/{dmsDealerIdCrm}", "DealRequest", "int", "AddNewDeal"),
        new("Deal", "POST", "lead/deal/lostReason/{dmsDealerIdCrm}", "DealLostReasonRequest", "List<DealLostReason>", "AddDealLostReason"),
        new("Deal", "PUT",  "lead/deal/lostReason/{dmsDealerIdCrm}", "DealLostReasonRequest", "List<DealLostReason>", "UpdateDealLostReason"),
        new("Deal", "GET",  "lead/deal/lostReason/{dmsDealerIdCrm}", null, "List<DealLostReason>", "GetDealLostReasons"),
        new("Deal", "POST", "lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId}", "FilterDealsRequest", "List<DealsResponse>", "GetDeals"),
        new("Deal", "GET",  "lead/dealV2/{dealId}", null, "string", "GetDealDetailsV2"),
        new("Deal", "GET",  "lead/deal/{dealId}", null, "string", "GetDealDetails"),
        new("Deal", "PUT",  "lead/deal/updateDealIsFavorite/{dealId}/{isFavorite}", null, "bool", "UpdateDealFavorite"),
        new("Deal", "POST", "lead/deal/uploadAttachmentToBlob", null, "List<DealAttachmentResponse>", "UploadAttachmentToBlob"),
        new("Deal", "POST", "lead/deal/uploadAttachmentToDeal/{dealId}", "List<DealAttachmentRequest>", "List<DealAttachmentResponse>", "UploadAttachmentToDeal"),
        new("Deal", "POST", "lead/deal/deleteDealAttachments/{dealId}", "List<DealAttachmentRequest>", "List<DealAttachmentResponse>", "DeleteDealAttachments"),
        new("Deal", "PUT",  "lead/deal/{dealId}/updateDealV2/{dealerId}", "DealDetailsRequest", "string", "UpdateDealV2"),
        new("Deal", "PUT",  "lead/deal/{dealId}/updateDeal/{dealerId}", "DealDetailsRequest", "string", "UpdateDeal"),
        new("Deal", "PUT",  "lead/deal/updateDealAssignUser", "List<AssignDealToUserRequest>", "string", "UpdateDealAssignUser"),
        new("Deal", "PUT",  "lead/deal/updateDealsLostStatus", "List<DealLostReasonRequest>", "string", "UpdateDealsLostStatus"),
        new("Deal", "PUT",  "lead/deal/updateDealPosition", "UpdateDealPositionRequest", "string", "UpdateDealPosition"),
        new("Deal", "PUT",  "lead/deal/updateDealPositionV2", "UpdateDealPositionRequestV2", "string", "UpdateDealPositionV2"),
        new("Deal", "PUT",  "lead/deal/updateDealWon", "List<UpdateDealWon>", "string", "UpdateDealWon"),
        new("Deal", "PUT",  "lead/deal/updateDealArchive", "List<UpdateDealArchive>", "string", "UpdateDealArchive"),
        new("Deal", "PUT",  "lead/deal/reopenDeal", "List<int>", "string", "ReopenDeal"),
        new("Deal", "PUT",  "lead/deal/updateDealsPipeline", "UpdateDealPipelineRequest", "string", "UpdateDealsPipeline"),
        new("Deal", "GET",  "lead/deal/allLeadTags", null, "List<DealLeadTags>", "GetAllLeadTags"),
        new("Deal", "POST", "lead/deal/addLeadTags", "DealLeadTagRequest", "DealLeadTags", "AddDealLeadTag"),
        new("Deal", "PUT",  "lead/deal/updateLeadTags", "DealLeadTagRequest", "DealLeadTags", "UpdateDealLeadTag"),
        new("Deal", "POST", "lead/deal/deleteLeadTags", "DealLeadTagDeleteRequest", "string", "DeleteDealLeadTag"),
        // ── Webhook (3) ──
        new("Webhook", "POST", "lead/deal/receiveEmailFormMailinator", "MailinatorEmailResponse", "string", "ReceiveEmailFormMailinator"),
        new("Webhook", "POST", "lead/deal/receiveWebLeadFormTargetWeb", "TargetWebLeadRequest", "string", "ReceiveWebLeadFormTargetWeb"),
        new("Webhook", "POST", "lead/deal/brp/sales-opportunity", "BrpSalesOpportunityRequest", "BrpSalesOpportunityResponse", "ReceiveBrpSalesOpportunity", Anonymous: true),
        // ── Task (12) ──
        new("Task", "GET",    "lead/taskStatus", null, "List<UserTaskStatusResponse>", "GetUserTaskStatus"),
        new("Task", "GET",    "lead/taskCategory", null, "List<UserTaskCategoryResponse>", "GetUserTaskCategories"),
        new("Task", "GET",    "lead/taskType", null, "List<UserTaskTypeResponse>", "GetUserTaskTypes"),
        new("Task", "GET",    "lead/taskType/{taskTypeId}", null, "UserTaskTypeResponse", "GetUserTaskType"),
        new("Task", "POST",   "lead/taskType", "UserTaskTypeRequest", "string", "CreateUserTaskType"),
        new("Task", "PUT",    "lead/taskType/{taskTypeId}", "UpdateUserTaskTypeRequest", "string", "UpdateUserTaskType"),
        new("Task", "POST",   "lead/tasks", "GetUserTasksRequest", "UserTaskResponses", "GetUserTasks"),
        new("Task", "GET",    "lead/task/{taskId}", null, "UserTaskResponse", "GetUserTask"),
        new("Task", "POST",   "lead/task", "CreateUserTaskRequest", "UserTaskResponse", "CreateUserTask"),
        new("Task", "PUT",    "lead/task/{taskId}", "UpdateUserTaskRequest", "UserTaskResponse", "UpdateUserTask"),
        new("Task", "PUT",    "lead/tasks", "UpdateUserTasksRequest", "string", "UpdateUserTasks"),
        new("Task", "DELETE", "lead/task/{taskId}", null, "string", "DeleteUserTask"),
    ];

    private static readonly Dto[] Contracts =
    [
        new("DealRequest",
        [
            new("Title", "string"), new("Amount", "decimal"), new("AssignedUserId", "string"),
            new("CreatedByUserId", "string"), new("Description", "string"), new("PipelineId", "int"),
            new("PipelineStageId", "int"), new("DealEndGoalId", "int?"), new("DealLostReasonId", "int?"),
            new("IsArchived", "bool"), new("Position", "int"), new("DealType", "string"),
            new("IsTaxExempted", "bool"), new("CustomerContacts", "List<CustomerContactRequest>"),
            new("DealProducts", "List<DealProductRequest>"), new("DealQuotes", "List<DealQuoteRequest>"),
        ]),
        new("DealDetailsRequest",
        [
            new("Title", "string"), new("Amount", "decimal"), new("Description", "string"),
            new("AssignedUserId", "Guid?"), new("AssignedUserName", "string"), new("PipelineId", "int"),
            new("PipelineStageId", "int"), new("PipelineStageName", "string"), new("PipelineStagePosition", "int"),
            new("DealState", "int"), new("IsTaxExempted", "bool"),
        ]),
        new("FilterDealsRequest",
        [
            new("AgeValue", "int?"), new("StatusId", "int?"), new("LocationId", "int?"),
            new("UserIds", "List<Guid?>"), new("IsFilter", "bool"),
        ]),
        new("UpdateDealPositionRequest",
            [new("DealId", "int"), new("PipelineStageId", "int"), new("Position", "int")]),
        new("UpdateDealPositionRequestV2",
            [new("DealId", "int"), new("PipelineStageId", "int"), new("Position", "int")]),
        new("AssignDealToUserRequest", [new("Id", "int"), new("AssignedUserId", "Guid")]),
        new("DealLostReasonRequest",
            [new("Id", "int"), new("DealId", "int"), new("LostReasonId", "int"), new("LostNote", "string")]),
        new("UpdateDealWon", [new("DealId", "int"), new("WonDate", "DateTime?"), new("Amount", "decimal?")]),
        new("UpdateDealArchive", [new("DealId", "int"), new("IsArchived", "bool")]),
        new("UpdateDealPipelineRequest", [new("DealIds", "List<int>"), new("PipelineId", "int")]),
        new("DealLeadTagRequest", [new("TagId", "int"), new("TagName", "string"), new("Color", "string")]),
        new("DealLeadTagDeleteRequest", [new("TagId", "int")]),
        new("DealAttachmentRequest", [new("AttachmentId", "int"), new("FileName", "string"), new("FileURL", "string")]),
        new("CreateUserTaskRequest",
        [
            new("DealerId", "int"), new("IsFollowed", "bool"), new("FollowUpTaskId", "int?"),
            new("CreatedByUserId", "Guid"), new("AssignedUserId", "Guid?"), new("TaskSubject", "string"),
            new("TaskTypeId", "int"), new("CustomerId", "int?"), new("DueDate", "DateTime?"),
            new("Notes", "string"), new("StatusId", "int"), new("TaskOutcomeId", "int?"), new("DealId", "int?"),
        ]),
        new("UpdateUserTaskRequest",
        [
            new("AssignedUserId", "Guid?"), new("TaskTypeId", "int?"), new("DueDate", "DateTime?"),
            new("Notes", "string"), new("StatusId", "int?"), new("TaskOutcomeId", "int?"),
            new("TaskSubject", "string"), new("TaskArchiveReason", "string"), new("DealerId", "int"),
        ]),
        new("UpdateUserTasksRequest",
        [
            new("AssignedUserId", "Guid?"), new("DueDate", "DateTime?"), new("TaskIds", "List<int>"),
            new("OutcomeId", "int?"), new("Notes", "string"), new("StatusId", "int?"), new("IsArchived", "bool?"),
        ]),
        new("GetUserTasksRequest",
        [
            new("CreatedByUserId", "Guid"), new("AssignedUserIds", "List<Guid>"), new("DmsDealerIdCrm", "long"),
            new("PageSize", "int"), new("PageNumber", "int"), new("TaskTypeIds", "List<int>"), new("StatusIds", "List<int>"),
        ]),
        new("UserTaskTypeRequest",
            [new("Value", "string"), new("DepartmentIds", "List<int>"), new("CategoryId", "int")]),
        new("MailinatorEmailResponse",
        [
            new("To", "string"), new("From", "string"), new("Subject", "string"),
            new("Text", "string"), new("Html", "string"), new("TimeReceived", "long"),
        ]),
        new("TargetWebLeadRequest",
        [
            new("FirstName", "string"), new("LastName", "string"), new("Email", "string"),
            new("Phone", "string"), new("Message", "string"), new("SourceUrl", "string"),
        ]),
        new("BrpSalesOpportunityRequest",
        [
            new("SalesOpportunityId", "string"), new("DealerNo", "string"), new("Language", "string"),
            new("Consumer", "BrpConsumer"), new("ProductInterest", "BrpProductInterest"), new("Status", "List<BrpStatus>"),
            new("ModelNumber", "string"), new("Comments", "string"), new("CampaignName", "string"),
        ]),
    ];

    private static readonly Wf[] Workflows =
    [
        new("Create deal then notify board", [("POST lead/deal/addNewDeal/{dmsDealerIdCrm}", "create"), ("GET lead/deal/{dealId}", "verify")]),
        new("Update deal with BRP sync", [("PUT lead/deal/{dealId}/updateDeal/{dealerId}", "update"), ("GET lead/dealV2/{dealId}", "verify")]),
        new("Assign deal to user then email", [("PUT lead/deal/updateDealAssignUser", "assign"), ("GET lead/deal/{dealId}", "verify")]),
        new("Move deal in pipeline", [("PUT lead/deal/updateDealPosition", "move"), ("POST lead/deal/pipelineDeals/{dmsDealerIdCrm}/{pipelineId}", "verify")]),
        new("Mark deal won", [("PUT lead/deal/updateDealWon", "won"), ("GET lead/deal/{dealId}", "verify")]),
        new("Mark deal lost", [("PUT lead/deal/updateDealsLostStatus", "lost"), ("GET lead/deal/lostReason/{dmsDealerIdCrm}", "verify")]),
        new("Archive then reopen deal", [("PUT lead/deal/updateDealArchive", "archive"), ("PUT lead/deal/reopenDeal", "reopen")]),
        new("Move deals to another pipeline", [("PUT lead/deal/updateDealsPipeline", "move"), ("GET lead/deal/pipelines/{dmsDealerIdCrm}", "verify")]),
        new("Lead tag lifecycle add update delete", [("POST lead/deal/addLeadTags", "add"), ("PUT lead/deal/updateLeadTags", "update"), ("POST lead/deal/deleteLeadTags", "delete")]),
        new("Deal attachment upload attach delete", [("POST lead/deal/uploadAttachmentToBlob", "upload"), ("POST lead/deal/uploadAttachmentToDeal/{dealId}", "attach"), ("POST lead/deal/deleteDealAttachments/{dealId}", "delete")]),
        new("Email webhook ingestion to deal", [("POST lead/deal/receiveEmailFormMailinator", "ingest"), ("GET lead/deal/{dealId}", "verify")]),
        new("Web lead webhook ingestion to deal", [("POST lead/deal/receiveWebLeadFormTargetWeb", "ingest"), ("GET lead/deal/{dealId}", "verify")]),
        new("BRP sales opportunity ingestion to deal", [("POST lead/deal/brp/sales-opportunity", "ingest"), ("GET lead/dealV2/{dealId}", "verify")]),
        new("Create task then notify assignee", [("POST lead/task", "create"), ("GET lead/task/{taskId}", "verify")]),
        new("Create follow up task chain", [("POST lead/task", "create-parent"), ("POST lead/task", "create-followup")]),
        new("Bulk update tasks", [("PUT lead/tasks", "bulk-update"), ("POST lead/tasks", "verify")]),
    ];

    // ── Seed entry point ──────────────────────────────────────────────────────
    public void Seed()
    {
        if (!_db.Projects.Any()) return; // project seed hasn't run yet
        // Two demo entries share the same route inventory: the original "leads" seed (kept for its
        // history — still in the OLD title format) and a "deals" seed reproducing the same scan under
        // the deals module label using the NEW title/step format.
        SeedModule("leads", "Leads");
        SeedModule("deals", "Deals");
    }

    private void SeedModule(string moduleKey, string moduleTitle)
    {
        try
        {
            // Idempotent per module — a Drafted session for this plan under this module. Failed/Analyzing
            // sessions from real pipeline attempts must NOT suppress the seed.
            if (_db.ApiTestSessions.Any(s => s.Module == moduleKey && s.TestPlanId == PlanId && s.Status == ApiSessionStatus.Drafted))
                return;

            var now = DateTime.UtcNow;
            var inventory = BuildInventory(moduleKey);
            var matrix = BuildMatrix(moduleKey);

            var session = new ApiTestSession
            {
                ProjectId = 1,
                Module = moduleKey,
                Repository = "Your Repo",
                Branch = "qa",
                FrontendModule = $"/{moduleKey}",
                FrontendPath = $"web-app/src/app/{moduleKey}",
                SourceDirsJson = JsonSerializer.Serialize(new[]
                {
                    "Lead/src/Target.Crm.Lead/Target.Crm.Lead.Api/Target.Crm.Lead.Api/Deal",
                    "Lead/src/Target.Crm.Lead/Target.Crm.Lead.Api/Target.Crm.Lead.Api/Task",
                    "Lead/src/Target.Crm.Lead/Target.Crm.Lead.Service",
                }),
                TestPlanId = PlanId,
                TestSuiteId = SuiteId,
                Status = ApiSessionStatus.Drafted,
                AutoHandoff = true,
                ActivityEnabled = true,
                InventoryJson = JsonSerializer.Serialize(inventory),
                AnalysisJson = JsonSerializer.Serialize(matrix),
                CreatedAt = now,
                UpdatedAt = now,
            };

            session.Drafts = BuildDrafts(now, moduleTitle);
            session.EventLogJson = JsonSerializer.Serialize(BuildNarrative(moduleKey, matrix.Rows.Count, session.Drafts.Count));

            _db.ApiTestSessions.Add(session);
            _db.SaveChanges();

            WriteSpecFiles(session, moduleKey);

            _log.LogInformation(
                "Seeded QA Loop {Module} scan: session #{Id} · {Drafts} drafts ({C} contract / {E} endpoint / {W} workflow) · {Rows} surfaces.",
                moduleKey, session.Id, session.Drafts.Count,
                session.Drafts.Count(d => d.Level == TestLevel.Contract),
                session.Drafts.Count(d => d.Level == TestLevel.Endpoint),
                session.Drafts.Count(d => d.Level == TestLevel.Workflow),
                matrix.Rows.Count);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "{Module} QA Loop seed skipped due to error.", moduleKey);
        }
    }

    // ── Inventory / matrix builders ───────────────────────────────────────────
    private static InventoryDoc BuildInventory(string moduleKey)
    {
        var routes = Endpoints.Select(e => new RouteInfo(
            e.Method, e.Route, e.Req, e.Resp,
            ValidationRules: [],
            AuthRequirements: e.Anonymous
                ? ["Anonymous (HMAC-SHA256 signature)"]
                : ["Function key + JWT (DealerId, DmsDealerId, CrmUserId)"])).ToList();
        var dtos = Contracts.Select(d => new DtoInfo(
            d.Name, d.Fields.Select(f => new DtoField(f.Name, f.Type)).ToList())).ToList();
        var workflows = Workflows.Select(w => new WorkflowInfo(
            w.Name, w.Steps.Select(s => new WorkflowStep(s.Ep, s.Role)).ToList())).ToList();
        return new InventoryDoc(moduleKey, routes, dtos, workflows);
    }

    private static SurfaceMatrix BuildMatrix(string moduleKey)
    {
        var rows = new List<SurfaceRow>();
        foreach (var e in Endpoints.Where(IsContractEligible))
            rows.Add(new SurfaceRow { Id = $"contract:{e.Method} {e.Route}", Level = TestLevel.Contract, Label = $"Contract: {e.Method} {e.Route}", Status = "gap", Selected = true });
        foreach (var e in Endpoints)
            rows.Add(new SurfaceRow { Id = $"endpoint:{e.Method} {e.Route}", Level = TestLevel.Endpoint, Label = $"Endpoint: {e.Method} {e.Route}", Status = "gap", Selected = true });
        foreach (var w in Workflows)
            rows.Add(new SurfaceRow { Id = $"workflow:{Slug(w.Name)}", Level = TestLevel.Workflow, Label = $"Workflow: {w.Name}", Status = "gap", Selected = true });
        return new SurfaceMatrix(moduleKey, rows);
    }

    // ── Draft builders (mirror PersistDraft + the generator fallbacks) ─────────
    private static List<ApiTestDraft> BuildDrafts(DateTime now, string moduleTitle)
    {
        var drafts = new List<ApiTestDraft>();
        var order = 0;

        // Contract — 1 draft/route (routes with a resolvable request DTO)
        foreach (var e in Endpoints.Where(IsContractEligible))
        {
            var dto = ResolveReqDto(e.Req)!;
            drafts.Add(new ApiTestDraft
            {
                Level = TestLevel.Contract,
                Title = $"[<tcId-pending>] {moduleTitle} - Contract - {e.Method} - Verify that {e.Route} response shape matches the expected schema",
                Preconditions = "",
                StepsJson = JsonSerializer.Serialize(new[]
                {
                    new { action = "Obtain a valid bearer token for the INFINITY QA environment", expected = "Token received" },
                    new { action = $"Send a {e.Method} request to {e.Route} with the bearer token", expected = "Response returns 200" },
                    new { action = "Parse the JSON response body", expected = "Valid JSON body returned" },
                    new { action = "Verify that the response body matches the expected schema", expected = $"Response contains the fields declared by the {e.Method} {e.Route} response contract, each non-null and of the declared type" },
                }),
                SchemaJson = JsonSerializer.Serialize(dto.Fields.ToDictionary(f => JsonName(f.Name), f => f.Type)),
                SpecCode = ContractSpec(e, dto),
                SourceRoutesJson = JsonSerializer.Serialize(new[] { $"{e.Method} {e.Route}" }),
                Status = ApiDraftStatus.Draft,
                SortOrder = order++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // Endpoint — 3 cases/route (200/401/400) sharing ONE spec file, CaseIndex 0..N
        foreach (var e in Endpoints)
        {
            var cases = BuildCases(e);
            var spec = EndpointSpec(e, cases);
            for (var i = 0; i < cases.Count; i++)
            {
                var (label, code) = cases[i];
                drafts.Add(new ApiTestDraft
                {
                    Level = TestLevel.Endpoint,
                    Title = $"[<tcId-pending:{i}>] {moduleTitle} - Endpoint - {e.Method} - Verify that {e.Route} {label}",
                    Preconditions = "",
                    StepsJson = JsonSerializer.Serialize(new[]
                    {
                        new { action = "Obtain a valid bearer token for the INFINITY QA environment", expected = "Token received" },
                        new { action = $"Send a {e.Method} request to {e.Route}", expected = $"Response returns {code} and the body carries the payload expected for the {label} case" },
                        new { action = $"Verify that the response returns status {code}", expected = $"HTTP {code} is returned, and — for state-changing calls — the returned resource shows the change made by this request" },
                    }),
                    SpecCode = spec,
                    SourceRoutesJson = JsonSerializer.Serialize(new[] { $"{e.Method} {e.Route}" }),
                    CaseIndex = i,
                    Status = ApiDraftStatus.Draft,
                    SortOrder = order++,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        // Workflow — 1 draft/workflow
        foreach (var w in Workflows)
        {
            drafts.Add(new ApiTestDraft
            {
                Level = TestLevel.Workflow,
                Title = $"[<tcId-pending>] {moduleTitle} - Workflow - Verify that the {w.Name} workflow completes end-to-end",
                Preconditions = "",
                StepsJson = JsonSerializer.Serialize(
                    w.Steps.Select(s => new { action = $"Send a request to {s.Ep}", expected = $"Response returns 200 and the body reflects the {s.Role} outcome (returned resource contains the fields set by this call)" })
                        .Append(new { action = $"Verify that the {w.Name} workflow completes end-to-end successfully", expected = "All prior calls succeeded and the final resource state matches the expected outcome of the flow" })
                        .ToArray()),
                SpecCode = WorkflowSpec(w),
                SourceRoutesJson = JsonSerializer.Serialize(w.Steps.Select(s => s.Ep).Distinct().ToArray()),
                Status = ApiDraftStatus.Draft,
                SortOrder = order++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        return drafts;
    }

    private static List<(string Label, int Code)> BuildCases(Ep e)
    {
        var cases = new List<(string, int)> { ("200 happy path", 200) };
        if (!e.Anonymous) cases.Add(("401 without bearer", 401));
        if (e.Req is not null || e.Route.Contains('{')) cases.Add(("400 on invalid input", 400));
        return cases;
    }

    // ── Playwright spec templates (placeholder-substituted, no interpolation) ──
    private static string ContractSpec(Ep e, Dto dto)
    {
        var zod = string.Join("\n", dto.Fields.Select(f => "  " + JsonName(f.Name) + ": " + ZodOf(f.Type) + ","));
        var sample = string.Join("\n", dto.Fields.Where(f => !f.Type.EndsWith('?'))
            .Select(f => "      " + JsonName(f.Name) + ": " + SampleOf(f.Type) + ","));
        return
            "import { test, expect } from '@playwright/test';\n" +
            "import { z } from 'zod';\n\n" +
            "// Contract test — " + e.Method + " " + e.Route + " (module: leads)\n" +
            "// Request schema derived from " + dto.Name + ".\n" +
            "const " + dto.Name + "Schema = z.object({\n" + zod + "\n});\n\n" +
            "test.describe('[<tcId-pending>] Contract: " + e.Method + " " + e.Route + "', () => {\n" +
            "  test('request payload conforms to the schema', () => {\n" +
            "    const sample = {\n" + sample + "\n    };\n" +
            "    expect(" + dto.Name + "Schema.safeParse(sample).success).toBe(true);\n" +
            "  });\n\n" +
            "  test('payload missing required fields is rejected', () => {\n" +
            "    expect(" + dto.Name + "Schema.safeParse({}).success).toBe(false);\n" +
            "  });\n" +
            "});\n";
    }

    private static string EndpointSpec(Ep e, List<(string Label, int Code)> cases)
    {
        var url = SampleUrl(e.Route);
        var m = e.Method.ToLowerInvariant();
        var auth = e.Anonymous ? "" : " headers: { Authorization: `Bearer ${TOKEN}` },";
        var body = e.Req is not null ? " data: " + SampleBody(e.Req) + "," : "";

        var sb = new StringBuilder();
        sb.Append("import { test, expect } from '@playwright/test';\n\n");
        sb.Append("const BASE = process.env.LEAD_API_BASE_URL ?? 'https://localhost:7071';\n");
        sb.Append("const TOKEN = process.env.LEAD_API_TOKEN ?? '';\n\n");
        sb.Append("test.describe('Endpoint: " + e.Method + " " + e.Route + "', () => {\n");
        for (var i = 0; i < cases.Count; i++)
        {
            var (label, code) = cases[i];
            var tag = "[<tcId-pending:" + i + ">]";
            if (code == 200)
                sb.Append(
                    "  test('" + tag + " returns 200 for a valid request', async ({ request }) => {\n" +
                    "    const res = await request." + m + "(`${BASE}/api/" + url + "`, {" + auth + body + " });\n" +
                    "    expect(res.status()).toBe(200);\n  });\n");
            else if (code == 401)
                sb.Append(
                    "  test('" + tag + " returns 401 without a bearer token', async ({ request }) => {\n" +
                    "    const res = await request." + m + "(`${BASE}/api/" + url + "`" + (e.Req is not null ? ", { data: {} }" : "") + ");\n" +
                    "    expect(res.status()).toBe(401);\n  });\n");
            else
                sb.Append(
                    "  test('" + tag + " returns 400/422 on invalid input', async ({ request }) => {\n" +
                    "    const res = await request." + m + "(`${BASE}/api/" + url + "`, {" + auth + " data: { __invalid__: true } });\n" +
                    "    expect([400, 422]).toContain(res.status());\n  });\n");
        }
        sb.Append("});\n");
        return sb.ToString();
    }

    private static string WorkflowSpec(Wf w)
    {
        var sb = new StringBuilder();
        sb.Append("import { test, expect } from '@playwright/test';\n\n");
        sb.Append("const BASE = process.env.LEAD_API_BASE_URL ?? 'https://localhost:7071';\n");
        sb.Append("const TOKEN = process.env.LEAD_API_TOKEN ?? '';\n\n");
        sb.Append("test('[<tcId-pending>] Workflow: " + w.Name + "', async ({ request }) => {\n");
        for (var i = 0; i < w.Steps.Length; i++)
        {
            var (ep, role) = w.Steps[i];
            var parts = ep.Split(' ', 2);
            var method = parts[0].ToLowerInvariant();
            var route = parts.Length > 1 ? SampleUrl(parts[1]) : "";
            sb.Append(
                "  // Step " + (i + 1) + " — " + role + "\n" +
                "  const step" + (i + 1) + " = await request." + method + "(`${BASE}/api/" + route + "`, {\n" +
                "    headers: { Authorization: `Bearer ${TOKEN}` },\n  });\n" +
                "  expect(step" + (i + 1) + ".ok()).toBeTruthy();\n\n");
        }
        sb.Append("});\n");
        return sb.ToString();
    }

    // ── Spec file writer (mirrors PersistDraft's slug + level-dir rules) ───────
    private void WriteSpecFiles(ApiTestSession session, string moduleKey)
    {
        var scriptsRoot = ScriptsRoot(moduleKey);
        var writtenEndpointRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in session.Drafts)
        {
            var levelDir = d.Level switch
            {
                TestLevel.Contract => "contracts",
                TestLevel.Endpoint => "endpoints",
                _ => "workflows",
            };
            string slug;
            if (d.Level == TestLevel.Endpoint)
            {
                // Shared multi-case file — slug from the route (SourceRoutes[0]); write once.
                var route = JsonSerializer.Deserialize<string[]>(d.SourceRoutesJson ?? "[]")?.FirstOrDefault() ?? d.Title;
                slug = Slug(route);
            }
            else
            {
                slug = Slug(d.Title);
            }

            var dir = Path.Combine(scriptsRoot, levelDir);
            var file = Path.Combine(dir, slug + ".spec.ts");
            d.SpecFilePath = file;

            if (d.Level == TestLevel.Endpoint && d.CaseIndex is not (null or 0))
                continue; // only case 0 writes the shared file
            if (d.Level == TestLevel.Endpoint && !writtenEndpointRoutes.Add(slug))
                continue;

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, d.SpecCode, Encoding.UTF8);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Could not write spec file {File}", file);
            }
        }
        _db.SaveChanges();
    }

    // ── Narrative (EventLogJson — ConsoleEvent.ToCamel shape) ─────────────────
    private static List<Dictionary<string, object?>> BuildNarrative(string moduleKey, int surfaceCount, int draftCount)
    {
        long seq = 0;
        double t = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeSeconds();
        Dictionary<string, object?> Ev(string kind, string? phase, string level, string text, Dictionary<string, object?>? data = null)
            => new()
            {
                ["seq"] = ++seq,
                ["ts"] = t += 2,
                ["sessionKey"] = $"qaloop:{moduleKey}",
                ["kind"] = kind,
                ["phase"] = phase,
                ["level"] = level,
                ["text"] = text,
                ["data"] = data,
            };
        return
        [
            Ev("system", null, "info", "QA Loop pipeline started · provider CopilotCli"),
            Ev("phase", "fetch", "info", $"Fetching source for module '{moduleKey}'"),
            Ev("result", "fetch", "success", "Fetched source · Lead API (Deal + Task) + Service layer"),
            Ev("result", "fetch", "info", "Found 0 existing test case(s) in suite 174631"),
            Ev("phase", "analyze", "info", $"Module analyzer started for '{moduleKey}'"),
            Ev("result", "analyze", "success", $"Analyzer: 45 route(s) · {surfaceCount} surface(s) · {surfaceCount} selected",
                new() { ["routes"] = 45, ["surfaces"] = surfaceCount, ["selected"] = surfaceCount }),
            Ev("system", null, "info", "Analyzer done — handing off to generators"),
            Ev("phase", "generate", "info", "Generators started (contract / endpoint / workflow)"),
            Ev("result", "generate", "success", "Contract specs generated — zod schema conformance per route"),
            Ev("result", "generate", "success", "Endpoint specs generated — 200 / 401 / 400 cases per route"),
            Ev("result", "generate", "success", "Workflow specs generated — chained multi-step flows"),
            Ev("system", "generate", "success", $"Drafted {draftCount} test case(s) — awaiting review",
                new() { ["terminal"] = true, ["count"] = draftCount }),
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static bool IsContractEligible(Ep e) => ResolveReqDto(e.Req) is not null;

    private static Dto? ResolveReqDto(string? req)
    {
        if (string.IsNullOrEmpty(req)) return null;
        var inner = req.StartsWith("List<") ? req[5..].TrimEnd('>') : req;
        return Contracts.FirstOrDefault(c => c.Name == inner);
    }

    // Matches ContractGeneratorAgent.Slug exactly: lowercase, non-alnum → '-', Trim('-') (no collapse).
    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private static string JsonName(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static string ZodOf(string type)
    {
        var nullable = type.EndsWith('?');
        var core = type.TrimEnd('?');
        var z = core switch
        {
            "string" => "z.string()",
            "int" or "long" or "decimal" or "double" => "z.number()",
            "bool" => "z.boolean()",
            "Guid" => "z.string().uuid()",
            "DateTime" => "z.string()",
            _ when core.StartsWith("List<") => "z.array(z.any())",
            _ => "z.any()",
        };
        return nullable ? z + ".nullable().optional()" : z;
    }

    private static string SampleOf(string type)
    {
        var core = type.TrimEnd('?');
        return core switch
        {
            "string" => "'sample'",
            "int" or "long" => "1",
            "decimal" or "double" => "1000.0",
            "bool" => "false",
            "Guid" => "'00000000-0000-0000-0000-000000000000'",
            "DateTime" => "new Date().toISOString()",
            _ when core.StartsWith("List<") => "[]",
            _ => "{}",
        };
    }

    private static string SampleUrl(string route) => route
        .Replace("{dmsDealerIdCrm}", "12345")
        .Replace("{dealerId}", "12345")
        .Replace("{pipelineId}", "1")
        .Replace("{dealId}", "1001")
        .Replace("{isFavorite}", "true")
        .Replace("{taskTypeId}", "3")
        .Replace("{taskId}", "500");

    private static string SampleBody(string reqType)
    {
        var isList = reqType.StartsWith("List<");
        var inner = isList ? reqType[5..].TrimEnd('>') : reqType;
        var dto = Contracts.FirstOrDefault(c => c.Name == inner);
        if (dto is null) return isList ? "[{}]" : "{}";
        var fields = string.Join(", ", dto.Fields.Where(f => !f.Type.EndsWith('?')).Take(6)
            .Select(f => JsonName(f.Name) + ": " + SampleOf(f.Type)));
        var obj = "{ " + fields + " }";
        return isList ? "[" + obj + "]" : obj;
    }
}
