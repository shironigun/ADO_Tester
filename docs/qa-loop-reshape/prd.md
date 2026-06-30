# PRD: QA Loop Re-shape - 3-Level API Testing + AI Impact Analysis

## Project Overview
The QA Loop Re-shape carves the existing single-page QA Loop into a 3-level API-testing system (Contract/Endpoint/Workflow) with two operating modes: (A) human-driven module scan-and-author producing ADO Test Cases + Playwright specs in Mahmood's house style, and (B) pipeline-driven impacted runs that watch the Lead QA pipeline (#175), use an AI ImpactAnalyzer to pick the affected test subset, execute owned Playwright runs against the INFINITY DMS profile, mark per-Test-Case Pass/Fail via a fresh ADO Test Run, draft-file ADO Bugs, and post a Teams card. It adds 5 SQLite tables (one EF migration), 5 LLM agents (1 rewrite + 4 new), 8 embedded prompts + 3 Teams templates, 6 new ADO client methods, a background orchestrator, a Playwright runner + run-execution service, a pipeline-watcher hosted service, 3 thin controllers, and 3 frontend pages reusing the SessionWorkspace shell. All work lives inside ADO_Tester; the CRM repo is read-only; byte-exact ADO write parity and the legacy entities/endpoints are preserved. An additive amendment (QAL-055..061) layers a Node-native Allure HTML report per run (gated by a new `qaloop.report_provider` setting, junit+json always-on) with server-side per-(module,pipeline) trend history and API-level-only attachments, plus a global `qaloop.activity_enabled` toggle and a per-row `ActivityEnabled` column on ApiTestSession + TestRun (folded into the same migration; no new tables/agents/controllers/pages) that gates verbose streaming + EventLogJson — bringing the task count to 61.

## Created
- **Date:** 2026-06-30
- **Author:** Mahmood Ahmad + Claude

---

## Tasks

### FOUNDATION

#### QAL-001: Add seven QA-Loop enums
- **Status:** [x]
- **ID:** QAL-001
- **Description:** Blueprint Data Layer → "Enums (Core/Models/Enums.cs, modified)". Append the seven new enums to the existing Enums.cs. Four serialize by exact member name (plain .HasConversion<string>()); four need lowercase/snake_case/kebab wire values and MUST reuse the [EnumMember] + extension pattern already used by BatchModule/BatchMode at the bottom of the file. Do NOT unify ApiSessionStatus with the existing SessionStatus.
- **File:** `backend/src/QaAutomation.Core/Models/Enums.cs`
- **Acceptance Criteria:**
  - ApiSessionStatus { Analyzing, AwaitingReview, Drafting, Drafted, Pushing, Done, Failed, Cancelled } — exact-name
  - ApiDraftStatus { Draft, Approved, Pushed, Rejected } — exact-name
  - TestRunStatus { Queued, Running, Passed, Failed, Cancelled, Error } — exact-name
  - TestLevel { Contract, Endpoint, Workflow } with Ui reserved — wire contract/endpoint/workflow/ui via [EnumMember]+extension+ValueConverter pattern
  - FailureClassification { RealBug, Flaky, TestIssue, StaleTest } — wire real_bug/flaky/test_issue/stale_test, must match FailureTriageAgent output
  - TriggerSource { Manual, PipelineWatcher } — wire manual/pipeline-watcher
  - DmsProfile { Ideal, Infinity, Aspen, Quantum } — wire lowercase ideal/infinity/aspen/quantum
  - File compiles; existing enums untouched

#### QAL-002: Add shared agent-output POCOs
- **Status:** [ ]
- **ID:** QAL-002
- **Description:** Blueprint Agents & Prompts → "Shared agent output POCOs (Core/Models/AgentOutputs.cs, modified)" and Generator Design InventoryJson/AnalysisJson shapes. Add the net-new records that the analyzer emits and the generators/impact analyzer consume.
- **File:** `backend/src/QaAutomation.Core/Models/AgentOutputs.cs`
- **Acceptance Criteria:**
  - record InventoryDoc(module, routes[], dtos[], workflows[]) with RouteInfo(method, route, requestDto?, responseDto?, validationRules[], authRequirements), DtoInfo(name, fields[]), DtoField(name, type), WorkflowInfo(name, steps[]), WorkflowStep(endpoint, role)
  - record SurfaceMatrix(module, rows[]) with SurfaceRow carrying id, level, label, status (gap|covered), linkedTestCaseId, selected
  - record ImpactAnalysis(impactedDraftIds[], reasoning, confidence high|medium|low, skippedReason?, fallback)
  - All in namespace QaAutomation.Core.Models; existing POCOs unchanged; compiles

#### QAL-003: Add AdoBuild + AdoBuildChange build-read DTOs
- **Status:** [ ]
- **ID:** QAL-003
- **Description:** Blueprint Pipeline Watcher → "Net-new build-read DTOs" (line ~2737-2755). Add two flat positional records next to ChangedFile/AdoTestPlan in AdoModels.cs in the file's existing public sealed record style. Use the canonical name AdoBuildChange (NOT AdoBuildCommit, which appears once in a stray blueprint interface snippet — follow the canonical naming table). Defined here in FOUNDATION; first referenced by the build-read methods in the WATCHER phase (definition-before-use, do not defer).
- **File:** `backend/src/QaAutomation.Core/Models/AdoModels.cs`
- **Acceptance Criteria:**
  - public sealed record AdoBuild(int Id, string SourceVersion, string RepositoryId, DateTime FinishedAt)
  - public sealed record AdoBuildChange(string Id, string Message)
  - Placed in the Build/pipeline-reads region; compiles; no existing record altered

#### QAL-004: Add QaLoopOptions configuration class
- **Status:** [ ]
- **ID:** QAL-004
- **Description:** Blueprint TeamsNotifier/Settings → "Configuration: QaLoopOptions". Typed options bound to section "QaLoop", carrying poll interval, Teams webhook/template, and runtime/scripts/runs folder names; bridged from flat .env keys then overlaid by RuntimeSettings (wiring done in DI task).
- **File:** `backend/src/QaAutomation.Core/Configuration/QaLoopOptions.cs`
- **Acceptance Criteria:**
  - class QaLoopOptions with SectionName = "QaLoop"
  - Properties for PollIntervalMinutes (default 5), TeamsWebhookUrl, TeamsTemplate (default "developer"), and ScriptsFolderName/RunsFolderName defaults "scripts"/"runs"
  - Namespace QaAutomation.Core.Configuration; compiles

#### QAL-005: Add DmsProfileOptions registry
- **Status:** [ ]
- **ID:** QAL-005
- **Description:** Blueprint → "Configuration: DmsProfileOptions". Four-profile registry; only Infinity credentialed for the MVP. Runner sets AUTOMATION_PROFILE=infinity by default.
- **File:** `backend/src/QaAutomation.Core/Configuration/DmsProfileOptions.cs`
- **Acceptance Criteria:**
  - Registry enumerates Ideal/Infinity/Aspen/Quantum with Profile, Key (lowercase), DisplayName, Credentialed flags
  - Infinity.Credentialed = true; the other three false
  - Namespace QaAutomation.Core.Configuration; compiles

### DATA LAYER

#### QAL-006: Create ApiTestSession + ApiTestDraft entities
- **Status:** [ ]
- **ID:** QAL-006
- **Description:** Blueprint Data Layer → Entities → ApiTestSession + ApiTestDraft (Core/Models/ApiTestSession.cs, new). Match existing EF entity style exactly (public sealed class, int Id, plain int FKs, DateTime CreatedAt/UpdatedAt, nav collection = []). WorkItemId/WorkItemTitle nullable for scan mode.
- **File:** `backend/src/QaAutomation.Core/Models/ApiTestSession.cs`
- **Acceptance Criteria:**
  - ApiTestSession columns: Id, ProjectId, Module, Repository?, Branch?, WorkItemId?, WorkItemTitle?, TestPlanId?, TestSuiteId?, Status(ApiSessionStatus=Analyzing), AnalysisJson?, InventoryJson?, EventLogJson?, ErrorMessage?, AutoHandoff(bool), CreatedAt, UpdatedAt, Drafts=[]
  - ApiTestDraft columns: Id, SessionId, Level(TestLevel=Contract), Title, Preconditions(=""), StepsJson(="[]"), SchemaJson?, SpecCode, SpecFilePath?, Tags?, SourceRoutesJson?, ErrorMessage?, Status(ApiDraftStatus), AdoTestCaseId?, SortOrder, CreatedAt, UpdatedAt
  - Both public sealed class in QaAutomation.Core.Models; compiles

#### QAL-007: Create TestRun + TestFailure entities
- **Status:** [ ]
- **ID:** QAL-007
- **Description:** Blueprint Data Layer → Entities → TestRun + TestFailure (Core/Models/TestRun.cs, new). One TestRun per execution; one TestFailure per failed test carrying triage state and the serialized Bug draft.
- **File:** `backend/src/QaAutomation.Core/Models/TestRun.cs`
- **Acceptance Criteria:**
  - TestRun columns: Id, ProjectId, Module?, LevelFilter?, TagFilter?, Profile, TestPlanId?, TestSuiteId?, TriggerSource, TriggerPipelineId?, TriggerBuildId?, TriggerCommitSha?, ImpactAnalysisJson?, Status(TestRunStatus), Passed/Failed/Skipped(int), DurationMs, JunitPath?, ReportPath?, AdoTestRunId?, ErrorMessage?, StartedAt?, FinishedAt?, plus CreatedAt for the list index
  - TestFailure columns: Id, RunId, TestTitle, AdoTestCaseId?, Classification(FailureClassification), Confidence, Reason, BugDraftJson?, BugId?, ArtifactPaths?
  - public sealed class in QaAutomation.Core.Models; compiles

#### QAL-008: Create PipelineWatch entity
- **Status:** [ ]
- **ID:** QAL-008
- **Description:** Blueprint Data Layer → Entities → PipelineWatch (Core/Models/PipelineWatch.cs, new). Configured pipelines polled by the watcher; MVP has exactly one row.
- **File:** `backend/src/QaAutomation.Core/Models/PipelineWatch.cs`
- **Acceptance Criteria:**
  - Columns: Id, ProjectId, AdoPipelineId, AdoPipelineName, Module, LastSeenBuildId?, LastPollAt?, LastPollError?, Enabled(bool, default true), CreatedAt, UpdatedAt
  - public sealed class in QaAutomation.Core.Models; compiles

#### QAL-009: Register 5 DbSets + OnModelCreating in QaDbContext
- **Status:** [ ]
- **ID:** QAL-009
- **Description:** Blueprint Data Layer → "QaDbContext changes". Add 5 DbSet<> and OnModelCreating registrations: ToTable + explicit HasColumnName per property + value converters for the 4 wire-string enums + HasIndex(...).HasDatabaseName(...) + cascade FKs. JSON-blob columns are plain TEXT NULL strings with no converters. Legacy entities untouched.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs`
- **Acceptance Criteria:**
  - DbSets: ApiTestSessions, ApiTestDrafts, TestRuns, TestFailures, PipelineWatches
  - Enum columns persist as strings; the 4 wire-string enums use ValueConverters; exact-name enums use HasConversion<string>()
  - Indexes: ix_ApiTestSessions_ProjectId_CreatedAt, ix_ApiTestDrafts_SessionId, ix_TestRuns_ProjectId_CreatedAt, ix_TestFailures_RunId, ix_PipelineWatches_ProjectId_AdoPipelineId (UNIQUE)
  - Cascade FKs: 3 Project FKs + 2 parent→child (ApiTestSession→ApiTestDraft, TestRun→TestFailure) all OnDelete(Cascade)
  - No legacy entity/table/column altered; build succeeds

#### QAL-010: Generate AddQaLoopReshape EF migration
- **Status:** [ ]
- **ID:** QAL-010
- **Description:** Blueprint Data Layer → "EF migration" + Build/Run/Migrate. Generate (do not hand-edit) the migration from OnModelCreating. dotnet is not on PATH — use the wrapper from the dotnet-build-environment memory; stop the backend first if running (SQLite file lock). Migration auto-applies on next backend start via DatabaseInitializer (there is NO dotnet ef database update step).
- **File:** `backend/src/QaAutomation.Infrastructure/Data/Migrations/<ts>_AddQaLoopReshape.cs`, `backend/src/QaAutomation.Infrastructure/Data/Migrations/<ts>_AddQaLoopReshape.Designer.cs`, `backend/src/QaAutomation.Infrastructure/Data/Migrations/QaDbContextModelSnapshot.cs`
- **Acceptance Criteria:**
  - Command: dotnet ef migrations add AddQaLoopReshape --project src\QaAutomation.Infrastructure --startup-project src\QaAutomation.Api
  - Up() creates 5 tables with enum columns typed TEXT, the unique PipelineWatches index, and onDelete: Cascade on the 3 Project FKs + 2 parent→child FKs
  - Migration not hand-edited; ModelSnapshot updated by EF; additive-only (no legacy table altered)
  - Fresh start (or wiped backend/data) applies the migration and creates all 5 tables

### AGENTS & PROMPTS

#### QAL-011: Author module_analyzer_system.txt rewrite
- **Status:** [ ]
- **ID:** QAL-011
- **Description:** Blueprint Agents & Prompts. Rewrite the (modified) embedded prompt body to the new inventory + per-level coverage-matrix contract (was a flat flows[] list). Stays a single AgentPrompts property; only the .txt body changes.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/Prompts/module_analyzer_system.txt`
- **Acceptance Criteria:**
  - Prompt instructs the model to emit InventoryDoc (routes+DTOs+workflow graph) AND SurfaceMatrix (contract/endpoint/workflow rows pre-reconciled vs the supplied existing Test Cases) plus additionalFilesNeeded[]
  - Model may cite only the supplied existing-test-case ids as linkedTestCaseId
  - Byte content is the new contract; file remains an EmbeddedResource under Prompts/

#### QAL-012: Author 7 net-new embedded prompt .txt files
- **Status:** [ ]
- **ID:** QAL-012
- **Description:** Blueprint Agents & Prompts → "Net-new embedded prompt files" table. Seven new prompts under Prompts/. The three *_testcase_system prompts reuse the tester_system.txt skeleton to preserve Mahmood's voice. The .csproj already globs Prompts/*.txt as EmbeddedResource — verify the glob covers them.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/Prompts/contract_generator_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/contract_testcase_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/endpoint_generator_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/endpoint_testcase_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/workflow_generator_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/workflow_testcase_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/impact_analyzer_system.txt`
- **Acceptance Criteria:**
  - contract_generator emits { schema (Zod), spec (.spec.ts) }; contract_testcase emits house-style Test Case array
  - endpoint_generator emits { spec, cases[] } (200 / 401-no-bearer / 400-invalid); endpoint_testcase emits per-case Test Case array
  - workflow_generator emits { spec } chained with idempotent cleanup; workflow_testcase emits 1 ADO step per leg
  - impact_analyzer emits { impactedDraftIds[], reasoning, confidence, skippedReason, fallback } and is told to cite ONLY supplied inventory ids
  - Every generator prompt instructs the spec title to include [<tcId-pending>]
  - All 7 are EmbeddedResource and build into the assembly

#### QAL-013: Register 7 prompt properties in AgentPrompts
- **Status:** [ ]
- **ID:** QAL-013
- **Description:** Blueprint Agents & Prompts → "AgentPrompts registrations". Add seven Load(...) properties; ModuleAnalyzerSystem declaration unchanged (only its body was rewritten).
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/AgentPrompts.cs`
- **Acceptance Criteria:**
  - Properties: ContractGeneratorSystem, ContractTestcaseSystem, EndpointGeneratorSystem, EndpointTestcaseSystem, WorkflowGeneratorSystem, WorkflowTestcaseSystem, ImpactAnalyzerSystem
  - Each loads its matching Prompts/*.txt resource via the existing Load helper
  - ModuleAnalyzerSystem property unchanged; compiles

#### QAL-014: Rewrite ModuleAnalyzerAgent (inventory + matrix + 1-pass refinement)
- **Status:** [ ]
- **ID:** QAL-014
- **Description:** Blueprint Agents & Prompts → "ModuleAnalyzerAgent (rewrite)". Reuse the existing completion + DI plumbing but rewrite output to emit InventoryDoc + SurfaceMatrix in one pass, plus an optional additionalFilesNeeded[] that drives exactly one bounded refinement fetch (no unbounded tool loop). Reconcile surfaces against the supplied existing Test Cases.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/ModuleAnalyzerAgent.cs`
- **Acceptance Criteria:**
  - AnalyzeAsync(module, source, existingCases, progress, emit, ct) returns ModuleAnalysis(InventoryDoc, SurfaceMatrix, additionalFilesNeeded)
  - Parses routes/dtos/workflows into InventoryDoc and surface rows (id,level,label,status,linkedTestCaseId,selected) into SurfaceMatrix
  - If additionalFilesNeeded non-empty, C# fetches them and re-invokes the agent exactly ONCE
  - Throws AgentException on non-object JSON; provider-portable (ILlmClient + JsonUtils.CompleteJsonAsync + PipelineConcurrency gate)
  - gap surfaces default selected=true; covered surfaces (linkedTestCaseId set) default selected=false

#### QAL-015: Create ContractGeneratorAgent
- **Status:** [ ]
- **ID:** QAL-015
- **Description:** Blueprint Agents & Prompts → ContractGeneratorAgent (new). One LLM call for the Zod schema + validator spec, one for the house-style Test Case. Emits a GeneratedSpec (test case + spec text + schema + source routes).
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/ContractGeneratorAgent.cs`
- **Acceptance Criteria:**
  - GenerateAsync produces one draft per contract endpoint: SchemaJson (Zod), SpecCode, GeneratedTestCase, SourceRoutes ["<METHOD> <route>"]
  - Test Case title [<tcId-pending>] Contract: <METHOD> <route> — response shape matches schema; preconditions + 2 steps per blueprint
  - Spec title carries [<tcId-pending>] (enforced by shared TitleTag helper)
  - FallbackTestCase used if the test-case LLM returns nothing; output path backend/data/scripts/{module}/contracts/{slug}.contract.spec.ts recorded as intended location
  - Provider-portable single-shot pattern; compiles

#### QAL-016: Create EndpointGeneratorAgent
- **Status:** [ ]
- **ID:** QAL-016
- **Description:** Blueprint Agents & Prompts → EndpointGeneratorAgent (new). Emits one spec file grouping all cases of an endpoint but a separate ADO Test Case per case (happy 200 / 401 / 400).
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/EndpointGeneratorAgent.cs`
- **Acceptance Criteria:**
  - GenerateAsync returns a list of GeneratedSpec — one ADO Test Case per case
  - Titles [<tcId-pending>] Endpoint: <METHOD> <route> — <case> (e.g. 200 returns array / 401 without bearer / 400 on invalid dealer id)
  - Feeds validationRules + authRequirements into the prompt; each spec title carries the tag
  - Output path backend/data/scripts/{module}/endpoints/{slug}.endpoint.spec.ts; provider-portable; compiles

#### QAL-017: Create WorkflowGeneratorAgent
- **Status:** [ ]
- **ID:** QAL-017
- **Description:** Blueprint Agents & Prompts → WorkflowGeneratorAgent (new). Emits a chained-call business-flow spec with idempotent cleanup and a Test Case with one ADO step per called endpoint.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/WorkflowGeneratorAgent.cs`
- **Acceptance Criteria:**
  - GenerateAsync(module, inventory, workflowNode,...) returns one GeneratedSpec per workflow
  - Title [<tcId-pending>] Workflow: <flow name>; SourceRoutes lists all chained routes
  - Spec chains the calls with idempotent cleanup; title carries the tag
  - Output path backend/data/scripts/{module}/workflows/{slug}.workflow.spec.ts; provider-portable; compiles

#### QAL-018: Create shared [<tcId-pending>] TitleTag enforcement helper
- **Status:** [ ]
- **ID:** QAL-018
- **Description:** Blueprint Agents & Prompts → "Post-LLM [<tcId-pending>] title-tag enforcement (shared)". A static helper the three generators call to verify/auto-inject the placeholder so the JUnit parser's \[(\d+)\] regex can later map failures.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/ContractGeneratorAgent.cs`
- **Acceptance Criteria:**
  - TitleTag.EnsureInSpec(specCode, title) returns spec text guaranteed to contain a [<tcId-pending>] (or numeric) tag in the test title
  - If the model omitted the tag, it is auto-injected
  - Shared by all three generator agents (placement may be a small shared static class/region)

#### QAL-019: Create ImpactAnalyzerAgent
- **Status:** [ ]
- **ID:** QAL-019
- **Description:** Blueprint Agents & Prompts → ImpactAnalyzerAgent (new). Reads changed files + commit messages + a compact ApiTestDraft inventory projection + FTS5 KB chunks; returns impacted draft ids with reasoning/confidence. Reuses JsonUtils.CompleteJsonAsync + the PipelineConcurrency gate.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/ImpactAnalyzerAgent.cs`
- **Acceptance Criteria:**
  - AnalyzeAsync(changedFiles, commitMessages, testInventory, kbContext,...) returns ImpactAnalysis(impactedDraftIds, reasoning, confidence, skippedReason, fallback)
  - Prompt instructs the model to cite ONLY supplied inventory ids; inventory projection is { id, module, level, title, sourceRoutes[], tags[] } — SpecCode NOT sent
  - Throws AgentException on non-object JSON (caller handles fallback)
  - Provider-portable single-shot; compiles

### ORCHESTRATOR

#### QAL-020: Define IQaLoopOrchestrator abstraction
- **Status:** [ ]
- **ID:** QAL-020
- **Description:** Blueprint QaLoopOrchestrator → "Interface". Mirror the AgentOrchestrator surface: Start, GenerateDrafts, Push, PushDraft, Cancel.
- **File:** `backend/src/QaAutomation.Core/Abstractions/IQaLoopOrchestrator.cs`
- **Acceptance Criteria:**
  - interface IQaLoopOrchestrator with Start(sessionId), GenerateDraftsAsync(sessionId), PushAllAsync(sessionId, plan, suite, story?), PushDraftAsync(draftId, plan, suite, story?), Cancel(sessionId)
  - Namespace QaAutomation.Core.Abstractions; compiles

#### QAL-021: Implement QaLoopOrchestrator scan+analyze background pipeline
- **Status:** [ ]
- **ID:** QAL-021
- **Description:** Blueprint QaLoopOrchestrator → Start / Launch / queue-gate wrapper / Start-seed-and-analyze. Copy the AgentOrchestrator skeleton verbatim (own DI scope via IServiceScopeFactory, IEventBus SSE with Last-Event-ID replay, durable EventLogJson, CancellationRegistry, PipelineConcurrency). HTTP seeds the ApiTestSession synchronously; the background task fetches source, runs ModuleAnalyzerAgent, persists Inventory/Analysis, and stops at AwaitingReview (or proceeds when AutoHandoff=1).
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
- **Acceptance Criteria:**
  - Singleton; background Task uses IServiceScopeFactory.CreateScope (never request scope)
  - Analyzing → AwaitingReview (manual gate) or Analyzing → Drafting when AutoHandoff=1; Analyzing → Failed on analyst exception; Analyzing → Cancelled on user cancel
  - Persists InventoryJson + AnalysisJson; emits ConsoleEvents via IEventBus; durable narrative to EventLogJson
  - gap surfaces preselected true, covered preselected false in the persisted matrix
  - Reconciles against the chosen suite's existing Test Cases when testSuiteId supplied

#### QAL-022: Implement GenerateDraftsAsync (3 generators in parallel, selected-only)
- **Status:** [ ]
- **ID:** QAL-022
- **Description:** Blueprint QaLoopOrchestrator → "AwaitingReview gate → GenerateDraftsAsync" + "GenerateDraftsInnerAsync". After review, run Contract/Endpoint/Workflow generators via Task.WhenAll, generating drafts only for surfaces still selected (not flagged covered). Per-draft errors go to ApiTestDraft.ErrorMessage without failing the batch; specs written to disk.
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
- **Acceptance Criteria:**
  - Routes each selected surface by level to its generator; runs all three via Task.WhenAll
  - Generates a draft only where AnalysisJson surface selected==true
  - Writes each spec to backend/data/scripts/{module}/{level}/{slug}.spec.ts and sets SpecFilePath; on disk-write failure SpecFilePath=null with a warning, draft still saved
  - Per-draft generator/spec errors captured to ErrorMessage; batch not failed
  - Drafting → Drafted on success; Drafting → Failed only on Task.WhenAll rethrow / DB-write failure; partial drafts preserved
  - StepsJson serialized as array of {action, expected}; SourceRoutesJson populated from generator inventory input

#### QAL-023: Implement Push / PushDraft (idempotent re-push, tag rewrite)
- **Status:** [ ]
- **ID:** QAL-023
- **Description:** Blueprint QaLoopOrchestrator → "Push" + "Per-row synchronous push". For each Approved draft: CreateTestCaseAsync → AddTestCaseToSuiteAsync → LinkTestedByAsync only when WorkItemId.HasValue → rewrite [<tcId-pending>] to the real [id] on disk and on the draft Title. Push is idempotent on draft Status; re-pressing Push retries only non-Pushed drafts.
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
- **Acceptance Criteria:**
  - Drafted → Pushing → Done on full success; Pushing → Drafted on partial failure (already-pushed drafts stay Pushed); Pushing → Failed on unrecoverable error
  - LinkTestedByAsync called ONLY when the session has a WorkItemId
  - On successful push, the placeholder [<tcId-pending>] is rewritten to the real [adoTcId] in both SpecCode-on-disk and Title; AdoTestCaseId set; draft Status=Pushed
  - Re-pressing Push retries only non-Pushed drafts (no separate retry button, no partial-failure state)
  - Per-draft progress emitted via IEventBus; reused FailOrCancelAsync terminal-write tail; new EventPhases constant added

### RUNNER & ADO TEST RUN

#### QAL-024: Define IPlaywrightRunner abstraction
- **Status:** [ ]
- **ID:** QAL-024
- **Description:** Blueprint PlaywrightRunner section → "The Runner". Runner abstraction (singleton-able) consumed by RunExecutionService.
- **File:** `backend/src/QaAutomation.Core/Abstractions/IPlaywrightRunner.cs`
- **Acceptance Criteria:**
  - interface IPlaywrightRunner with a RunAsync(PlaywrightRunSpec spec, eventKey, ct) returning a PlaywrightRunResult (exit code, failed tests, counts, paths)
  - PlaywrightRunSpec carries runId, profile, grep tags, module, levelFilter
  - Namespace QaAutomation.Core.Abstractions; compiles

#### QAL-025: Implement PlaywrightRunner (install-once runtime, grep, spawn, parse)
- **Status:** [ ]
- **ID:** QAL-025
- **Description:** Blueprint PlaywrightRunner → prepare dir / provision runtime once / determine spec set + build --grep / spawn serialized + streamed / persist outputs / counts. Shared playwright-runtime provisioned once at runner-service startup (npm ci + npx playwright install), read-only at run time. AUTOMATION_PROFILE set on the child ProcessStartInfo.Environment, not a CLI flag.
- **File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`
- **Acceptance Criteria:**
  - Per run writes backend/data/runs/{runId}/ with minimal package.json + playwright.config.ts whose testDir points at backend/data/scripts/{module}/{level}/ and resolves node_modules at the shared read-only backend/data/playwright-runtime/
  - Runtime provisioned exactly ONCE at startup (npm ci + npx playwright install); runs never re-install or race
  - --grep built as an alternation of each impacted/selected draft's unique [<adoTcId>] tag (e.g. \[41833\]|\[41840\]); the level-wide Tags column is NOT used for impacted-run scoping
  - Only Pushed drafts (numeric [<id>], AdoTestCaseId set) are eligible; results with no numeric [<id>] are logged and excluded, never silently dropped
  - Spawn serialized through the existing PipelineConcurrency gate; AUTOMATION_PROFILE on child env; stdout/stderr streamed line-by-line via IEventBus
  - Outputs junit.xml, results.json, html/ written; counts (Passed/Failed/Skipped/DurationMs) parsed from JUnit
  - All paths resolve off StorageOptions.DataDirectory + ScriptsFolderName/RunsFolderName
  - (amended: Allure/activity — the human-facing HTML report + reporter list + ReportPath resolution are delegated to QAL-055/QAL-057; junit + json stay always-on; verbose stdout streaming + EventLogJson are gated by ActivityEnabled per QAL-060)

#### QAL-026: Add 3 ADO Test Run lifecycle + GetTestPoints methods to IAzureDevOpsClient
- **Status:** [ ]
- **ID:** QAL-026
- **Description:** Blueprint API/ADO mutation (new) + PlaywrightRunner → "New IAzureDevOpsClient methods". Append the test-run lifecycle + test-point resolution to the interface, each with trailing CancellationToken ct = default. NOTE: this interface file is also edited in the WATCHER phase (2 build-read methods) — both edits are purely additive appends to the same file; no conflict.
- **File:** `backend/src/QaAutomation.Core/Abstractions/IAzureDevOpsClient.cs`
- **Acceptance Criteria:**
  - CreateAdoTestRunAsync(planId, suiteId, name, pointIds[]) → int adoTestRunId
  - SetAdoTestResultAsync(adoTestRunId, testPointId, outcome, errorMessage?, durationMs)
  - CompleteAdoTestRunAsync(adoTestRunId)
  - GetTestPointsAsync(planId, suiteId, testCaseIds) → pointIds (map tcId→pointId)
  - Grouped in a new region with trailing ct = default; compiles

#### QAL-027: Implement ADO Test Run lifecycle methods in AzureDevOpsClient
- **Status:** [ ]
- **ID:** QAL-027
- **Description:** Blueprint PlaywrightRunner → "New IAzureDevOpsClient methods" implementation. Additive only — follow existing Opts.BaseUrl + Opts.ApiVersion + SendDocAsync/GetDocAsync helpers; do NOT touch CreateTestCaseAsync/CreateBugAsync bodies (golden parity). NOTE: this implementation file is also edited in the WATCHER phase (2 build-read methods) — additive append, no conflict.
- **File:** `backend/src/QaAutomation.Infrastructure/AzureDevOps/AzureDevOpsClient.cs`
- **Acceptance Criteria:**
  - CreateAdoTestRunAsync POSTs /_apis/test/runs?api-version=7.0 with { name, plan{id}, pointIds[] } and returns the new run id
  - SetAdoTestResultAsync POSTs /_apis/test/Runs/{runId}/results?api-version=7.0 with the per-point payload (testPoint{id}, outcome, errorMessage, durationInMs)
  - CompleteAdoTestRunAsync PATCHes /_apis/test/runs/{runId}?api-version=7.0 with { state: "Completed" }
  - GetTestPointsAsync resolves TestPoint ids for the given test case ids in the plan/suite
  - No edits to existing write bodies; golden parity preserved; compiles

#### QAL-028: Implement RunExecutionService (runner + ADO Test Run + triage handoff)
- **Status:** [ ]
- **ID:** QAL-028
- **Description:** Blueprint PlaywrightRunner → "RunExecutionService" + "ADO Test Run lifecycle" → plan/suite resolution + skip rule, per-result outcomes + complete. Scoped service (uses DbContext). Drives the runner, the ADO Test Run lifecycle, and hands off to triage.
- **File:** `backend/src/QaAutomation.Infrastructure/QaRuns/RunExecutionService.cs`
- **Acceptance Criteria:**
  - Resolves TestRun.TestPlanId/TestSuiteId from the single ApiTestSession the selected/impacted drafts belong to; rejects a run whose drafts span more than one (plan, suite)
  - If plan/suite unknown (moduleFilter-only run, or never-pushed drafts) the ADO Test Run is SKIPPED with a warning on TestRun.ErrorMessage; Playwright run + Bug drafting still proceed
  - Otherwise resolves TestPoints → CreateAdoTestRunAsync → stores AdoTestRunId; per JUnit result calls SetAdoTestResultAsync(Pass/Fail) keyed on the [<id>] tag; CompleteAdoTestRunAsync on finish
  - If CreateAdoTestRunAsync fails, the Playwright run still proceeds; failure recorded as a warning on TestRun.ErrorMessage; Bug filing still works
  - Run status transitions: Queued→Running→Passed/Failed/Error per the TestRun lifecycle; hands the parsed JUnit to TriageAsync

### PIPELINE WATCHER & ADO CLIENT METHODS

#### QAL-029: Add 2 build-read methods to IAzureDevOpsClient
- **Status:** [ ]
- **ID:** QAL-029
- **Description:** Blueprint Pipeline Watcher → "IAzureDevOpsClient additions". Append the two build reads (Build read scope already on the PAT) so the watcher can enumerate succeeded builds and their commits; file resolution reuses existing GetCommitChangedFilesAsync. Additive append to the same interface file the RUNNER phase touched — no conflict. Use canonical AdoBuildChange (defined in FOUNDATION).
- **File:** `backend/src/QaAutomation.Core/Abstractions/IAzureDevOpsClient.cs`
- **Acceptance Criteria:**
  - GetPipelineBuildsAsync(pipelineId, minBuildId?=null, ct) → IReadOnlyList<AdoBuild> (completed+succeeded, newest-first, id > minBuildId)
  - GetBuildChangesAsync(buildId, ct) → IReadOnlyList<AdoBuildChange>
  - Both in the build/pipeline-reads region with trailing ct = default; compiles

#### QAL-030: Implement 2 build-read methods in AzureDevOpsClient
- **Status:** [ ]
- **ID:** QAL-030
- **Description:** Blueprint Pipeline Watcher → "AzureDevOpsClient implementations". GET-and-map against /_apis/build/builds, mirroring GetTestPlansAsync style; no new usings (System.Globalization already imported). Additive append to the same impl file the RUNNER phase touched — no conflict; existing write bodies untouched.
- **File:** `backend/src/QaAutomation.Infrastructure/AzureDevOps/AzureDevOpsClient.cs`
- **Acceptance Criteria:**
  - GetPipelineBuildsAsync GETs /_apis/build/builds?definitions={id}&statusFilter=completed&resultFilter=succeeded&queryOrder=finishTimeDescending and maps id/sourceVersion/repositoryId/finishTime; minBuildId does incremental server-side filtering
  - GetBuildChangesAsync GETs /_apis/build/builds/{id}/changes and maps commit id+message
  - Additive only; existing write bodies untouched; compiles

#### QAL-031: Implement PipelineWatcherHostedService (poll, impact, fallback, run-latest)
- **Status:** [ ]
- **ID:** QAL-031
- **Description:** Blueprint Pipeline Watcher → "PipelineWatcherHostedService" + "run-latest manual trigger" + "Behaviour summary", and solution §Pipeline Watcher. IHostedService poll loop on qaloop.poll_interval_minutes (default 5). For each enabled watch: list new succeeded builds (id > LastSeenBuildId, oldest-first) → resolve commits → changed files (reuse GetCommitChangedFilesAsync) → ImpactAnalyzerAgent → empty? Cancelled TestRun (no Playwright, no Teams) : create TestRun + enqueue run scoped to impactedDraftIds → advance LastSeenBuildId. Back off to 15 min on poll error.
- **File:** `backend/src/QaAutomation.Infrastructure/QaPipelines/PipelineWatcherHostedService.cs`
- **Acceptance Criteria:**
  - Empty impactedDraftIds → single TestRun Status=Cancelled, ErrorMessage "no impacted tests", no enqueue, no Teams ping
  - Non-empty → TestRun(TriggerSource=pipeline-watcher, TriggerPipelineId/BuildId/CommitSha, ImpactAnalysisJson, Profile=infinity) and enqueues a run scoped to result.impactedDraftIds
  - ImpactAnalyzer malformed JSON: retry once; on second failure fall back to module-wide run (all Pushed drafts in the module) and set fallback=true in ImpactAnalysisJson
  - LastSeenBuildId advances monotonically (oldest-first); LastPollAt updated, LastPollError cleared on success; on poll error LastPollError set + back-off to 15 min
  - run-latest fetches the most recent succeeded build, runs the same path, ADVANCES LastSeenBuildId past that build, returns runId
  - Bug filing on pipeline runs is draft-first (never auto-files)

### API (CONTROLLERS + DTOs + SSE)

#### QAL-032: Author QaLoopContracts DTOs
- **Status:** [ ]
- **ID:** QAL-032
- **Description:** Blueprint API → "Contracts: QaLoopContracts.cs". sealed class DTOs, camelCase wire, enum-by-exact-name, static From(entity) mappers, {Total,Items} lists. PATCH-draft skip-null; PATCH-analysis full-replace.
- **File:** `backend/src/QaAutomation.Core/Contracts/QaLoopContracts.cs`
- **Acceptance Criteria:**
  - ScanStartRequest { module, repository?, branch?, sourceDirs?, extensions?, workItemId?, testPlanId?, testSuiteId?, autoHandoff }
  - ScanStartResponse { sessionId, existingSessionId? }
  - ApiAnalysisUpdate (full-replace), UpdateApiDraftRequest (skip-null status/title/preconditions/stepsJson/specCode)
  - ApiTestDraftDto + From; ApiSessionSummary + From; ApiSessionList {Total,Items}; ApiSessionDetail + From(session, drafts); ApproveAllResponse/AcceptedResponse/CancelResponse
  - Namespace QaAutomation.Core.Contracts; compiles

#### QAL-033: Author QaRunsContracts DTOs
- **Status:** [ ]
- **ID:** QAL-033
- **Description:** Blueprint API → "Contracts: QaRunsContracts.cs". Run start/list/detail, triage, file-bugs DTOs with From mappers.
- **File:** `backend/src/QaAutomation.Core/Contracts/QaRunsContracts.cs`
- **Acceptance Criteria:**
  - RunStartRequest { moduleFilter?, levelFilter?, tagFilter?, profile, draftIds?[] }; RunStartResponse { runId }
  - TriageRequest {} (no auto-file flag); FileBugsRequest { failureIds[], storyId? }; FileBugsResponse { filedCount, bugIds }
  - TestFailureDto + From; TestRunSummary + From; TestRunList {Total,Items}; TestRunDetail + From(run, failures)
  - Namespace QaAutomation.Core.Contracts; compiles

#### QAL-034: Author QaPipelinesContracts DTOs
- **Status:** [ ]
- **ID:** QAL-034
- **Description:** Blueprint API → "Contracts: QaPipelinesContracts.cs". Watch list/add/patch, run-latest, preview-impact DTOs with From mapper.
- **File:** `backend/src/QaAutomation.Core/Contracts/QaPipelinesContracts.cs`
- **Acceptance Criteria:**
  - PipelineWatchDto + From(watch); AddWatchRequest { adoPipelineId, adoPipelineName, module }; UpdateWatchRequest { enabled?, module? }
  - RunLatestResponse { runId, buildId }; PreviewImpactRequest { changedFiles[], commitMessages[] }; PipelineWatchList {Total,Items}
  - Namespace QaAutomation.Core.Contracts; compiles

#### QAL-035: Implement QaLoopController
- **Status:** [ ]
- **ID:** QAL-035
- **Description:** Blueprint API → "QaLoopController" + route map. Thin [ApiController] [Route("api/qaloop")] mirroring AuthorTestsController: 202 on async start/push, 200 on sync ops, {total,items} paging, ProblemDetails (ADO→502, not-found→404, else→500). SSE reuses existing SseWriter.
- **File:** `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
- **Acceptance Criteria:**
  - POST /{projectId}/scan/start (202, seeds session, returns sessionId + existingSessionId warning when a non-terminal/recent session exists for (project,module))
  - GET /sessions/{sessionId}/stream (SSE, Last-Event-ID replay); PATCH /sessions/{sessionId}/analysis (full-replace); POST /sessions/{sessionId}/generate-drafts
  - PATCH /drafts/{draftId} (skip-null); POST /sessions/{sessionId}/approve-all; POST /sessions/{sessionId}/push (202); POST /drafts/{draftId}/push (sync); POST /sessions/{sessionId}/cancel
  - GET /sessions/{projectId} (paged list); GET /sessions/detail/{sessionId} (session + drafts)
  - Error mapping matches blueprint status codes; thin controller; compiles

#### QAL-036: Implement QaRunsController
- **Status:** [ ]
- **ID:** QAL-036
- **Description:** Blueprint API → "QaRunsController". [Route("api/qaloop/runs")]. Manual run start, list/detail, triage (draft-first, never files), user-gated file-bugs, preview-impact.
- **File:** `backend/src/QaAutomation.Api/Controllers/QaRunsController.cs`
- **Acceptance Criteria:**
  - POST /start (202, returns runId); GET /{runId}/stream (SSE Playwright stdout); GET /{projectId} (paged); GET /detail/{runId} (run + failures + triage)
  - POST /{runId}/triage persists one TestFailure per failure and serializes BugDraftJson for real_bug; NEVER files to ADO
  - POST /{runId}/file-bugs { failureIds[], storyId? } deserializes each BugDraftJson → CreateBugAsync (no re-triage), links Bug to its Test Case + storyId when supplied; 400 when no failures selected
  - POST /preview-impact invokes ImpactAnalyzerAgent for debugging
  - Error mapping ADO→502, not-found→404, else→500; compiles

#### QAL-037: Implement QaPipelinesController
- **Status:** [ ]
- **ID:** QAL-037
- **Description:** Blueprint API → "QaPipelinesController". [Route("api/qaloop/pipelines")]. Watch CRUD + run-latest manual trigger.
- **File:** `backend/src/QaAutomation.Api/Controllers/QaPipelinesController.cs`
- **Acceptance Criteria:**
  - GET /{projectId} (list watches); POST /{projectId} (add watch; 409 on duplicate pipeline via unique index)
  - PATCH /{watchId} (toggle Enabled / change Module / refresh metadata); DELETE /{watchId}
  - POST /{watchId}/run-latest (202, runs the poller path against the latest succeeded build, advances LastSeenBuildId, returns runId; 404 when watch/build missing, 502 on ADO fault)
  - Thin controller; compiles

### NOTIFICATIONS & SETTINGS & DI

#### QAL-038: Split IResultsIngestService + ResultsIngestService into Triage/FileBugs
- **Status:** [ ]
- **ID:** QAL-038
- **Description:** Blueprint PlaywrightRunner → "ResultsIngestService split". Split IngestAsync into TriageAsync (triage-persist, writes TestFailure + BugDraftJson, never files) and FileBugsAsync (deserialize BugDraftJson → CreateBugAsync, no second LLM call). Reuse FailureTriageAgent and the house-style body builders BuildReproHtml/BuildSystemInfo verbatim (byte-exact, golden parity).
- **File:** `backend/src/QaAutomation.Core/Abstractions/IResultsIngestService.cs`, `backend/src/QaAutomation.Infrastructure/Results/ResultsIngestService.cs`
- **Acceptance Criteria:**
  - IResultsIngestService exposes TriageAsync(...) → TriageResult and FileBugsAsync(...) → FileBugsResult
  - TriageAsync persists one TestFailure row per failure; for real_bug serializes the composed Bug (title/severity/preconditions/repro/expected/observations built via the reused builders) into BugDraftJson; never calls CreateBugAsync
  - FileBugsAsync reads each selected failure's BugDraftJson and calls CreateBugAsync with NO second LLM call, links Bug to Test Case + chosen Story, sets BugId; filed Bugs include a deep link to the ADO Test Run
  - FailureTriageAgent, JUnitParser, BuildReproHtml, BuildSystemInfo unchanged; the four classifications preserved; golden tests still pass

#### QAL-039: Define ITeamsNotifier abstraction
- **Status:** [ ]
- **ID:** QAL-039
- **Description:** Blueprint TeamsNotifier → "Abstraction". Notifier fired on run completion and on triage/file-bugs.
- **File:** `backend/src/QaAutomation.Core/Abstractions/ITeamsNotifier.cs`
- **Acceptance Criteria:**
  - interface ITeamsNotifier with NotifyRunCompleteAsync(run, ...) and NotifyTriageAsync(run, draftCount, filedBugIds)
  - Namespace QaAutomation.Core.Abstractions; compiles

#### QAL-040: Author 3 Teams card templates
- **Status:** [ ]
- **ID:** QAL-040
- **Description:** Blueprint TeamsNotifier → "Embedded templates". Three .json.template resources under Notifications/Templates/ (NOT under Prompts/). Requires a second EmbeddedResource glob in the Infrastructure .csproj since the Prompts glob does not cover them. Use the blueprint's literal glob pattern Notifications\Templates\*.json.template (line 4203).
- **File:** `backend/src/QaAutomation.Infrastructure/Notifications/Templates/teams_summary_compact.json.template`, `backend/src/QaAutomation.Infrastructure/Notifications/Templates/teams_summary_developer.json.template`, `backend/src/QaAutomation.Infrastructure/Notifications/Templates/teams_summary_scheduled.json.template`
- **Acceptance Criteria:**
  - compact: pass/fail counts + duration + report link (amended: Allure/activity — the report link is the Allure report `TestRun.ReportPath`, with Playwright/omit fallback per QAL-057)
  - developer: per-level breakdown, failed tests with AI impact reasoning, ADO Test Run link (amended: Allure/activity — also carries the Allure report link `TestRun.ReportPath` per QAL-057)
  - scheduled: greeting, environment context, vertical-pair results, per-level table, mailto to Mahmood
  - A second <EmbeddedResource Include="Notifications\Templates\*.json.template" /> glob added to the Infrastructure .csproj; all three build into the assembly

#### QAL-041: Implement TeamsNotifier
- **Status:** [ ]
- **ID:** QAL-041
- **Description:** Blueprint TeamsNotifier → "Implementation" + "Where it fires". Single configurable webhook (teams.webhook_url); active template selected by teams.template (default developer). Empty webhook → log and return silently. Always-notify default; skipped (Cancelled) runs do not notify.
- **File:** `backend/src/QaAutomation.Infrastructure/Notifications/TeamsNotifier.cs`
- **Acceptance Criteria:**
  - Loads the three embedded templates by name; selects active by teams.template setting (default developer)
  - Posts a card on every run completion (manual + pipeline) with status/counts/duration/profile/branch/commit + ADO Test Run link; pipeline runs add a collapsible "Why these tests?" with AI reasoning + confidence
  - Triage fires a card noting N real_bug drafts awaiting review with a /qa-runs link; after file-bugs fires again with the new ADO Bug ids + deep links
  - Empty teams.webhook_url → logs and returns silently, never fails/blocks a run; skipped runs do not notify
  - Singleton; compiles

#### QAL-042: Register 3 settings keys in AppSettingsService
- **Status:** [ ]
- **ID:** QAL-042
- **Description:** Blueprint TeamsNotifier/Settings → "AppSettingsService key registration". Add the three dotted keys to AllKeys + OptionKeys (+ SecretKeys for the webhook only); inject IOptionsMonitor<QaLoopOptions> and read the keys in OptionValue; evict the QaLoop options cache on update so DB values rebuild. RuntimeSettings is a key/value bag — do not add typed properties.
- **File:** `backend/src/QaAutomation.Infrastructure/Settings/AppSettingsService.cs`
- **Acceptance Criteria:**
  - teams.webhook_url (secret — masked ••••••••, with a teams.webhook_url.set companion bool), teams.template (default developer, plaintext), qaloop.poll_interval_minutes (default 5, plaintext) all registered
  - All three in OptionKeys so Effective routes them through typed options (DB-over-env); UpdateAsync persists to Settings table + calls ApplyAsync
  - OptionValue reads from the new IOptionsMonitor<QaLoopOptions>; _qaLoopCache.Clear() on update
  - GET /api/config returns the three keys with correct masking; no new Defaults branch needed (defaults live on QaLoopOptions)
  - (amended: Allure/activity — also register `qaloop.report_provider` + `qaloop.activity_enabled` in AllKeys + OptionKeys per QAL-058, same plaintext + IOptionsMonitor<QaLoopOptions> pattern)

#### QAL-043: Add .env bridge + RuntimeSettings overlay + AddQaLoop DI registration
- **Status:** [ ]
- **ID:** QAL-043
- **Description:** Blueprint TeamsNotifier/Settings → ".env additions" + "DependencyInjection.cs → AddQaLoop", and the manifest DI bullet. New private AddQaLoop(...) extension called from AddInfrastructure. Bridge flat .env (TEAMS_WEBHOOK_URL, TEAMS_TEMPLATE, QALOOP_POLL_INTERVAL_MINUTES) → QaLoopOptions via PostConfigure, then a RuntimeSettings.Get overlay PostConfigure AFTER so DB wins. Register orchestrator, runner, run-execution service, watcher, notifier, agents, options.
- **File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
- **Acceptance Criteria:**
  - IQaLoopOrchestrator→QaLoopOrchestrator (singleton); IPlaywrightRunner→PlaywrightRunner (singleton); RunExecutionService (scoped, uses DbContext); ITeamsNotifier→TeamsNotifier (singleton); PipelineWatcherHostedService via AddHostedService
  - The 5 agents registered transient/scoped to match existing agents; QaLoopOptions + DmsProfileOptions bound
  - Two-layer settings: .env PostConfigure first, RuntimeSettings.Get overlay PostConfigure after (DB-over-env)
  - AddQaLoop called from AddInfrastructure; background pipelines use their own DI scope; build succeeds; app starts
  - (amended: Allure/activity — the .env bridge + RuntimeSettings overlay also cover `QALOOP_REPORT_PROVIDER` + `QALOOP_ACTIVITY_ENABLED` → QaLoopOptions per QAL-058)

### FRONTEND

#### QAL-044: Generalize session types + sessionIdentity helper
- **Status:** [ ]
- **ID:** QAL-044
- **Description:** Blueprint Frontend → "SessionWorkspace generalization". Make workItemId/workItemTitle nullable on SessionSummary/SessionDetail, widen SessionModuleConfig.key to include 'qa-loop', add optional renderIdentity, tolerate module/inventoryJson on detail. Add sessionIdentity(config, s) fallback helper in session/shared.tsx.
- **File:** `client/src/session/types.ts`, `client/src/session/shared.tsx`
- **Acceptance Criteria:**
  - SessionSummary.workItemId/workItemTitle and SessionDetail.workItemId/workItemTitle are number|null / string|null
  - SessionModuleConfig.key includes 'qa-loop'; renderIdentity?(s) added; detail tolerates module?/inventoryJson?
  - sessionIdentity(config, s) returns config.renderIdentity(s) when set, else the #id — title fallback
  - tsc passes

#### QAL-045: Apply config-driven identity at the 4 hardcoded sites
- **Status:** [ ]
- **ID:** QAL-045
- **Description:** Blueprint Frontend → SessionWorkspace.tsx (3 sites: card, drawer header, analytics row) + SessionDetailFullScreen.tsx (4th site: header). Replace the inline #{id} — {title} with sessionIdentity(config, ...).
- **File:** `client/src/session/SessionWorkspace.tsx`, `client/src/session/SessionDetailFullScreen.tsx`
- **Acceptance Criteria:**
  - Card title, drawer header, and analytics row in SessionWorkspace.tsx use sessionIdentity(config, summary|session)
  - SessionDetailFullScreen.tsx header uses sessionIdentity
  - author-tests/find-bugs render unchanged (fallback); a no-story qa-loop session renders 'deals · Drafted · N drafts'
  - tsc passes

#### QAL-046: Rewrite api/qaLoop.ts
- **Status:** [ ]
- **ID:** QAL-046
- **Description:** Blueprint Frontend → "api/qaLoop.ts (rewritten)". Drop the old extract/generate/ingest surface; target /api/qaloop. One method per route; SSE via apiUrl. Reuse author-tests plan/suite/search pickers (module-agnostic).
- **File:** `client/src/api/qaLoop.ts`
- **Acceptance Criteria:**
  - startScan, listSessionsPage, getSessionDetail, saveAnalysis (full-replace PATCH), generateDrafts, patchDraft (skip-null), approveAllDrafts, pushSession, pushDraft, cancelSession, sessionStreamUrl
  - ScanStartRequest/Response, Surface/AnalysisDoc, ApiTestDraftDto types match the wire DTOs
  - getPlans/getSuites/searchWorkItems reuse /api/author-tests endpoints
  - tsc passes

#### QAL-047: Create api/qaRuns.ts and api/qaPipelines.ts
- **Status:** [ ]
- **ID:** QAL-047
- **Description:** Blueprint Frontend → "api/qaRuns.ts (new)" + "api/qaPipelines.ts (new)". Wrap the shared client.ts axios instance, one method per route; SSE URLs via apiUrl.
- **File:** `client/src/api/qaRuns.ts`, `client/src/api/qaPipelines.ts`
- **Acceptance Criteria:**
  - qaRuns: startRun, listRunsPage, getRunDetail, triage, fileBugs, previewImpact, runStreamUrl; RunStatus union and TestRunSummary/Detail/TestFailureDto types
  - qaPipelines: listWatches, addWatch, updateWatch, deleteWatch, runLatest; PipelineWatchDto type
  - tsc passes

#### QAL-048: Create qaLoopConfig SessionWorkspace config + scan form
- **Status:** [ ]
- **ID:** QAL-048
- **Description:** Blueprint Frontend → session/configs/qaLoop.tsx (new). Mirror configs/authorTests.tsx. Provides qaLoopConfig (key 'qa-loop', route /qa-loop, labels, moduleChip, pipelineSteps Scan→Review→Drafts→Push, activeStepIndex, analyticsColumns, listSessions/getDetail/streamUrl, renderIdentity), plus QaLoopStartForm, QaLoopDetailBody, CoverageMatrix, DraftsByLevel, and the push panel.
- **File:** `client/src/session/configs/qaLoop.tsx`
- **Acceptance Criteria:**
  - qaLoopConfig.renderIdentity packs 'module · status · N drafts' into the single identity line
  - QaLoopStartForm owns module + repo/branch + optional suite/story fields, POSTs scan/start, conforms to StartFormProps {onStarted,onError,onClose}
  - CoverageMatrix shows each surface covered/gap with a link to its existing Test Case (new tab), checkbox to deselect, a sticky footer 'N of M selected → will generate N' and a 'Deselect all covered' action; saves via full-replace PATCH /analysis
  - Drafts step has Contract/Endpoint/Workflow tabs with inline edit + spec-code preview; Push step picks Plan + Suite + optional Story and pushes all-Approved or per-row
  - Reuses PageHeader, AgentConsole+useEventStream, AgentPipeline/PipelineStepIcon, StatusChip, LiveActivityIndicator, StatTile/MiniDonut, EmptyState; tsc passes

#### QAL-049: Repoint QaLoopPage + create QaRunsPage and QaPipelinesPage
- **Status:** [ ]
- **ID:** QAL-049
- **Description:** Blueprint Frontend → pages summary. QaLoopPage.tsx becomes a one-liner repoint to SessionWorkspace with qaLoopConfig (old UI removed). QaRunsPage = launcher (module×level×tag×profile + Run) over a paged run-history grid with a per-run drawer (report links, AI reasoning, draft-first Bug filing). QaPipelinesPage = watch cards + Run latest.
- **File:** `client/src/pages/QaLoopPage.tsx`, `client/src/pages/QaRunsPage.tsx`, `client/src/pages/QaPipelinesPage.tsx`
- **Acceptance Criteria:**
  - QaLoopPage renders <SessionWorkspace config={qaLoopConfig} /> only
  - QaRunsPage: run launcher + paged grid; drawer shows Playwright report + ADO Test Run links, collapsible AI impact reasoning for pipeline runs, per-failure cards (classification/confidence/reason); real_bug rows carry a checkbox, user multi-selects + picks a Story + clicks File Bugs; Bug link appears after filing
  - QaPipelinesPage: one card per watch with enable Switch, last-poll/last-build/last-error, and a Run latest button; AddWatchDialog over addWatch; EmptyState when none
  - tsc + eslint pass

#### QAL-050: Wire routes/paths.ts, AppRoutes.tsx, Layout.tsx
- **Status:** [ ]
- **ID:** QAL-050
- **Description:** Blueprint Frontend → "Routing & nav wiring". Add qaRuns/qaPipelines to paths; register /qa-loop/:sessionId (+ epic parity route) full-screen routes in AppRoutes; add QA Runs + Pipelines nav items and the two in-shell page routes in Layout, repointing /qa-loop off the removed QaLoopPage UI to the new SessionWorkspace page.
- **File:** `client/src/routes/paths.ts`, `client/src/routes/AppRoutes.tsx`, `client/src/layout/Layout.tsx`
- **Acceptance Criteria:**
  - paths.ts adds qaRuns:'/qa-runs' and qaPipelines:'/qa-pipelines'
  - AppRoutes registers /qa-loop/:sessionId and /qa-loop/epic/:batchId (parity) full-screen routes per-config (not looped)
  - Layout navItems gains QA Runs + Pipelines beside QA Loop; in-shell <Route> entries for paths.qaLoop, paths.qaRuns, paths.qaPipelines; the always-visible open-full-screen button no longer dead-ends on Coming soon
  - tsc + eslint pass

### TESTING

#### QAL-051: Write QaLoopOrchestratorTests
- **Status:** [ ]
- **ID:** QAL-051
- **Description:** Manifest QaLoopOrchestratorTests.cs (new) + blueprint orchestrator contracts. Cover the scan→analyze→generate→push lifecycle edges and the selected-only / idempotent-re-push / tag-rewrite invariants.
- **File:** `backend/src/QaAutomation.Tests/QaLoopOrchestratorTests.cs`
- **Acceptance Criteria:**
  - Asserts Analyzing→AwaitingReview (default) and Analyzing→Drafting when AutoHandoff=1
  - generate-drafts emits a draft only for selected (non-covered) surfaces; per-draft generator error lands on ApiTestDraft.ErrorMessage without failing the batch
  - Push rewrites [<tcId-pending>] → real [id] and sets AdoTestCaseId/Status=Pushed; LinkTestedBy called only when WorkItemId set; re-press Push retries only non-Pushed drafts (Pushing→Drafted on partial failure)
  - xUnit; runs green via dotnet test --filter FullyQualifiedName~QaLoopOrchestratorTests

#### QAL-052: Write ImpactAnalyzerAgentTests
- **Status:** [ ]
- **ID:** QAL-052
- **Description:** Manifest ImpactAnalyzerAgentTests.cs (new). Verify the agent's JSON parsing/output contract and the malformed-JSON behavior the watcher relies on.
- **File:** `backend/src/QaAutomation.Tests/ImpactAnalyzerAgentTests.cs`
- **Acceptance Criteria:**
  - Valid JSON → ImpactAnalysis with impactedDraftIds/reasoning/confidence parsed; empty ids → skippedReason populated
  - Inventory projection excludes SpecCode (only id/module/level/title/sourceRoutes/tags sent)
  - Non-object/malformed JSON → throws AgentException (so the watcher can apply its retry/fallback)
  - xUnit; runs green via the matching --filter

#### QAL-053: Write JUnitTriageSplitTests + watcher ProcessBuild test
- **Status:** [ ]
- **ID:** QAL-053
- **Description:** Manifest JUnitTriageSplitTests.cs (new) + blueprint Pipeline Watcher → "Verification". Cover the triage/file-bugs split (draft-first) and the watcher's ProcessBuildAsync with a fake IAzureDevOpsClient + stub ImpactAnalyzerAgent.
- **File:** `backend/src/QaAutomation.Tests/JUnitTriageSplitTests.cs`
- **Acceptance Criteria:**
  - TriageAsync writes one TestFailure per failure and a BugDraftJson for real_bug but files nothing; FileBugsAsync deserializes BugDraftJson → CreateBugAsync with no second LLM call and sets BugId
  - Watcher: empty ImpactedDraftIds → one TestRun Status=Cancelled, no EnqueueAsync; malformed-JSON stub → ImpactAnalysisJson fallback==true and ImpactedDraftIds == every Pushed draft in the module; non-empty → EnqueueAsync once with the agent ids and LastSeenBuildId==build.Id
  - xUnit; runs green via the matching --filter

### BUILD & VERIFY

#### QAL-054: Full build, lint, test + golden parity + runtime smoke
- **Status:** [ ]
- **ID:** QAL-054
- **Description:** Blueprint Build/Run/Migrate + Reference Documents. Final gate. dotnet is not on PATH — use the wrapper from the dotnet-build-environment memory. Confirm the migration auto-applies, the existing byte-exact ADO golden tests still pass (new ADO methods are additive only), and both operating modes work end-to-end.
- **File:** `backend/QaAutomation.sln`, `client/package.json`
- **Acceptance Criteria:**
  - dotnet build succeeds; dotnet test all green INCLUDING the existing AdoGoldenTests (CreateTestCaseAsync/CreateBugAsync bodies unchanged) and the 3 new test classes
  - Backend starts on :5250; DatabaseInitializer applies AddQaLoopReshape and creates all 5 tables on a stock/wiped qa.db; Swagger lists the /api/qaloop routes
  - npm run build (tsc -b && vite build) and npm run lint both pass; the three new nav items route correctly and no open-full-screen dead-ends
  - Mode A: scan deals → coverage matrix → deselect covered → generate drafts → push creates ADO Test Cases in house style and rewrites [tcId] on disk; Mode B: run-latest on Lead QA #175 → ImpactAnalyzer → Playwright run vs INFINITY → per-Test-Case ADO Test Run Pass/Fail → draft-first Bugs → Teams card
  - git status inside the Your Repo repo shows no changes after any QA Loop action (CRM repo read-only); never leave a failing build

### ALLURE REPORTING & ACTIVITY TOGGLE (amendment)

> Additive amendment phase (solution.md "## Allure Reporting" + "## Activity Capture", blueprint amendments). Adds a Node-native Allure HTML report per run gated by `qaloop.report_provider` (allure default / playwright / both), server-side per-(module,pipeline) trend history, API-level-only attachments authored into the generated specs, and a global `qaloop.activity_enabled` toggle that suppresses verbose streaming + heavy EventLogJson per row. junit + json ALWAYS emit regardless of provider (triage / ADO Test Run / bug drafting depend on them, untouched). A broken report never breaks a run.

#### QAL-055: Allure runtime deps + per-run reporter wiring
- **Status:** [ ]
- **ID:** QAL-055
- **Description:** solution "## Allure Reporting" decisions 1+2+5. Add the Node-native Allure toolchain to the shared playwright-runtime and wire the per-run reporter list. The runtime `package.json`/lockfile gain `allure-playwright` (the Playwright reporter) + a Node-native Allure CLI (latest stable that runs on Node with NO Java dependency); `EnsureRuntimeAsync`/startup provisioning adds an `npx allure --version` capability check alongside the existing `npx playwright install`. The per-run `playwright.config.ts` reporter list adds `allure-playwright` (resultsDir `runs/{runId}/allure-results`), keeps `junit` + `json` always-on, and gates the Playwright `html` reporter on `qaloop.report_provider ∈ {playwright, both}`. NO `use:` capture block is added (API-level only; no screenshot/video/trace). Amends QAL-025 (PlaywrightRunner config generation + runtime provisioning).
- **File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`, `backend/data/playwright-runtime/package.json` (+ lockfile) and the per-run `playwright.config.ts` template asset
- **Acceptance Criteria:**
  - playwright-runtime `package.json` + lockfile add `allure-playwright` and a Node-native Allure CLI; startup/`EnsureRuntimeAsync` runs `npx allure --version` and logs the resolved version (capability check), provisioned once with the existing `npm ci` + `npx playwright install`
  - Per-run `playwright.config.ts` reporter list = `junit` + `json` always-on + `allure-playwright` (resultsDir `runs/{runId}/allure-results`) + `html` ONLY when `report_provider ∈ {playwright, both}`; provider=allure omits ONLY the Playwright `html` reporter
  - NO `use:` capture block in the generated config (no screenshot/video/browser-trace anywhere)
  - QAL-055 VERIFIES the exact Node Allure package + version and that it supports the playwright reporter + single-file + trends; documented FALLBACK (bundled JRE + Allure 2) recorded if a capability is missing
  - Build/runtime succeeds; junit + json still emit on every run unchanged

#### QAL-056: Allure context files (environment / categories / executor)
- **Status:** [ ]
- **ID:** QAL-056
- **Description:** solution "## Allure Reporting" decision 6. Before `allure generate`, write the three Allure context files into `runs/{runId}/allure-results/`: `environment.properties` (profile, branch, commit, build id, trigger source, module, ADO Test Run id), `categories.json` (defect buckets mapped to the triage taxonomy real_bug/flaky/test_issue/stale_test), and `executor.json` (deep links to the ADO build + ADO Test Run). Sourced from TestRun fields (Profile, TriggerSource, TriggerBuildId, TriggerCommitSha, Module, AdoTestRunId) already resolved by RunExecutionService.
- **File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`, `backend/src/QaAutomation.Infrastructure/QaRuns/RunExecutionService.cs`
- **Acceptance Criteria:**
  - `environment.properties` carries profile, branch, commit, build id, trigger source, module, ADO Test Run id
  - `categories.json` buckets map 1:1 to the FailureClassification taxonomy (real_bug / flaky / test_issue / stale_test)
  - `executor.json` carries deep links to the ADO build + ADO Test Run (omits the link cleanly when the ADO Test Run was skipped)
  - All three written into `allure-results/` before generate; missing optional fields degrade gracefully (no crash); compiles

#### QAL-057: Allure generate single-file + trends + ReportPath + fallback
- **Status:** [ ]
- **ID:** QAL-057
- **Description:** solution Runner step 4 + "## Allure Reporting" decisions 3+4+7. After the Playwright run, when `report_provider ∈ {allure, both}`: seed `allure-results/history/` from the persisted per-(module, pipeline) history store, run Node-native `allure generate --single-file`, set `TestRun.ReportPath` to the single-file `.html`, then copy `history/` back to the server-side store so cross-run trend continuity survives. Single-file/trends resolution per decision 4: generate WITH history seeded so trend DATA is baked into the single file; if QAL-057 verification shows the single-file build cannot render the trend widget, ReportPath falls back to the folder `index.html` + a zip download (trends never silently lost). Generate failure → run still passes/fails normally; ReportPath falls back to the Playwright `html/index.html` if present else null + a warning on `TestRun.ErrorMessage`. Amends QAL-025/QAL-028 (persist-outputs + ReportPath resolution).
- **File:** `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`, `backend/src/QaAutomation.Infrastructure/QaRuns/RunExecutionService.cs`
- **Acceptance Criteria:**
  - History store keyed by (module, pipeline) under DataDirectory; seeded into `allure-results/history/` before generate and copied back after, so trends accumulate cross-run server-side regardless of report format
  - `allure generate --single-file` produces a single `.html`; `TestRun.ReportPath` = that single file (open-in-tab AND download serve the same file) when provider ∈ {allure, both}
  - Decision-4 resolution: trend data seeded so it bakes into the single file; if verification shows the single-file build cannot render the trend widget, ReportPath falls back to folder `index.html` + zip download (documented)
  - When provider=playwright, ReportPath = Playwright `html/index.html` (Allure not generated)
  - `allure generate` failure does NOT fail the run; ReportPath falls back (Playwright `html` if present else null) + warning appended to `TestRun.ErrorMessage`; counts/triage/ADO Test Run unaffected
  - Compiles; a broken report never breaks a run

#### QAL-058: Settings report_provider + activity_enabled (both keys end-to-end)
- **Status:** [ ]
- **ID:** QAL-058
- **Description:** solution "## Allure Reporting" decision 2 + "## Activity Capture" decision 8. Add the two new dotted settings keys end-to-end through the two-layer settings pipeline (mirrors QAL-042/043 wiring for the existing three keys). QaLoopOptions gains `ReportProvider` (default `allure`) + `ActivityEnabled` (default `true`); `.env` bridges `QALOOP_REPORT_PROVIDER` / `QALOOP_ACTIVITY_ENABLED` → QaLoopOptions via PostConfigure, then a RuntimeSettings overlay PostConfigure AFTER (DB-over-env); AppSettingsService registers `qaloop.report_provider` + `qaloop.activity_enabled` in AllKeys + OptionKeys (read via IOptionsMonitor<QaLoopOptions>, _qaLoopCache.Clear() on update); GET /api/config returns both. Amends QAL-004 (QaLoopOptions props), QAL-042 (AppSettingsService keys), QAL-043 (.env bridge + overlay).
- **File:** `backend/src/QaAutomation.Core/Configuration/QaLoopOptions.cs`, `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`, `backend/src/QaAutomation.Infrastructure/Settings/AppSettingsService.cs`
- **Acceptance Criteria:**
  - QaLoopOptions adds `ReportProvider` (default `allure`; values allure|playwright|both) and `ActivityEnabled` (default `true`)
  - `.env` keys `QALOOP_REPORT_PROVIDER` + `QALOOP_ACTIVITY_ENABLED` bridge into QaLoopOptions via PostConfigure; RuntimeSettings overlay PostConfigure runs AFTER so a saved DB value wins
  - `qaloop.report_provider` + `qaloop.activity_enabled` registered in AppSettingsService AllKeys + OptionKeys (both plaintext, defaults live on QaLoopOptions); read in OptionValue via IOptionsMonitor<QaLoopOptions>; `_qaLoopCache.Clear()` on update
  - GET /api/config returns both keys with their effective values (DB-over-env); UpdateAsync persists + calls ApplyAsync
  - Compiles; existing three keys unaffected

#### QAL-059: Allure-rich generated specs (generator prompt amendments)
- **Status:** [ ]
- **ID:** QAL-059
- **Description:** solution "## Allure Reporting" decision 5. Amend the three `*_generator_system.txt` prompts so every generated spec wraps each HTTP call in `test.step` + `allure.attachment` and carries Allure metadata — API-level capture authored INTO the spec, not UI testing (no `use:` block anywhere). Each call attaches: request (method/url/headers with auth redacted/payload), response (status code/headers/body), schema-validation diff (Contract level), assertion expected-vs-actual, and latency as an Allure parameter. Metadata: epic=module, feature=level (Contract/Endpoint/Workflow), story=route/flow, severity, tags, and the `[tcId]` as an `allure.link` to the ADO Test Case. Amends QAL-012 (prompt bodies) + QAL-015/016/017 (the generators that consume them) — prompt-body-only change, generator C# unchanged.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/Prompts/contract_generator_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/endpoint_generator_system.txt`, `backend/src/QaAutomation.Infrastructure/Agents/Prompts/workflow_generator_system.txt`
- **Acceptance Criteria:**
  - Each generator prompt instructs the emitted spec to wrap every call in `test.step` and attach request (method/url/headers[auth redacted]/payload) + response (status/headers/body) + assertion expected-vs-actual via `allure.attachment`, with latency as an Allure parameter
  - contract_generator additionally attaches the schema-validation diff (Contract level)
  - Each prompt adds Allure metadata: epic=module, feature=level, story=route/flow, severity, tags, and `[tcId]` as an `allure.link` to the ADO Test Case
  - The existing `[<tcId-pending>]` title-tag instruction (QAL-012) is preserved; NO `use:` capture block; the three prompts stay EmbeddedResource and build into the assembly
  - contract/endpoint/workflow `_testcase_system.txt` (house-style) prompts UNCHANGED (Mahmood voice / golden parity untouched)

#### QAL-060: ActivityEnabled persistence + streaming gate
- **Status:** [ ]
- **ID:** QAL-060
- **Description:** solution "## Activity Capture" decisions 9+10+11+13. Add `ActivityEnabled bool` (default true) to the ApiTestSession + TestRun entities + their QaDbContext mapping, folded into the SAME AddQaLoopReshape migration (migration column DEFAULT 1 so existing rows always show their activity). Capture the flag from `qaloop.activity_enabled` at row creation. When false for a NEW row: QaLoopOrchestrator (sessions) + PlaywrightRunner/RunExecutionService (runs) SUPPRESS verbose streaming (agent token-level ConsoleEvents for sessions; Playwright stdout for runs) AND skip heavy EventLogJson persistence, but STILL emit coarse phase/status transitions so step-state + completion still work. When true (or any existing row with stored EventLogJson): behaviour unchanged. Amends QAL-006/007 (entity columns), QAL-009 (mapping), QAL-010 (migration), QAL-021/022 (orchestrator gate), QAL-025/028 (runner/run-exec gate).
- **File:** `backend/src/QaAutomation.Core/Models/ApiTestSession.cs`, `backend/src/QaAutomation.Core/Models/TestRun.cs`, `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs`, `backend/src/QaAutomation.Infrastructure/Data/Migrations/<ts>_AddQaLoopReshape.cs`, `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`, `backend/src/QaAutomation.Infrastructure/QaRuns/PlaywrightRunner.cs`, `backend/src/QaAutomation.Infrastructure/QaRuns/RunExecutionService.cs`
- **Acceptance Criteria:**
  - ApiTestSession + TestRun gain `ActivityEnabled bool` (entity default true); QaDbContext maps the column; column folded into AddQaLoopReshape with DEFAULT 1 (NOT a separate migration) so existing/back-filled rows render their activity
  - Each new row captures `ActivityEnabled` from the `qaloop.activity_enabled` setting at creation time
  - ActivityEnabled=false NEW row: orchestrator suppresses agent token-level ConsoleEvents + EventLogJson persistence; runner/run-exec suppresses Playwright stdout streaming + EventLogJson; BOTH still emit coarse phase/status transitions (step-state + completion signal intact)
  - ActivityEnabled=true (or any row with stored EventLogJson) behaves exactly as today (live SSE while running; EventLogJson replay when done)
  - Honest scope note holds: sessions save LLM-generation tokens + storage/bandwidth; runs save bandwidth/clutter/storage only (Playwright stdout, no LLM); applied globally
  - Migration auto-applies on startup; compiles

#### QAL-061: FE Allure report + activity swap + Settings UI
- **Status:** [ ]
- **ID:** QAL-061
- **Description:** solution "## Activity Capture" decision 12 + Frontend Layout amendments (UI/UX 14+15+16). Surface the new backend signals in the FE. Add `activityEnabled` to the session + run DTOs so the FE picks AgentConsole vs indeterminate progress (`reportPath` already exists on run DTOs). The /qa-runs run-detail drawer gains an "Allure report" open-in-new-tab button + a "Download" button (same single-file html) conditional on `reportPath` + provider, with a muted "report unavailable" state when generate failed; the existing "Playwright report" button becomes conditional on provider ∈ {playwright, both}. AgentConsole ↔ indeterminate LinearProgress/LiveActivityIndicator + step pills swaps per `activityEnabled`. Settings page adds a `report_provider` SELECT (Allure default / Playwright / Both) and an `activity_enabled` SWITCH alongside the existing teams.webhook_url / teams.template / qaloop.poll_interval_minutes (real select + switch controls, not text fields). Optional minor run-history grid "report available" indicator. Amends QAL-032/033 (DTOs), QAL-049 (QaRunsPage drawer), QAL-048 (qaLoopConfig console swap), plus the Settings page.
- **File:** `client/src/pages/QaRunsPage.tsx`, `client/src/session/configs/qaLoop.tsx`, `client/src/session/SessionWorkspace.tsx`, `client/src/pages/SettingsPage.tsx`, `client/src/api/qaRuns.ts`, `client/src/api/qaLoop.ts`, `backend/src/QaAutomation.Core/Contracts/QaLoopContracts.cs`, `backend/src/QaAutomation.Core/Contracts/QaRunsContracts.cs`
- **Acceptance Criteria:**
  - `activityEnabled` added to ApiSessionSummary/ApiSessionDetail + TestRunSummary/TestRunDetail DTOs (BE + matching FE types); `reportPath` already present on run DTOs
  - /qa-runs drawer shows "Allure report ↗" (open single-file in new tab) + "Download ⤓" (same html) conditional on `reportPath` + provider ∈ {allure, both}; muted "report unavailable" state when generate failed; "Playwright report ↗" conditional on provider ∈ {playwright, both}
  - FE renders AgentConsole when `activityEnabled` (or stored EventLogJson present) else an indeterminate LinearProgress/LiveActivityIndicator + step pills — both in QaRunsPage and the qa-loop session views
  - Settings page renders a `report_provider` SELECT (Allure / Playwright / Both) and an `activity_enabled` SWITCH (real MUI Select + Switch, not text fields) next to the existing teams.webhook_url / teams.template / qaloop.poll_interval_minutes fields
  - Optional small "report available" indicator on the run-history grid (minor)
  - tsc + eslint pass

---

## Success Metrics

- Both operating modes work end-to-end: (A) module scan → coverage matrix → deselect-covered → generate Contract/Endpoint/Workflow drafts → push; (B) pipeline-driven impacted run on Lead QA #175 via watcher poll and Run-latest
- ADO Test Cases and draft-first Bugs are created in Mahmood's house style (golden-tested CreateTestCaseAsync/CreateBugAsync bodies unchanged)
- Each ADO_Tester execution creates a fresh ADO Test Run and marks every executed Test Case Pass/Fail in the chosen Plan/Suite; ADO Test Run skipped (not failed) when plan/suite unknown
- ImpactAnalyzerAgent picks the impacted draft subset with persisted reasoning/confidence; malformed JSON twice → module-wide fallback with fallback=true
- Bugs are never auto-filed: triage writes BugDraftJson; user multi-selects + picks a Story; file-bugs calls CreateBugAsync with no second LLM call
- [<tcId-pending>] convention round-trips: generators inject it, push rewrites it to real [id] on disk, JUnit \[(\d+)\] maps results back to Test Cases; only Pushed drafts are run-eligible
- Playwright runtime installed ONCE (read-only at run time); spawns serialized through PipelineConcurrency; AUTOMATION_PROFILE=infinity on child env
- Re-pressing Push is idempotent (retries only non-Pushed drafts); EF AddQaLoopReshape migration auto-applies on startup and adds exactly 5 additive tables
- Teams card fires on run completion (with AI reasoning for pipeline runs) and on triage/file-bugs; empty webhook logs-and-skips silently
- backend dotnet build + dotnet test green (incl. byte-parity golden tests + 3 new test classes); client npm run build + npm run lint green; Your Repo repo shows no git changes

---

## Notes

- Critique verdict was COMPLETE with an empty missing[] list: every draft task is preserved and nothing new needed folding in. The merge applied only the critique's minor fixes: (1) Teams-templates glob now uses the blueprint's literal *.json.template; (2) AdoBuildChange called out as the canonical name (not AdoBuildCommit from a stray blueprint snippet); (3) the two cross-phase file edits (IAzureDevOpsClient + AzureDevOpsClient, touched in both RUNNER and WATCHER) flagged as purely additive appends; (4) TestRun.ErrorMessage column made explicit in the entity (referenced by RunExecutionService skip/warning paths).
- Follow the blueprint EXACTLY — it carries full code, exact column lists, JSON shapes, route signatures, and status codes. solution.md v1.3 is authoritative; do not contradict it. When the blueprint shows code, type it as written (class names, namespaces, file paths per the Project Structure manifest and Canonical naming table).
- Byte-exact ADO write parity is load-bearing (CLAUDE.md): the new ADO methods (test-run lifecycle, test points, build reads) are ADDITIVE only — never touch CreateTestCaseAsync/CreateBugAsync bodies, Steps XML, ReproSteps/AC/SystemInfo HTML, or json-patch op order. Run the golden tests after any AzureDevOpsClient change.
- dotnet is NOT on PATH on this machine — use the wrapper/invocation from the dotnet-build-environment memory before any dotnet build/test/ef command. Stop the backend before generating the EF migration (SQLite file lock).
- EF migration auto-applies on next backend start via DatabaseInitializer — there is NO dotnet ef database update step. You only generate AddQaLoopReshape; do not hand-edit the generated migration.
- Reuse, don't rebuild: FailureTriageAgent, JUnitParser, and the ResultsIngestService body builders (BuildReproHtml/BuildSystemInfo) are reused verbatim — the ONLY ResultsIngestService change is splitting IngestAsync into TriageAsync + FileBugsAsync. Frontend reuses PageHeader/AgentConsole/AgentPipeline/StatusChip/StatTile/MiniDonut/EmptyState/SessionWorkspace — no new shared abstractions.
- Background pipelines copy the AgentOrchestrator skeleton: own DI scope via IServiceScopeFactory (never the request scope), IEventBus→SSE with Last-Event-ID replay, durable EventLogJson, CancellationRegistry, global PipelineConcurrency cap.
- Two-layer settings per CLAUDE.md: register each new dotted key (teams.webhook_url, teams.template, qaloop.poll_interval_minutes) in BOTH the .env→Options PostConfigure bridge AND AppSettingsService; add a RuntimeSettings.Get overlay PostConfigure AFTER so DB wins. RuntimeSettings is a key/value bag — do not add typed properties.
- Phase order is dependency-correct (Foundation → Data → Agents → Orchestrator → Runner → Watcher → API → Notifications/Settings/DI → Frontend → Testing → Build/Verify); work one task per iteration in phase order, update progress.md after each verified task, never leave a failing build. Note: IAzureDevOpsClient + AzureDevOpsClient are each edited in TWO phases (RUNNER adds 3 test-run/test-point methods, WATCHER adds 2 build-read methods) — both additive; each method's only consumer lives in its own phase. AdoBuild/AdoBuildChange are defined in FOUNDATION and first used in WATCHER (definition-before-use, do not defer the Foundation task).
- Two manifest files live outside the main Project Structure block but are required and covered here: AdoModels.cs (+AdoBuild/AdoBuildChange) and the second EmbeddedResource glob in the Infrastructure .csproj for Notifications/Templates/*.json.template. Verify the existing Prompts/*.txt glob covers the 7 new prompt files.
- Deferred/out-of-scope (do NOT build): UI-level tests (ui/ folder reserved, TestLevel.Ui reserved), modules other than Deals, pipelines other than #175, DMS profiles other than INFINITY (registry only), sync-to-product-repo, run cancellation, and converting agents to Claude-CLI skills (agents stay provider-portable single-shot completion calls).
