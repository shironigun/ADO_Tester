# Author Tests Enhancements — Technical Implementation Guide

| | |
| --- | --- |
| **Project Title** | Author Tests Enhancements — Free-Text Context, Code Discovery, Refine Loops, Existing-Test Dedup |
| **Document** | `blueprint.md` — implementation guide (companion to `solution.md`) |
| **Version** | 1.0 |
| **Date** | July 28, 2026 |
| **Prepared By** | Mahmood Ahmad (BA / QA) + Claude |

## Purpose

This document is the **implementation guide** for the Author Tests enhancements designed in
[`solution.md`](./solution.md). It turns that design into a concrete, file-by-file build map for the
existing **ADO_Tester** repo — a .NET 8 (ASP.NET Core + EF Core 8 / SQLite, FTS5) backend and a
React 19 + Vite + MUI frontend. Every contract, column, and lifecycle is locked in `solution.md`; where
this guide names a type or path, it is the canonical manifest the code conforms to. Task IDs
(`ATE-###`) map 1:1 to `prd.md`; per-task status lives in `progress.md`.

> Guardrails (load-bearing): do **not** edit ADO write bodies, the embedded `AnalystSystem`/
> `TesterSystem` prompt resources, or `BugOrchestrator`'s observable behavior. All new agent
> instructions live in the **user** prompt; the shared code-discovery service is used by Author Tests
> only for this effort.

## Technology Stack

| Component | Technology |
| --- | --- |
| Backend framework | .NET 8 / ASP.NET Core |
| Language | C# 12 |
| ORM / Database | EF Core 8 / SQLite (FTS5 for KB + existing-test retrieval) |
| Project layout | Clean layering: `QaAutomation.Core` / `.Infrastructure` / `.Api` / `.Tests` |
| Background work | Singleton `AgentOrchestrator` + background `Task` with its OWN DI scope |
| Live progress | Server-Sent Events over `IEventBus` (in-memory pub/sub); durable replay via `EventLogJson` |
| LLM layer | `ILlmClientFactory` — `ClaudeCli` (default), `Anthropic`, `AzureOpenAi`, `OpenAiCompatible` |
| ADO integration | `AzureDevOpsClient` (PAT Basic auth) — work items, test plans/suites, Git/code search |
| Frontend | React 19 + Vite + MUI + Redux Toolkit + React Query |

## Project Structure

New and modified files. Paths and type names below are the canonical manifest every section conforms
to. Status: `(new)` / `(modified)`; `[ATE-###]` maps to the task backlog.

```
backend/src/QaAutomation.Core/
  Models/
    TestSession.cs                    (modified) [ATE-001] — +6 context/reference-plan columns
    AuthorRunContext.cs               (new)      [ATE-002] — optional per-run guidance carrier
    TestPlanScan.cs                   (new)      [ATE-025] — scan header entity
    ExistingTestCaseIndexEntry.cs     (new)      [ATE-026] — indexed-case entity
    ConsoleEvent.cs                   (modified) [ATE-018] — EventPhases.Dedup = "dedup"
  Contracts/
    AuthorTestsContracts.cs           (modified) [ATE-003, ATE-033] — request/response DTOs
  Abstractions/
    IAgentOrchestrator.cs             (modified) [ATE-004] — context args + refine methods
    ICodeContextService.cs            (new)      [ATE-016] — DiscoverAsync
    IExistingTestsIndexService.cs     (new)      [ATE-024] — Ensure/Status/Build/FindDuplicates + records
backend/src/QaAutomation.Infrastructure/
  Agents/
    AnalystAgent.cs                   (modified) [ATE-005, ATE-006] — prompt injection + RefineAsync
    TesterAgent.cs                    (modified) [ATE-007, ATE-008] — prompt injection + RefineAsync
  CodeContext/
    CodeContextService.cs             (new)      [ATE-017] — anchored-context + triage engine
  ExistingTests/
    ExistingTestsIndexService.cs      (new)      [ATE-027] — scan/index + dedup
  Orchestrators/
    AgentOrchestrator.cs              (modified) [ATE-009, ATE-020, ATE-031] — Discover step, refine, dedup
  Data/
    QaDbContext.cs                    (modified) [ATE-028] — 2 DbSets + mappings
    DatabaseInitializer.cs            (modified) [ATE-029] — bootstrap test_case_index FTS5
    Migrations/<ts>_AuthorTestsContextAndIndex.cs  (new, LOCAL) [ATE-034]
  DependencyInjection.cs              (modified) [ATE-019, ATE-030] — register both services
backend/src/QaAutomation.Api/
  Controllers/
    AuthorTestsController.cs          (modified) [ATE-010, ATE-032] — passthrough + 4 endpoints
client/src/
  api/authorTests.ts                  (modified) [ATE-011, ATE-035] — refine + rescan/status + bodies
  hooks/useEventStream.ts             (modified) [ATE-023] — 'dedup' in phase union
  session/shared.tsx                  (modified) [ATE-023] — dedup: 'DEDUP' label
  session/configs/authorTests.tsx     (modified) [ATE-012..015, ATE-021, ATE-022, ATE-036..038]
```

## Phase 1 — Free-text context + refine loops

**Persistence + carrier (`ATE-001`/`ATE-002`).** Add the six nullable columns to `TestSession`; the
context fields mirror `BugSession`. `AuthorRunContext` is a `sealed record` with four optional fields so
the orchestrator surface takes one arg and a null context reproduces the old behavior.

**Contracts (`ATE-003`).** `StartRequest` gains the four fields; `RunTesterRequest` is net-new (the gate
endpoint took no body); `RerunAnalystRequest { Feedback }`, `RefineTesterRequest { OverallFeedback?,
PerTest[] }`, `TestFeedback { DraftId, Feedback?, Remove }`. `TestSessionDetail` gains all six fields and
populates them in `From(...)`.

**Orchestrator surface (`ATE-004`).** `StartAsync`/`RunTesterAsync` take `AuthorRunContext? context =
null`; add `RerunAnalystAsync(sessionId, feedback, ct)` and `RefineTesterAsync(sessionId,
overallFeedback, perTest, ct)`.

**Agents (`ATE-005`–`ATE-008`).** Mirror `BugWriterAgent.BuildUserPrompt`'s labeled-section pattern.
`AnalystAgent`: `BuildUserPrompt` gains `additionalContext`/`codeContext` sections; `RefineAsync` prompts
with prior `AnalystOutput` JSON + a reviewer-feedback section. `TesterAgent`: `BuildUserPrompt` gains
`testerContext`/`existingTestsBlock` sections; `RefineAsync` renders each existing test with a stable
`ref` + status (Pushed read-only) + feedback, returns the revised array carrying `ref`/`"new"`, parser
tolerates the `ref`/`id` key. **System prompts are not touched.**

**Orchestrator impl (`ATE-009`).** `StartAsync` persists context; `RunTesterAsync` persists + injects
Tester context; `RerunAnalystAsync` reloads `AnalysisJson`, calls the Analyst refine overload, writes
back, returns to `AwaitingTester`; `RefineTesterAsync` loads drafts, calls the Tester refine overload,
and reconciles via `ReconcileDrafts` (update matched + reset changed `Approved→Draft`, insert `"new"`,
delete only flagged, skip `Pushed`). Reuse `RunStreamingAsync` for live progress.

**Controller (`ATE-010`).** `start`/`run-tester` map bodies to `AuthorRunContext`; add background
`rerun-analyst` and `refine-tester` (202).

**Frontend (`ATE-011`–`ATE-015`).** `api/authorTests.ts`: extend bodies, add `rerunAnalyst`/`refineTester`.
`authorTests.tsx`: additional-context field in the start form; extra-Tester-context box at the gate
(re-seed all three from `session`); Analyst refine box+button; Tester per-test feedback + remove toggle
+ overall box + "Regenerate with feedback" (Pushed cards locked).

## Phase 2 — Code discovery

**Abstraction + impl (`ATE-016`/`ATE-017`).** `ICodeContextService.DiscoverAsync(workItemId, wi,
analysis?, commitIds, prIds, extraHintText, alwaysSearch, llm, key, ct) → (DiffSummary, Code)`.
`CodeContextService` generalizes the bug pipeline's anchored-context + triage engine with the
`extraHintText` (search-seed hints) and `alwaysSearch` (search even with linked refs) knobs; reuses
`BugHelpers` + the `IAzureDevOpsClient` Git surface; budgets `MaxFiles=20`, `MaxBytesPerFile=30_000`,
`MaxTotalBytes=250_000`.

**Wire + DI (`ATE-018`/`ATE-019`).** `EventPhases.Dedup = "dedup"`; register
`ICodeContextService` as a singleton.

**Orchestrator (`ATE-020`).** Insert the Discover stage after Retrieve KB: call the service with
`alwaysSearch:true` + `extraHintText` = the run's additional context; store the block on
`session.CodeContext`; feed `additionalContext` + code into the Analyst.

**Frontend (`ATE-021`–`ATE-023`).** Insert the "Discover code" step at index 2, shift indices
(discover=2/analyst=3/tester=4), `reachedIdx` reads `session.codeContext`; render the `sel === 2`
code-context card; add `'dedup'` to the `useEventStream` phase union and `dedup: 'DEDUP'` to
`PHASE_LABEL`.

## Phase 3 — Existing-test dedup

**Abstraction + entities (`ATE-024`–`ATE-026`).** `IExistingTestsIndexService` with
`EnsureIndexAsync`/`GetScanStatusAsync`/`BuildTesterContextAsync`/`FindDuplicatesAsync` + the
`DedupResult`/`GeneratedTestCase` records; `TestPlanScan` + `ExistingTestCaseIndexEntry` entities.

**Impl (`ATE-027`).** Scan via `GetSuiteTestCasesAsync(planId, suiteId ?? rootSuite, recurse:true)`,
upsert rows, refresh the FTS mirror, record `ScannedAt` + `TitlePatternSummary`; 24h TTL cache
(forceRescan bypasses). `BuildTesterContextAsync` groups titles + pattern summary + FTS-ranked steps
within a byte budget. `FindDuplicatesAsync` = normalized title-signature + step-overlap (Jaccard) →
clear-duplicate / borderline verdicts.

**Data + DI (`ATE-028`–`ATE-030`).** `QaDbContext`: `TestPlanScans` + `ExistingTestCaseIndexEntries`
DbSets; `DatabaseInitializer`: bootstrap `test_case_index` FTS5 like `doc_chunks`; register
`IExistingTestsIndexService` singleton.

**Orchestrator (`ATE-031`).** Before the Tester (initial + refine), if a reference plan is set:
`EnsureIndexAsync` → `BuildTesterContextAsync` → inject. After generation: `FindDuplicatesAsync` → drop
clear dupes, mark borderline → emit a `dedup` event with `{ skipped[], flagged[] }`.

**Controller + contracts (`ATE-032`/`ATE-033`).** `POST /plans/{planId}/rescan?suiteId=` and `GET
/plans/{planId}/scan-status?suiteId=`; `RescanPlanResponse` + `ScanStatusResponse`.

**Migration (`ATE-034`, LOCAL).** Generate `AuthorTestsContextAndIndex` covering the six `TestSession`
columns + the two tables; additive-only; applies on backend start. Run on the user's machine — no .NET
SDK in the container.

**Frontend (`ATE-035`–`ATE-038`).** `api`: `rescanPlan`/`getScanStatus`. `authorTests.tsx`:
reference-plan/suite pickers + Rescan + status line + nav-hint field at both the start form and the
gate; Tester dedup results card (skipped/flagged chips with matched-TC tooltips).

## Build & verify order

Phase 1 plumbing lands first (everything depends on the context columns + contract fields). Phases 2 and
3 are independent and may verify in either order. Backend verification (`dotnet build`/`dotnet test` +
the `AuthorTestsContextAndIndex` migration) runs on the user's local machine; the frontend
(`npm run build`/`npm run lint`) is verified in-container. See `solution.md` → Verification and
`progress.md` for live status.
