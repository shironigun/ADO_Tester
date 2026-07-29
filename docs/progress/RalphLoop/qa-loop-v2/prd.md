# PRD — QA Loop v2 (task backlog)

Tasks `QLV2-###`, grouped by slab, **built in order**. One task per Ralph-loop iteration; flip `[ ]`→`[x]`
here and log in `progress.md` only after the task is implemented **and** verified. Slab 1 is fully broken
down; Slabs 2–3 are epics that get decomposed into `QLV2-2xx`/`QLV2-3xx` tasks when we reach them (their
designs are locked in `solution.md`; file-level guidance grows in `blueprint.md`).

Acceptance-criteria shorthand: **B** = `dotnet build` clean · **T** = `dotnet test` green (incl.
`AdoGoldenTests`) · **F** = `npm run build` + `npm run lint` clean on touched files.

---

## Slab 1 — Free-text + merge-refine in QA Loop

- [x] **QLV2-101** — Add `AdditionalContext`, `GenerateContext`, `LastFeedback` (nullable `TEXT`) to
  `ApiTestSession`; generate migration `AddQaLoopFreeTextContext`. **AC:** B; migration applies on
  startup; three columns present on `ApiTestSessions`; no other schema change.
- [x] **QLV2-102** — `ModuleAnalyzerAgent.AnalyzeInventoryAsync`/`BuildInventoryPrompt` accept
  `additionalContext` and inject `## Additional Context (from tester)` when non-empty. **AC:** B/T; prompt
  contains the section only when non-empty; system prompt untouched.
- [x] **QLV2-103** — Add `ModuleAnalyzerAgent.RefineInventoryAsync(prior inventory+surface, feedback, …)`
  returning `ModuleAnalysis` (reuses `ParseAnalysis`). **AC:** B/T; unit test parses a refined
  inventory+matrix; malformed JSON throws `AgentException`.
- [x] **QLV2-104** — Contract/Endpoint/Workflow generators: add `extraContext` param (user-prompt
  injection) + a `RefineAsync` overload (prior spec/steps + feedback → re-emit). **AC:** B/T;
  `AdoGoldenTests` green; system prompts byte-exact.
- [x] **QLV2-105** — `QaLoopOrchestrator`: thread `session.AdditionalContext` into the analyze call and
  `session.GenerateContext` into all three generator calls. **AC:** B/T; live scan with prose reaches the
  analyzer prompt.
- [x] **QLV2-106** — `QaLoopOrchestrator.RerunAnalyzeAsync` + `RerunAnalyzeInnerAsync` + `IQaLoopOrchestrator`
  entry. **AC:** B/T; `Analyzing → AwaitingReview`; `InventoryJson`/`AnalysisJson`/`LastFeedback` persisted;
  gap-selected default re-applied.
- [x] **QLV2-107** — `QaLoopOrchestrator.RefineDraftsAsync` + `RefineDraftsInnerAsync` + `ReconcileApiDrafts`
  + interface entry. **AC:** B/T; targeted Draft/Rejected rows updated in place + on-disk spec rewritten;
  `ref:"new"` inserted; flagged removed; **Approved + Pushed untouched**; `Drafting → Drafted`.
- [x] **QLV2-108** — Contracts: `ScanStartRequest.AdditionalContext`, generate-drafts `GenerateContext`,
  `RerunAnalyzeRequest`, `RefineDraftsRequest`, `ApiDraftFeedback`; `ApiSessionDetail` surfaces the three
  context fields. **AC:** B.
- [x] **QLV2-109** — `QaLoopController`: persist `AdditionalContext` on `scan/start`; write `GenerateContext`
  before `generate-drafts`; new `POST sessions/{id}/rerun-analyze` + `POST sessions/{id}/refine-drafts`
  (202). **AC:** B; endpoints resolve (curl round-trip).
- [x] **QLV2-110** — `client/src/api/qaLoop.ts`: extend `startScan`/`generateDrafts` bodies; add
  `rerunAnalyze` + `refineDrafts`; add the three context fields to the detail type. **AC:** F.
- [x] **QLV2-111** — `client/src/session/configs/qaLoop.tsx`: start-form "Additional context" field;
  coverage-step "Context for generators" + "Refine analysis with feedback"; drafts-step per-draft feedback
  + remove toggle + overall box + "Regenerate with feedback" (Approved/Pushed locked). **AC:** F; inputs
  re-seed from detail on reload.
- [x] **QLV2-112** — `QaLoopOrchestratorTests`: refine-merge (targeted updated, new inserted, flagged
  removed, Approved+Pushed untouched) + analyzer-refine returns matrix → `AwaitingReview`. Full B/T/F. **AC:**
  all green.
- [x] **QLV2-113** — Docs after verification: `docs/features/qa-loop-free-text.md` + a `docs/changelog/`
  entry. **AC:** docs match shipped behavior.

---

## Slab 2 — UI testing phase  *(epic — decompose into QLV2-2xx when reached)*

Locked in `solution.md` → Slab 2. High-level units to break down:
- **QLV2-201** data: `ApiTestDraft.Bucket` + artifact-type marker + UI phase marker on session; UI path
  `{module}/ui/{bucket}/…`; migration.
- **QLV2-202** settings + Playwright global-setup: `qaloop.app_url`/`app_username`/`app_password`
  (secret) two-layer wiring; login → `storageState` under `fixtures/`.
- **QLV2-203** UI authoring: Analyst→Tester (no code) module/free-text driven → bucketed UI cases +
  review/approve + Slab-1 merge-refine.
- **QLV2-204** Playwright-agents harness: `init-agents --loop=claude`; agentic Claude-CLI+MCP invocation
  (framework-root cwd, not llm-scratch); planner→generator per approved case → verified `.ui.spec.ts`.
- **QLV2-205** execution: UI 4th level in runner/RunExecutionService; UI capture on; ADO "UI" suite tree
  (extend `EnsureLevelSuitesAsync`); ADO Test Run linkage.
- **QLV2-206** failure triage: batch **Heal** (healer agent via MCP on multi-select/all) + existing
  Draft-bugs path; healer never auto-fires.
- **QLV2-207** frontend: UI toggle on scan form; UI phase authoring/scripts/execute panels; heal-or-bug
  triage UI.
- **QLV2-208** tests + docs.

## Slab 3 — Scripts Board module  *(epic — decompose into QLV2-3xx when reached)*

Locked in `solution.md` → Slab 3. High-level units to break down:
- **QLV2-301** `@qa-meta` spec header (generators + backfill) + disk⟷DB reconcile service.
- **QLV2-302** scripts-board backend: controller/service reusing `PlaywrightRunner`; execution by
  spec/bucket/level/type/module/tag/all + headed/headless + workers.
- **QLV2-303** history/trends (Allure store) + flakiness metric.
- **QLV2-304** raw-ANSI `term` SSE stream (`FORCE_COLOR=1`, raw stdout chunks).
- **QLV2-305** frontend module (paths/api/page/Layout) + `xterm.js` terminal + inventory grid + filtering
  + per-spec drawer + trends + flakiness.
- **QLV2-306** tests + docs.
