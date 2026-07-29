# Progress: QA Loop v2 Implementation

## Overview
- **Design:** `solution.md` (authoritative vision brief) · **Ruleset:** `Ralph loop.md`
- **Backlog:** `prd.md` (tasks `QLV2-###`, grouped by slab, built in order) · **Guide:** `blueprint.md`
- **Started:** 2026-07-29
- **Status:** SLAB 2 COMPLETE. Slab 3 decomposed (QLV2-301..309). **QLV2-301 + QLV2-302 VERIFIED**
  (176/176 tests pass). Resume point: **QLV2-303**. ADO PAT is currently expired (401) — see blocker note.

## Operational note (2026-07-29)
- This machine's `Shell` tool cannot enforce the sandbox policy without `required_permissions: ["all"]`
  on every call (fails to spawn otherwise) — see `.cursor/rules/ralph-loop-ops.mdc`.
- Standing verification story for every task in this Ralph loop from here on: **177540** (controlled ADO
  story dedicated to exercising ADO_Tester features) — always use it for manual/agentic verification runs
  instead of picking an arbitrary module/story. Same rule file.

## Slab status
| Slab | Name                                                      | Status                                     |
| ---- | --------------------------------------------------------- | ------------------------------------------ |
| 1    | Free-text + merge-refine in QA Loop                       | **COMPLETE** (QLV2-101..113, all VERIFIED) |
| 2    | UI testing phase (Playwright agents via MCP + Claude CLI) | **COMPLETE** (QLV2-201..208, all VERIFIED) |
| 3    | Scripts Board module                                      | In progress — 301+302 VERIFIED, 303 next   |

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

- **QLV2-207** — `qaLoop.ts`: `ScanStartRequest += hasUiPhase?`; `ApiSessionDetail += hasUiPhase/uiStatus`;
  `ApiTestDraftDto += draftKind?/bucket?`; added `authorUiCases(id)`, `generateUiScripts(id)`,
  `healSpecs(id, draftIds)` API functions. `qaLoop.tsx`: `hasUiPhase` Switch toggle on start form
  (wired into scan request); new `UiPhasePanel` component — author step (Author UI Cases button +
  LinearProgress), review step (UiCase list with bucket chip + per-case Approve button + Generate
  Scripts button), script step (UiScript list with spec viewer + heal-selection checkboxes + "Heal
  selected (N)" button); "UI Phase" tab added to QaLoopDetailBody (conditionally shown when
  `hasUiPhase`). Build clean; lint clean on touched files (1 pre-existing warning only).

- **QLV2-208** — `QaLoopOrchestratorTests`: three new tests: `AuthorUiCases_creates_UiCase_drafts_with_bucket`
  (polls `UiStatus != Authoring`, asserts `AwaitingReview` + `DraftKind=UiCase, Bucket="smoke"`);
  `GenerateUiScripts_creates_UiScript_drafts` (seeds approved UiCase, polls `UiStatus=Drafted`, asserts UiScript
  rows with non-null `SpecFilePath`); `HealSpecs_resets_status_to_Draft` (seeds UiScript+file, sets
  Rejected+ErrorMessage, polls for `Status=Draft`, asserts `ErrorMessage=null`). Harness: `DataDir` public;
  `StubPlaywrightAgentService` added; `SeedUiCaseDraftAsync` + `SeedUiScriptDraftWithFileAsync` helpers. 163/163
  tests pass. Feature doc `docs/features/qa-loop-ui-phase.md` written (what/why, data model, data flow, ADO suite
  tree, key files, config, limitations). Changelog entry added. **SLAB 2 COMPLETE.**

- **QLV2-206** — `HealSpecsRequest { DraftIds: int[] }` added to contracts. `IQaLoopOrchestrator.HealSpecsAsync`
  added. `QaLoopOrchestrator.HealSpecsAsync/InnerAsync`: loads UiScript drafts by ID+sessionId; calls
  `IPlaywrightAgentService.HealAsync(draft.SpecFilePath, draft.ErrorMessage ?? "")` per draft; reads file
  back → updates `SpecCode`; clears `ErrorMessage`; resets `Status=Draft`. Individual failures log+skip
  (batch continues). `QaLoopController` += `POST sessions/{id}/heal-specs` (202, requires non-empty draftIds).
  160/160 tests pass.

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

---

## Slab 3 — QLV2-301..309 (in progress, running locally on Windows)

### 2026-07-29 (Slab 3 decomposition)
- Ran a dedicated explore agent against the current codebase to ground the Slab 3 design before writing
  file-level detail: confirmed `PlaywrightRunner.RunAsync` needs no QA-Loop session (reusable for ad-hoc
  execution); found the **disk-artifact collision risk** between a new `ScriptBoardRun` table and `TestRun`
  (`{runsDir}/{RunId}` is a flat autoincrement int for both) — resolved via a **negative `RunId` namespace**
  for Scripts Board runs, avoiding any change to `PlaywrightRunner`'s directory scheme; confirmed Allure
  history is file-based (`allure-history/{module}/{pipeline|manual}/history.jsonl`) and already
  module+pipeline-keyed, reusable for trends; confirmed `PlaywrightRunner.SpawnAsync` is line-buffered
  (`BeginOutputReadLine`) so the raw-ANSI `term` stream needs a **separate spawn path** (new
  `ScriptsBoardRunner`), not a modification of the golden-tested QA-Runs spawn code; confirmed no
  `@qa-meta`-equivalent marker exists yet and line 1 (before any `import`) is a safe insertion point
  (`SpecNormalizer` ignores comments); confirmed Slab 2's UI scripts live under
  `{PlaywrightRoot}/generated/ui/{bucket}/…`, not the aspirational `scripts/{module}/ui/{bucket}/…` from
  `solution.md` — Slab 3 v1 scans both roots as-is rather than relocating Slab-2 output.
- Wrote full file-level blueprint for all 9 tasks (QLV2-301..309) in `blueprint.md` → "SLAB 3 (detailed)".
  Expanded the Slab 3 epic into 9 tasks in `prd.md` (301 `@qa-meta` header, 302 inventory/reconcile
  service, 303 execution service+controller, 304 raw-ANSI term stream via a separate runner, 305
  trends/flakiness, 306 frontend scaffold, 307 xterm.js terminal, 308 trends UI, 309 tests+docs close-out).
- Created `.cursor/rules/ralph-loop-ops.mdc`: (1) this machine's `Shell` tool requires
  `required_permissions: ["all"]` on every call or it fails to spawn (no sandbox backend available); (2)
  standing verification story **177540** for every task's runtime check from here on (superseding ad-hoc
  module/story choices for QA_Tester feature verification; `CLAUDE.md`'s 171740 remains the older generic
  reference, kept as a note in the rule for disambiguation).

### 2026-07-29 (QLV2-301 — @qa-meta header)
- **QLV2-301** — `QaMeta`/`QaMetaHeader` (`Core/Models/QaMeta.cs`): `Build`/`TryParse`/`StripHeader`/
  `Upsert`/`ComputeHash`. Wired into `QaLoopOrchestrator`: `PersistDraft` (initial API spec write),
  `RefineDraftsInnerAsync`'s disk rewrite, `RewriteSpecOnDisk` (push-time — opportunistically fills in
  `TcId` on an already-stamped spec, does not backfill header-less legacy specs),
  `GenerateUiScriptsInnerAsync` (stamps the Claude-CLI-generated `.ui.spec.ts` before persisting), and
  `HealSpecsInnerAsync` (re-stamps carrying forward the prior header's fields, or mints a fresh one if the
  spec had none). New `QaMetaHeaderTests.cs` (7 tests: round-trip, upsert-prepend, upsert-replace-in-place,
  legacy/malformed → null not throw, strip-is-noop, hash stability). **170/170 tests pass** (163 + 7 new),
  `dotnet build` clean (0 warnings/errors).
- **Environment note:** the local `dotnet build`/`dotnet test` run needed the already-running
  `QaAutomation.Api` dev-server process killed first (it holds a file lock on the build output DLLs) —
  restart your `dotnet run --project src\QaAutomation.Api` afterward if you had it open for manual testing.
- **Verification against story 177540 — blocked, not skipped.** Attempted a live check
  (`GET /api/diagnostics/workitem/177540` against a locally-started backend) to ground a real
  verification run per the new standing-story rule; discovered `ADOACCESS_PAT` in the repo-root `.env` is
  **expired/invalid — every live ADO call returns 401**. Logged as a known blocker in
  `.cursor/rules/ralph-loop-ops.mdc`. QLV2-301 has zero ADO-write surface (it only changes local
  `.spec.ts` file contents) so `dotnet test` is a legitimate substitute for this task specifically, but
  **the PAT needs refreshing before any task with real ADO reads/writes (QA Loop push, Author Tests,
  Find Bugs) can get its live-run verification** — flag this to the user, don't fake the check.

### 2026-07-29 (QLV2-302 — inventory/reconcile service)
- **QLV2-302** — `ScriptBoardRun` entity (`Core/Models/ScriptBoardRun.cs`, deliberately ADO-optional —
  no `AdoTestRunId`/`TriggerSource`) + `QaDbContext` mapping + migrations `AddScriptBoardRuns` generated
  for **both** providers (`dotnet ef migrations add AddScriptBoardRuns --project src\QaAutomation.Infrastructure`
  for SQLite, and the Npgsql equivalent with `DATABASE_PROVIDER=postgres --project
  src\QaAutomation.Migrations.Npgsql`). `IScriptInventoryService`/`ScriptInventoryService`
  (`Infrastructure/ScriptsBoard/`): scans `{DataDirectory}/scripts/**/*.spec.ts` (API) +
  `{PlaywrightRoot}/generated/ui/**/*.ui.spec.ts` (UI, skipped gracefully when `PlaywrightRoot` unset/
  missing), parses `@qa-meta` per file, cross-references `ApiTestDraft` rows by normalized absolute path,
  and classifies each into `linked`/`no_meta`/`orphan_disk`/`orphan_db`. Registered scoped in
  `DependencyInjection.cs`. New `ScriptInventoryServiceTests.cs` (6 tests, one per reconcile state + two
  `PlaywrightRoot` edge cases). **176/176 tests pass** (163 + 7 QLV2-301 + 6 QLV2-302), `dotnet build` clean.
- **No UI/endpoint surface yet** — this task is a backend service with no controller (that's QLV2-303).
  Nothing new to click through in the app yet; verified via the automated test suite only.
- **Live boot-check attempted, inconclusive (not a code issue):** tried starting the local backend to
  confirm `AddScriptBoardRuns` applies cleanly on boot; hit a pre-existing `address already in use` on
  port 5250 from two PIDs neither `Get-Process` nor `Get-NetTCPConnection`-then-`Get-Process` could
  resolve on this machine (visible via `netstat` only) — didn't force-kill unidentified PIDs. The
  migration itself is a plain additive `CREATE TABLE` (mirrors `TestRuns`' shape) with no manual-testing
  risk; `dotnet build`+`dotnet test` (which spin up their own temp SQLite DBs via `DatabaseInitializer`)
  are sufficient confidence for this task. **If you still have a stuck backend process from an earlier
  manual test session, restart it** to pick up the new table.
