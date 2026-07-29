# Progress: QA Loop v2 Implementation

## Overview
- **Design:** `solution.md` (authoritative vision brief) · **Ruleset:** `Ralph loop.md`
- **Backlog:** `prd.md` (tasks `QLV2-###`, grouped by slab, built in order) · **Guide:** `blueprint.md`
- **Started:** 2026-07-29
- **Status:** SLAB 1 COMPLETE — all 13 tasks (QLV2-101..113) VERIFIED. 158/158 tests pass.
  Frontend build clean. Resume point for Slab 2: **QLV2-201** (decompose from epic in prd.md).

## Slab status
| Slab | Name                                                      | Status                                     |
| ---- | --------------------------------------------------------- | ------------------------------------------ |
| 1    | Free-text + merge-refine in QA Loop                       | **COMPLETE** (QLV2-101..113, all VERIFIED) |
| 2    | UI testing phase (Playwright agents via MCP + Claude CLI) | Not started                                |
| 3    | Scripts Board module                                      | Not started                                |

---

## Log

### 2026-07-29 (scaffold)
- Finalized the three-slab vision with the user (decision-by-decision): one QA-Loop run / two phases
  (API → UI); Slab 1 = three free-text touchpoints + merge-refine; Slab 2 = UI phase built by the
  **official Playwright agents (planner/generator/healer) over Playwright MCP + Claude CLI** (no code
  grounding), automated-login `storageState`, UI 4th level + smoke/regression/e2e buckets + ADO UI suite
  tree, human heal-or-bug triage, generic across modules; Slab 3 = Scripts Board (new coexisting module,
  DB + `@qa-meta` identity, inventory/execution/trends/flakiness, xterm.js live terminal).
- Created `docs/progress/RalphLoop/qa-loop-v2/` with `Ralph loop.md` (ruleset) and the authoritative
  overarching `solution.md`. Established the three explore-agent codebase maps (Author Tests pipeline,
  QA Loop internals + legacy UI generator, frontend module patterns) referenced throughout the design.
- Wrote `blueprint.md` (Slab 1 detailed; Slabs 2–3 outlined) and `prd.md` (Slab 1 tasks QLV2-101..113;
  Slabs 2–3 epics QLV2-2xx/3xx).

### 2026-07-29 (Slab 1 QLV2-101..108 — locally on Windows, all VERIFIED)
- **QLV2-101** — Added `AdditionalContext`, `GenerateContext`, `LastFeedback` (nullable TEXT) to `ApiTestSession`; generated migration `AddQaLoopFreeTextContext`. Build + 156 tests green.
- **QLV2-102** — `ModuleAnalyzerAgent.BuildInventoryPrompt` + `AnalyzeInventoryAsync` gain `additionalContext` optional param; appends `## Additional Context (from tester)` when non-empty. System prompt untouched. `QaLoopOrchestrator` passes `session.AdditionalContext` via named arg.
- **QLV2-103** — `ModuleAnalyzerAgent.RefineInventoryAsync` added: builds prompt with prior inventory+surfaceMatrix JSON + reviewer feedback; reuses `CompleteJsonAsync`+`ParseAnalysis`; same `ModuleAnalysis` output shape.
- **QLV2-104** — `ContractGeneratorAgent`, `EndpointGeneratorAgent`, `WorkflowGeneratorAgent`: each `GenerateAsync` gains `string? extraContext`; appends `## Extra Context (from tester)` into user prompt. Each gains a `RefineAsync` overload (prior spec/steps + feedback + extraContext → same `GeneratedSpec` shape). `AdoGoldenTests` green.
- **QLV2-105** — `QaLoopOrchestrator.GenerateDraftsInnerAsync`: all three generator `GenerateAsync` calls pass `session.GenerateContext` as `extraContext` via named arg.
- **QLV2-106** — `IQaLoopOrchestrator.RerunAnalyzeAsync` + `QaLoopOrchestrator.RerunAnalyzeAsync`/`RerunAnalyzeInnerAsync`: loads prior inventory+surface, sets `Analyzing`, calls `RefineInventoryAsync`, re-applies gap-selected, persists, sets `AwaitingReview`+PersistLog. Full cancel/error handling.
- **QLV2-107** — `IQaLoopOrchestrator.RefineDraftsAsync` + `QaLoopOrchestrator.RefineDraftsAsync`/`RefineDraftsInnerAsync`/`MatchRouteForDraft`/`MatchWorkflowForDraft`/`RefineSingle`/`RefineSingleMulti` helpers. Targeted Draft/Rejected rows refined in place via matching generator `RefineAsync`; disk spec rewritten; flagged rows removed; Approved+Pushed NEVER touched; `Drafted` on completion.
- **QLV2-108** — `QaLoopContracts`: `ScanStartRequest` += `AdditionalContext`; new `RerunAnalyzeRequest`, `ApiDraftFeedback`, `RefineDraftsRequest`, `GenerateDraftsRequest`; `ApiSessionDetail` += `AdditionalContext`/`GenerateContext`/`LastFeedback` + `From()` mapper updated.

### 2026-07-29 (Slab 1 QLV2-109..113 — all VERIFIED)
- **QLV2-109** — `QaLoopController`: `ScanStart` persists `AdditionalContext`; `GenerateDrafts` takes optional `GenerateDraftsRequest` body + persists `GenerateContext`; `POST sessions/{id}/rerun-analyze` + `POST sessions/{id}/refine-drafts` endpoints added (202). 158/158 tests.
- **QLV2-110** — `client/src/api/qaLoop.ts`: `ScanStartRequest` += `additionalContext`; `ApiSessionDetail` += three context fields; `generateDrafts(id, ctx?)` sends context body; `rerunAnalyze(id, feedback)` + `refineDrafts(id, req)` + `ApiDraftFeedback` added. Frontend build clean.
- **QLV2-111** — `client/src/session/configs/qaLoop.tsx`: start form `additionalContext` multiline field; `AwaitingReview` detail tab: `generateContext` TextField + `rerunAnalyze` box+button; `DraftsByLevel`: per-draft feedback+remove checkbox + overall feedback + `refineDrafts` button (locked for Approved/Pushed). Build clean.
- **QLV2-112** — `QaLoopOrchestratorTests`: two new tests (`RefineDrafts_updates_targeted_removes_flagged_leaves_approved_and_pushed_untouched` + `RerunAnalyze_returns_session_to_AwaitingReview_with_refreshed_matrix`). Fixed `QaLoopAdoStub.GetLinkedTestCasesAsync` (returns empty). `SetInventoryAsync` + `sourceRoutes` param added to harness. 158/158 tests.
- **QLV2-113** — Feature doc `docs/features/qa-loop-free-text.md` + changelog entry + PRD all 13 tasks flipped `[x]`. **SLAB 1 COMPLETE.**
---

## Slab 2 — QLV2-201..208 (in progress)

### 2026-07-29 (Slab 2 decomposition)
- Expanded Slab 2 epic into 8 tasks (QLV2-201..208) in `prd.md`; wrote full file-level blueprint for all
  8 tasks in `blueprint.md`. Verification note: use story **177540** + Anthropic provider
  `claude-haiku-4-5-20251001` for all manual UI phase test runs.

### 2026-07-29 (QLV2-201 — Data layer)
- **QLV2-201** — Added `ApiDraftKind` (wire-string enum: api_case/ui_case/ui_script) + `ApiUiStatus`
  (exact-name enum: Authoring/AwaitingReview/Drafting/Drafted/Pushing/Done/Failed/Cancelled) +
  `UiPhaseEnumExtensions` to `Enums.cs`. Fixed `ToDirName(Ui)` → "ui" (was "uis" via the old generic
  formula). Added `HasUiPhase` (bool) + `UiStatus` (`ApiUiStatus?`) to `ApiTestSession`; added
  `DraftKind` (`ApiDraftKind?`) + `Bucket` (nullable string) to `ApiTestDraft`. Added nullable
  ValueConverters for both new enums + four new property configs to `QaDbContext`. **Pending local:**
  run `dotnet ef migrations add AddQaLoopUiPhase --project src\QaAutomation.Infrastructure
  --startup-project src\QaAutomation.Api` then `dotnet build` + `dotnet test`.

- **QLV2-202** — `QaLoopOptions` += `AppUrl`/`AppUsername`/`AppPassword`; two-layer env-var bridge +
  RuntimeSettings PostConfigure added to `DependencyInjection.cs`; three keys (`qaloop.app_url`,
  `qaloop.app_username`, `qaloop.app_password`) added to `AppSettingsService` `AllKeys`/`OptionKeys`/
  `OptionValue` + `qaloop.app_password` to `SecretKeys`; `scripts/fixtures/auth.setup.ts` scaffolded
  (Playwright global-setup: reads env vars, logs in, saves `storageState.json`). B/T 158/158 green.

- **QLV2-203** — Created `ui_case_author_system.txt` embedded prompt (bucket-tagged manual UI test cases;
  JSON array of `{title, preconditions, steps, bucket}`). Added `AgentPrompts.UiCaseAuthorSystem`
  property. New `UiCaseAuthorAgent(ILlmClient)` with `AuthorCasesAsync(module, inventory, ctx?, …) →
  List<UiCaseDraft>` (each has `Title/Preconditions/StepsJson/Bucket`; normalises bucket to
  smoke/regression/e2e; `AgentException` on non-array JSON). `QaLoopContracts`: `ApiTestDraftDto` +=
  `DraftKind`+`Bucket` (wired in `From()`); `ApiSessionDetail` += `HasUiPhase`+`UiStatus` (wired in
  `From()`); new empty `AuthorUiCasesRequest`. `IQaLoopOrchestrator.AuthorUiCasesAsync` added.
  `QaLoopOrchestrator.AuthorUiCasesAsync/InnerAsync`: sets `UiStatus=Authoring`, calls agent, persists
  `ApiTestDraft` rows (`Level=Ui`, `DraftKind=UiCase`, `Bucket` filled), sets `UiStatus=AwaitingReview`.
  `QaLoopController` += `POST sessions/{id}/author-ui-cases` (202). B/T 158/158 green.

- **QLV2-204** — `QaLoopOptions` += `PlaywrightRoot` (string, `AUTOMATION_PLAYWRIGHT_ROOT` env bridge).
  Created `IPlaywrightAgentService` (Core/Abstractions) + `PlaywrightAgentService` (Infrastructure/QaLoop):
  `GenerateScriptAsync(caseTitle, stepsJson, bucket, tcIdPending, …) → string` (spawns claude CLI with
  `--output-format stream-json [--mcp-config .mcp.json] [--model …]` from `PlaywrightRoot` cwd; writes
  spec to `{root}/generated/ui/{bucket}/{slug}-{id}.ui.spec.ts`; returns path). Internal `BuildAgentArgs()`
  method for unit-test verification. `HealAsync(specPath, failureMsg, …)` spawns healer prompt against
  same CLI. Registered scoped. `IQaLoopOrchestrator.GenerateUiScriptsAsync` added. Orchestrator
  `GenerateUiScriptsAsync/InnerAsync`: `UiStatus=Drafting` → service per Approved UiCase → insert
  UiScript draft (SpecCode/SpecFilePath/DraftKind=UiScript) → `UiStatus=Drafted`; each case failure
  is skipped with a warning (batch continues). Controller += `POST sessions/{id}/generate-ui-scripts`
  (202). B/T 158/158 green.

- **QLV2-205** — `PlaywrightRunSpec.EnableCapture` added (`IPlaywrightRunner.cs`). `PlaywrightRunner.WriteRunConfig`
  injects `screenshot: 'only-on-failure', trace: 'on-first-retry'` into the `use:` block when `spec.EnableCapture`.
  `RunExecutionService.ExecuteAsync` sets `EnableCapture = drafts.Any(d => d.Level == TestLevel.Ui)` — off for
  API-only runs (no regression). `QaLoopOrchestrator` gains private `EnsureUiBucketSuitesAsync(planId, uiSuiteId,
  allSuites?, sessionKey?, ct) → Dictionary<string,int>` that creates/finds Smoke/Regression/E2E sub-suites under
  the Ui level suite (same catch-and-fallback pattern as `EnsureLevelSuitesAsync`). Batch push (`RunPushAsync`) and
  single-draft push (`PushDraftAsync`) both call it when UiScript drafts are present and route them to the correct
  bucket suite. Two new golden tests: `Push_routes_UiScript_draft_to_bucket_sub_suite_not_level_suite` (verifies
  TC lands in the Smoke sub-suite, not root/level suite) + `Push_api_drafts_do_not_get_bucket_routing` (verifies
  no bucket suites created for API-only pushes). `QaLoopAdoStub.GetTestSuitesAsync` returns empty list (was
  NotImplementedException) for cleaner suite-creation tests. 160/160 tests green.

- **Environment blocker for backend build/test.** This remote Linux container has **only Node/npm** (v22)
  — **no .NET SDK**, and the network policy **denies** the dotnet build hosts (`builds.dotnet.microsoft.com`
  → 403 on CONNECT; allowlist is npm/pypi/crates/go registries only, no Microsoft dotnet host). The repo's
  `launch.json` backend points at a Windows `C:\Program Files\dotnet\dotnet.exe` — the .NET backend is
  meant to be built on the user's Windows machine, not here.
- **Impact:** Slab 1 is ~90% backend (entity/migration/agents/orchestrator/controller/contracts). I cannot
  run `dotnet build`/`dotnet test` here, so I cannot satisfy the Ralph-loop "verify before commit" rule for
  backend tasks in-container. Frontend tasks (QLV2-110/111) ARE verifiable here (`npm run build`/`lint`).
- **Resolution (user):** author the **documentation** here; **execute the implementation locally**
  afterwards on the Windows machine. No backend code is written in this container — the doc set is the
  deliverable, and the local Ralph-loop run builds/tests/commits each task with real `dotnet`.
