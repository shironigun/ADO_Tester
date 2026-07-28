# PRD: Author Tests Enhancements — Free-Text Context, Code Discovery, Refine Loops, Existing-Test Dedup

## Project Overview
The Author Tests page today can only feed the pipeline a work-item id; the Analyst runs on ticket
text + knowledgebase with **zero code awareness** and there is no plain "here's extra context" channel
or mid-run steering. This effort layers four capabilities onto the existing
`Fetch → Retrieve KB → Analyst → Tester` pipeline, delivered in three independently-shippable phases:
**(1) Free-text context + refine loops** — a free-text "Additional context" box at start (→ Analyst)
and an "Extra Tester context" box at the Analyst→Tester gate (→ Tester); an **Analyst refine** that
re-runs the Analyst with reviewer feedback to fill gaps in the *existing* analysis; and a **Tester
refine** that MERGES feedback into existing drafts (update flagged, add new, remove only explicitly
flagged, never touch pushed). **(2) Code discovery** — a new *Discover code* pipeline step backed by a
shared `ICodeContextService` (extracted-in-spirit from the bug pipeline's anchored-context engine)
that backtracks the feature through the ADO repo from linked PRs/commits + free-text route/screen
hints (`alwaysSearch:true` for thin tickets) and feeds real code into the Analyst. **(3) Existing-test
dedup** — a cached plan scan/index (`IExistingTestsIndexService`) over a reference Test Plan/suite so
the Tester produces only net-new/complementary coverage, enforced at two layers (prompt-level existing
titles + title-pattern summary; post-gen similarity check that auto-drops clear duplicates and flags
borderline ones), persisted to SQLite with an FTS5 mirror (`test_case_index`), a 24h staleness TTL, and
a manual Rescan. It adds 6 `TestSession` columns, 2 new EF entities + 1 FTS5 virtual table (one
migration), 1 new event phase (`dedup`), 2 shared services, 2 agent refine overloads + 4 prompt
sections, 4 new controller endpoints, and the matching React surface (pipeline step insert, three
free-text boxes, reference-plan pickers, Analyst/Tester refine UI, dedup + code-context result cards).
The pipeline becomes `Fetch → Retrieve KB → Discover code → Analyst → (pause) → Tester`. Byte-exact ADO
write parity and the frozen agent system prompts are preserved — all refine/injection instructions live
in the **user** prompt only; `BugOrchestrator` behavior is unchanged.

## Created
- **Date:** 2026-07-28
- **Author:** Mahmood Ahmad + Claude
- **Branch:** `claude/chat-session-flzhjh`
- **Companion docs:** `solution.md` (design) · `blueprint.md` (implementation guide) · `progress.md` (status + log)

## Status legend
- **`[x]` VERIFIED** — implemented and verified green **in this environment** (frontend `tsc`/`vite build`/`eslint`, or trivially verifiable docs).
- **`[~]` CODE-COMPLETE** — implemented and pushed; backend `dotnet build`/`dotnet test`/migration **pending on the user's local machine** (no .NET SDK in the container, per the user's standing "I'll verify locally").
- **`[ ]` TODO** — not yet done.

---

## Tasks

### PHASE 1 — FREE-TEXT CONTEXT + REFINE LOOPS

#### ATE-001: Add context + reference-plan columns to TestSession
- **Status:** [~]
- **ID:** ATE-001
- **Description:** solution.md Data Design → "TestSession new columns". Add the six nullable persistence fields that carry per-run guidance and the reference plan through the background pipeline. Context fields mirror `BugSession.ActualBehavior`/`CodeContext`.
- **File:** `backend/src/QaAutomation.Core/Models/TestSession.cs`
- **Acceptance Criteria:**
  - `AdditionalContext`, `CodeContext`, `TesterContext` (all `string?`)
  - `ReferenceTestPlanId`, `ReferenceTestSuiteId` (`int?`), `PlanNavigationHint` (`string?`)
  - Existing columns untouched; compiles

#### ATE-002: Add AuthorRunContext record
- **Status:** [~]
- **ID:** ATE-002
- **Description:** solution.md API Design. A small immutable carrier so the orchestrator surface takes one optional context arg instead of six loose params; a null context reproduces pre-enhancement behavior.
- **File:** `backend/src/QaAutomation.Core/Models/AuthorRunContext.cs`
- **Acceptance Criteria:**
  - `sealed record AuthorRunContext(string? AdditionalContext = null, int? ReferenceTestPlanId = null, int? ReferenceTestSuiteId = null, string? PlanNavigationHint = null)`
  - All fields default null; compiles

#### ATE-003: Extend AuthorTestsContracts DTOs
- **Status:** [~]
- **ID:** ATE-003
- **Description:** solution.md API Design → "Contracts". `StartRequest` gains the context + reference-plan fields; new `RunTesterRequest` (the gate endpoint took no body before), `RerunAnalystRequest`, `RefineTesterRequest`/`TestFeedback`; `TestSessionDetail` gains the six new fields (+ mappers) so the UI re-seeds.
- **File:** `backend/src/QaAutomation.Core/Contracts/AuthorTestsContracts.cs`
- **Acceptance Criteria:**
  - `StartRequest` += `AdditionalContext?`, `ReferenceTestPlanId?`, `ReferenceTestSuiteId?`, `PlanNavigationHint?`
  - `RunTesterRequest { AdditionalContext?; ReferenceTestPlanId?; ReferenceTestSuiteId?; PlanNavigationHint? }` (empty body = old behavior)
  - `RerunAnalystRequest { string Feedback }`; `RefineTesterRequest { string? OverallFeedback; List<TestFeedback> PerTest }`; `TestFeedback { int DraftId; string? Feedback; bool Remove }`
  - `TestSessionDetail` += the six fields, populated in `From(...)`; camelCase wire; compiles

#### ATE-004: Extend IAgentOrchestrator surface
- **Status:** [~]
- **ID:** ATE-004
- **Description:** solution.md API Design. `StartAsync`/`RunTesterAsync` take an optional `AuthorRunContext`; add `RerunAnalystAsync(sessionId, feedback, ct)` and `RefineTesterAsync(sessionId, overallFeedback, perTest, ct)`.
- **File:** `backend/src/QaAutomation.Core/Abstractions/IAgentOrchestrator.cs`
- **Acceptance Criteria:**
  - `StartAsync(projectId, workItemId, autoHandoff, AuthorRunContext? context, batchId?, ct)`
  - `RunTesterAsync(sessionId, AuthorRunContext? context, ct)`
  - `RerunAnalystAsync(sessionId, string feedback, ct)`; `RefineTesterAsync(sessionId, string? overallFeedback, IReadOnlyList<TestFeedback> perTest, ct)`
  - Compiles; existing members unchanged

#### ATE-005: AnalystAgent prompt injection (additionalContext + codeContext)
- **Status:** [~]
- **ID:** ATE-005
- **Description:** solution.md Agent Design → "labeled-section pattern". `BuildUserPrompt` gains `additionalContext` + `codeContext` params appending `## Additional Context (from tester)` and `## Code Context (snippets from the repo)` — same shape as `BugWriterAgent`. Embedded `AnalystSystem` resource is NOT touched.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/AnalystAgent.cs`
- **Acceptance Criteria:**
  - Sections appended only when the corresponding text is non-empty
  - System prompt (embedded resource) unchanged (frozen-prompt guardrail)
  - Compiles

#### ATE-006: AnalystAgent refine overload
- **Status:** [~]
- **ID:** ATE-006
- **Description:** solution.md Agent Design → "Analyst refine". `RefineAsync(prior, feedback, wi, context, codeContext, …)` builds a user prompt with the prior `AnalystOutput` JSON + `## Reviewer Feedback (refine, do not restart)`, asking it to improve/fill gaps while keeping the same output shape. Returns `AnalystOutput`.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/AnalystAgent.cs`
- **Acceptance Criteria:**
  - Prior analysis + feedback fed back in; output shape identical to the initial run
  - Refine instructions in the USER prompt only; compiles

#### ATE-007: TesterAgent prompt injection (testerContext + existingTestsBlock)
- **Status:** [~]
- **ID:** ATE-007
- **Description:** solution.md Agent Design + Dedup. `BuildUserPrompt` gains a `testerContext` param → `## Extra Context (from tester)` and an `existingTestsBlock` param → `## Existing Test Cases in ADO (do NOT duplicate)`, alongside the existing Open-Questions channel. Instruction: produce only net-new/complementary coverage; may cite an existing TC id.
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/TesterAgent.cs`
- **Acceptance Criteria:**
  - Both sections appended only when non-empty; Open-Questions channel preserved
  - System prompt unchanged; compiles

#### ATE-008: TesterAgent refine overload
- **Status:** [~]
- **ID:** ATE-008
- **Description:** solution.md Agent Design → "Tester refine". `RefineAsync(analysis, wi, existingTests, overallFeedback, testerContext, existingTestsBlock, …)` renders each existing test with a stable `ref` + status (Pushed = read-only) + its per-test feedback + overall feedback + the dedup block. Returns the revised array where each item carries `ref` (existing) or `"new"`; parser tolerates an optional `ref`/`id` key (like the existing `StrOr` extraction).
- **File:** `backend/src/QaAutomation.Infrastructure/Agents/TesterAgent.cs`
- **Acceptance Criteria:**
  - Pushed tests marked read-only in the prompt
  - `ref` is an additive tolerated key; initial-run output shape untouched
  - Compiles

#### ATE-009: AgentOrchestrator — context wiring + refine methods + ReconcileDrafts
- **Status:** [~]
- **ID:** ATE-009
- **Description:** solution.md Orchestrator. `StartAsync` persists context to the session; `RunTesterAsync` persists Tester context + reference plan and injects them; `RerunAnalystAsync` reloads prior `AnalysisJson`, calls the Analyst refine overload, writes back, returns to `AwaitingTester`; `RefineTesterAsync` loads drafts, calls the Tester refine overload, and reconciles via a `ReconcileDrafts` helper (update matched in place + reset changed `Approved→Draft`, insert `ref:"new"`, delete only flagged, never touch `Pushed`). Reuses `RunStreamingAsync` for live progress.
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/AgentOrchestrator.cs`
- **Acceptance Criteria:**
  - Analyst refine improves in place (not blanked); session returns to `AwaitingTester`
  - Tester refine MERGES: unflagged drafts survive, changed reset to Draft, flagged removed, Pushed untouched
  - Live progress via `IEventBus`/SSE; compiles

#### ATE-010: AuthorTestsController — context passthrough + refine endpoints
- **Status:** [~]
- **ID:** ATE-010
- **Description:** solution.md API Design. `start` and `run-tester` map their request bodies into `AuthorRunContext`; add `POST /sessions/{id}/rerun-analyst` → `RerunAnalystAsync` and `POST /sessions/{id}/refine-tester` → `RefineTesterAsync` (both background like the existing run endpoints).
- **File:** `backend/src/QaAutomation.Api/Controllers/AuthorTestsController.cs`
- **Acceptance Criteria:**
  - `start` + `run-tester` thread the new fields through
  - `rerun-analyst` + `refine-tester` endpoints present; thin controller; compiles

#### ATE-011: Frontend API — rerunAnalyst / refineTester + extended bodies
- **Status:** [x]
- **ID:** ATE-011
- **Description:** solution.md Frontend Layout. Extend `startSession`/`runTester` body types with the context + reference-plan fields; add `rerunAnalyst(id, feedback)` and `refineTester(id, { overallFeedback, perTest })`.
- **File:** `client/src/api/authorTests.ts`
- **Acceptance Criteria:**
  - `StartSessionBody`/`RunTesterBody` carry `additionalContext` + reference-plan fields
  - `rerunAnalyst` → `POST /sessions/{id}/rerun-analyst`; `refineTester` → `POST /sessions/{id}/refine-tester`
  - `TestFeedbackItem { draftId; feedback?; remove? }`; `tsc`/`vite build` clean

#### ATE-012: Frontend — AuthorStartForm additional-context field
- **Status:** [x]
- **ID:** ATE-012
- **Description:** solution.md Frontend Layout. Multiline "Additional context (optional)" `TextField` with route/screen helper text; included in the `startSession` body.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Field present with helper text; value passed to `startSession`; build clean

#### ATE-013: Frontend — AwaitingTester extra-tester-context box
- **Status:** [x]
- **ID:** ATE-013
- **Description:** solution.md Frontend Layout. "Extra context for the Tester (optional)" box at the gate; passed to `runTester`; all three free-text fields re-seed from `session` detail.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Box present at the `AwaitingTester` gate; value reaches `runTester`; re-seeds on load; build clean

#### ATE-014: Frontend — Analyst refine panel
- **Status:** [x]
- **ID:** ATE-014
- **Description:** solution.md Frontend Layout. In the Analyst findings panel, a "Refine analysis with feedback" box + button calling `rerunAnalyst(id, feedback)`; available whenever an analysis exists, disabled while in-flight.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Feedback box + button wired to `rerunAnalyst`; disabled in-flight; build clean

#### ATE-015: Frontend — Tester refine (per-test feedback + remove + overall)
- **Status:** [x]
- **ID:** ATE-015
- **Description:** solution.md Frontend Layout. Per-test feedback field + "remove" toggle on each non-pushed draft card, an overall-feedback box, and a "Regenerate with feedback" button calling `refineTester`. Pushed cards render locked; a note explains pushed tests are preserved.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Per-test feedback + remove toggle on non-pushed cards; overall box; button → `refineTester`
  - Pushed cards locked (no feedback/remove); build clean

### PHASE 2 — CODE DISCOVERY

#### ATE-016: ICodeContextService abstraction
- **Status:** [~]
- **ID:** ATE-016
- **Description:** solution.md Code Discovery. One method `DiscoverAsync(...)` returning `(string DiffSummary, string Code)` with two generalization knobs: `extraHintText` (adds user hints to the search seed) and `alwaysSearch` (search even when linked refs exist — Author Tests thin-ticket path).
- **File:** `backend/src/QaAutomation.Core/Abstractions/ICodeContextService.cs`
- **Acceptance Criteria:**
  - Signature takes `workItemId`, `AdoWorkItem`, `AnalystOutput?`, `commitIds`, `prIds`, `extraHintText`, `alwaysSearch`, `ILlmClient`, `key`, `ct`
  - `analysis` nullable (discovery runs before the Analyst); XML docs present; compiles

#### ATE-017: CodeContextService implementation
- **Status:** [~]
- **ID:** ATE-017
- **Description:** solution.md Code Discovery. The anchored-context + triage engine generalized from the bug pipeline: resolve linked PRs/commits, optionally always-search on hints/feature terms, expand relative imports, triage files via the LLM, and assemble a byte-budgeted code block. Reuses `BugHelpers` + the full `IAzureDevOpsClient` Git surface.
- **File:** `backend/src/QaAutomation.Infrastructure/CodeContext/CodeContextService.cs`
- **Acceptance Criteria:**
  - `MaxFiles=20`, `MaxBytesPerFile=30_000`, `MaxTotalBytes=250_000` budgets enforced
  - `alwaysSearch:true` searches on hints/feature terms even with linked refs; `alwaysSearch:false` preserves the bug default
  - Emits Discover/Triage SSE events; compiles

#### ATE-018: EventPhases.Dedup constant
- **Status:** [~]
- **ID:** ATE-018
- **Description:** solution.md Data Design → wire vocabulary. Add the frozen `dedup` phase constant used by the post-gen dedup result event.
- **File:** `backend/src/QaAutomation.Core/Models/ConsoleEvent.cs`
- **Acceptance Criteria:**
  - `EventPhases.Dedup = "dedup"`; existing constants unchanged; compiles

#### ATE-019: Register ICodeContextService (DI)
- **Status:** [~]
- **ID:** ATE-019
- **Description:** solution.md Code Discovery. Register as a singleton in the Infrastructure composition root.
- **File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
- **Acceptance Criteria:**
  - `services.AddSingleton<ICodeContextService, CodeContextService>()`; compiles

#### ATE-020: AgentOrchestrator — Discover step + feed Analyst
- **Status:** [~]
- **ID:** ATE-020
- **Description:** solution.md Orchestrator. Insert a Discover-code stage after Retrieve KB: call `ICodeContextService` with `alwaysSearch:true` and `extraHintText` = the run's additional context, store the block on `session.CodeContext`, and feed `additionalContext` + discovered code into the Analyst call.
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/AgentOrchestrator.cs`
- **Acceptance Criteria:**
  - Discover runs between Retrieve KB and Analyst; `session.CodeContext` persisted
  - Analyst receives additionalContext + code; terminal failure still records what was discovered; compiles

#### ATE-021: Frontend — pipeline step insert + index shift
- **Status:** [x]
- **ID:** ATE-021
- **Description:** solution.md Frontend Layout. Insert a "Discover code" step at index 2 between Retrieve KB and Analyst; shift `activeStepIndex`/`reachedIdx` (discover=2, analyst=3, tester=4); `reachedIdx` uses `session.codeContext` to detect the Discover gate so the stepper highlights correctly even on terminal failure.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Discover step at index 2; all downstream indices +1; `reachedIdx` reads `session.codeContext`; build clean

#### ATE-022: Frontend — Discover panel
- **Status:** [x]
- **ID:** ATE-022
- **Description:** solution.md Frontend Layout. `sel === 2` renders a code-context card (pre-formatted, scrollable) or an in-flight/"no code context" fallback.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Card shows `session.codeContext` when present; fallback text otherwise; build clean

#### ATE-023: Frontend — phase union + PHASE_LABEL (dedup)
- **Status:** [x]
- **ID:** ATE-023
- **Description:** solution.md Frontend Layout. `ConsoleEvent.phase` is a strict union; add `'dedup'` there and `dedup: 'DEDUP'` to `PHASE_LABEL` so the timeline renders the new phase.
- **File:** `client/src/hooks/useEventStream.ts`, `client/src/session/shared.tsx`
- **Acceptance Criteria:**
  - `'dedup'` in the `phase` union; `dedup: 'DEDUP'` label; `tsc` clean (no TS2367)

### PHASE 3 — EXISTING-TEST DEDUP

#### ATE-024: IExistingTestsIndexService + records
- **Status:** [~]
- **ID:** ATE-024
- **Description:** solution.md Dedup. `EnsureIndexAsync`, `GetScanStatusAsync`, `BuildTesterContextAsync`, `FindDuplicatesAsync`; plus `DedupResult(Title, IsClearDuplicate, MatchedTitle?, MatchedAdoId?)` and `GeneratedTestCase(Title, StepsSummary)`.
- **File:** `backend/src/QaAutomation.Core/Abstractions/IExistingTestsIndexService.cs`
- **Acceptance Criteria:**
  - Four methods with the scan/build/find contract; `byteBudget` on the build call
  - Both records defined; XML docs present; compiles

#### ATE-025: TestPlanScan entity
- **Status:** [~]
- **ID:** ATE-025
- **Description:** solution.md Data Design. Persisted scan header: plan/suite scope, `ScannedAt`, `CaseCount`, `TitlePatternSummary`.
- **File:** `backend/src/QaAutomation.Core/Models/TestPlanScan.cs`
- **Acceptance Criteria:**
  - `Id, PlanId, SuiteId?, ScannedAt, CaseCount, TitlePatternSummary?`; compiles

#### ATE-026: ExistingTestCaseIndexEntry entity
- **Status:** [~]
- **ID:** ATE-026
- **Description:** solution.md Data Design. One indexed case per row: `ScanId` FK, `AdoTestCaseId`, `SuitePath`, `Title`, normalized `TitleSignature`, `StepsSummary`, `StepsHash`.
- **File:** `backend/src/QaAutomation.Core/Models/ExistingTestCaseIndexEntry.cs`
- **Acceptance Criteria:**
  - All columns present; `TitleSignature` normalized (lowercase, stopwords/punct stripped); compiles

#### ATE-027: ExistingTestsIndexService implementation
- **Status:** [~]
- **ID:** ATE-027
- **Description:** solution.md Dedup. Scan via `GetSuiteTestCasesAsync(planId, suiteId ?? rootSuite, recurse:true)`, upsert index rows, refresh the FTS mirror, record `ScannedAt` + a derived title-pattern summary; 24h TTL cache (forceRescan bypasses). `BuildTesterContextAsync` returns titles grouped + pattern summary within budget; `FindDuplicatesAsync` does normalized title-signature + step-overlap (Jaccard) similarity → clear-duplicate / borderline verdicts.
- **File:** `backend/src/QaAutomation.Infrastructure/ExistingTests/ExistingTestsIndexService.cs`
- **Acceptance Criteria:**
  - Cache hit when fresh + not forced; recursive scan on miss/force
  - Post-gen check auto-drops clear duplicates, flags borderline (never silently discards ambiguous)
  - Compiles

#### ATE-028: QaDbContext DbSets + mappings
- **Status:** [~]
- **ID:** ATE-028
- **Description:** solution.md Data Design. Add `TestPlanScans` + `ExistingTestCaseIndexEntries` DbSets; make the Phase-1 `TestSession` field mappings explicit alongside.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs`
- **Acceptance Criteria:**
  - Both DbSets registered; new columns/tables modeled; legacy entities untouched; compiles

#### ATE-029: DatabaseInitializer — FTS5 test_case_index
- **Status:** [~]
- **ID:** ATE-029
- **Description:** solution.md Data Design. Bootstrap a `test_case_index` FTS5 virtual table (mirroring title + steps) alongside the existing `doc_chunks` FTS bootstrap.
- **File:** `backend/src/QaAutomation.Infrastructure/Data/DatabaseInitializer.cs`
- **Acceptance Criteria:**
  - `test_case_index` created if absent, same pattern as `doc_chunks`; idempotent on restart; compiles

#### ATE-030: Register IExistingTestsIndexService (DI)
- **Status:** [~]
- **ID:** ATE-030
- **Description:** solution.md Dedup. Register as a singleton.
- **File:** `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
- **Acceptance Criteria:**
  - `services.AddSingleton<IExistingTestsIndexService, ExistingTestsIndexService>()`; compiles

#### ATE-031: AgentOrchestrator — dedup injection + post-gen filter + event
- **Status:** [~]
- **ID:** ATE-031
- **Description:** solution.md Orchestrator + Dedup. Before the Tester runs (initial + refine), if a reference plan is set, `EnsureIndexAsync` then `BuildTesterContextAsync` → inject the block; after generation, `FindDuplicatesAsync` drops clear dupes and marks borderline; emit an `EventPhases.Dedup` result carrying `skipped`/`flagged` for the UI.
- **File:** `backend/src/QaAutomation.Infrastructure/Orchestrators/AgentOrchestrator.cs`
- **Acceptance Criteria:**
  - Dedup block injected only when a reference plan is set; post-gen check applied
  - `dedup` event data carries `{ skipped[], flagged[] }` (title + matchedTitle? + matchedAdoId?); compiles

#### ATE-032: AuthorTestsController — rescan + scan-status endpoints
- **Status:** [~]
- **ID:** ATE-032
- **Description:** solution.md API Design. `POST /plans/{planId}/rescan?suiteId=` → `EnsureIndexAsync(forceRescan:true)`; `GET /plans/{planId}/scan-status?suiteId=` → `GetScanStatusAsync`.
- **File:** `backend/src/QaAutomation.Api/Controllers/AuthorTestsController.cs`
- **Acceptance Criteria:**
  - Both endpoints present with optional `suiteId` query param; return the response contracts; compiles

#### ATE-033: AuthorTestsContracts — rescan/scan-status responses
- **Status:** [~]
- **ID:** ATE-033
- **Description:** solution.md API Design. `RescanPlanResponse { scanId, caseCount, scannedAt }`; `ScanStatusResponse { scanId?, caseCount?, scannedAt? }` (null = never scanned).
- **File:** `backend/src/QaAutomation.Core/Contracts/AuthorTestsContracts.cs`
- **Acceptance Criteria:**
  - Both response classes present; camelCase wire; compiles

#### ATE-034: EF migration — AuthorTestsContextAndIndex
- **Status:** [ ]
- **ID:** ATE-034
- **Description:** solution.md Data Design + Verification. Generate one migration covering the six `TestSession` columns (Phase 1) + the `TestPlanScans` and `ExistingTestCaseIndexEntries` tables (Phase 3). Applies automatically on next backend start via `DatabaseInitializer`. **Must be run on the user's local machine (no .NET SDK in the container).**
- **File:** `backend/src/QaAutomation.Infrastructure/Data/Migrations/<ts>_AuthorTestsContextAndIndex.cs` (+ `.Designer.cs`, `QaDbContextModelSnapshot.cs`)
- **Acceptance Criteria:**
  - Command: `dotnet ef migrations add AuthorTestsContextAndIndex --project src/QaAutomation.Infrastructure --startup-project src/QaAutomation.Api`
  - Up() adds the six nullable columns to `TestSessions` and creates the two new tables; additive-only
  - Applies cleanly on backend start against an existing DB

#### ATE-035: Frontend API — rescanPlan / getScanStatus
- **Status:** [x]
- **ID:** ATE-035
- **Description:** solution.md Frontend Layout. `rescanPlan(planId, suiteId?)` and `getScanStatus(planId, suiteId?)` with their response interfaces.
- **File:** `client/src/api/authorTests.ts`
- **Acceptance Criteria:**
  - Both functions present with optional `suiteId`; typed responses; build clean

#### ATE-036: Frontend — AwaitingTester reference-plan pickers + rescan + status
- **Status:** [x]
- **ID:** ATE-036
- **Description:** solution.md Frontend Layout. Reference Test Plan + suite scope pickers (reuse `getPlans`/`getSuites`), a "Rescan plan" button, an "indexed N cases · scanned <time>" status line, and a plan navigation-hint field at the gate.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Plan/suite selects + rescan button + status line + nav-hint field present; wired to `rescanPlan`/`getScanStatus`; build clean

#### ATE-037: Frontend — AuthorStartForm reference-plan pickers
- **Status:** [x]
- **ID:** ATE-037
- **Description:** solution.md Frontend Layout. The same reference-plan/suite pickers + nav-hint field in the start form; all passed into `startSession`.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Pickers + nav-hint in the start form; values in the `startSession` body; build clean

#### ATE-038: Frontend — Tester dedup results card
- **Status:** [x]
- **ID:** ATE-038
- **Description:** solution.md Frontend Layout. In the Tester panel, a card reading the `dedup` event: "Skipped (already covered)" and "Similar to existing (kept for review)" chip rows, each chip tooltip showing the matched title + TC id.
- **File:** `client/src/session/configs/authorTests.tsx`
- **Acceptance Criteria:**
  - Skipped/flagged chips rendered from the dedup event data; matched-TC tooltips; build clean

### DOCS & PROJECT RULES

#### ATE-039: CLAUDE.md documentation-discipline rule + /docs structure
- **Status:** [x]
- **ID:** ATE-039
- **Description:** Persist the standing "Documentation discipline" rule in `CLAUDE.md` and establish the `/docs` subfolder structure (`features/`, `architecture/`, `changelog/`, `progress/`). Committed `d31d8d2`.
- **File:** `CLAUDE.md`, `docs/`
- **Acceptance Criteria:**
  - Rule section present in `CLAUDE.md`; predictable subfolders documented; committed

#### ATE-040: Feature doc + changelog entry
- **Status:** [ ]
- **ID:** ATE-040
- **Description:** Per the documentation-discipline rule, write `docs/features/author-tests-free-form-context.md` (what/why, data flow, shared services, injection points) and a dated `docs/changelog/` entry — **only after** the user's local end-to-end verification passes.
- **File:** `docs/features/author-tests-free-form-context.md`, `docs/changelog/<date>-author-tests-enhancements.md`
- **Acceptance Criteria:**
  - Feature page + changelog entry written after verification; behavior described matches shipped code
