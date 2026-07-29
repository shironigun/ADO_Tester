# Progress: QA Loop v2 Implementation

## Overview
- **Design:** `solution.md` (authoritative vision brief) · **Ruleset:** `Ralph loop.md`
- **Backlog:** `prd.md` (tasks `QLV2-###`, grouped by slab, built in order) · **Guide:** `blueprint.md`
- **Started:** 2026-07-29
- **Status:** SLAB 1 IN PROGRESS — QLV2-101..108 VERIFIED (locally on Windows). Currently working on
  QLV2-109 (controller) → 110/111 (frontend) → 112 (tests) → 113 (docs).
- **Resume point:** QLV2-109 — `QaLoopController` wire new fields + rerun-analyze/refine-drafts endpoints.

## Slab status
| Slab | Name                                                      | Status                           |
| ---- | --------------------------------------------------------- | -------------------------------- |
| 1    | Free-text + merge-refine in QA Loop                       | In progress (QLV2-101..108 done) |
| 2    | UI testing phase (Playwright agents via MCP + Claude CLI) | Not started                      |
| 3    | Scripts Board module                                      | Not started                      |

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
