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

## Slab 2 — UI testing phase

- [x] **QLV2-201** — Data: add `ApiDraftKind` enum (`api_case`/`ui_case`/`ui_script`, wire-string) +
  `ApiUiStatus` enum (exact-name: `Authoring`/`AwaitingReview`/`Drafting`/`Drafted`/`Pushing`/`Done`/
  `Failed`/`Cancelled`) + `UiPhaseEnumExtensions`; add `ApiTestDraft.DraftKind` (`ApiDraftKind?`) +
  `ApiTestDraft.Bucket` (nullable TEXT); add `ApiTestSession.HasUiPhase` (bool, default false) +
  `ApiTestSession.UiStatus` (`ApiUiStatus?`); fix `ToDirName(Ui)` → `"ui"` (was `"uis"`); add EF
  converters for both new enums to `QaDbContext`; generate migration `AddQaLoopUiPhase`. **AC:** B;
  migration applies on startup; all four new columns present on their tables; `DraftKind` null = existing
  API-case behavior preserved; `ToDirName(Ui)` returns `"ui"`.

- [x] **QLV2-202** — Settings: `QaLoopOptions` += `AppUrl`/`AppUsername`/`AppPassword`; add
  `PostConfigure` bindings in `DependencyInjection.cs` for `qaloop.app_url`, `qaloop.app_username`,
  `qaloop.app_password`; register the three keys in `AppSettingsService`; scaffold
  `scripts/fixtures/auth.setup.ts` (reads `AUTOMATION_APP_URL`/`_USERNAME`/`_PASSWORD` env vars →
  headless Chromium login → saves `storageState.json` to the same `fixtures/` dir). **AC:** B; three
  runtime-settings keys recognised; `auth.setup.ts` file committed.

- [x] **QLV2-203** — UI test-case authoring: embed `ui_case_author_system.txt` in `AgentPrompts`;
  new `UiCaseAuthorAgent.AuthorCasesAsync(module, inventory, additionalContext?, …) → UiCaseDraft[]`
  (each element has `Title`, `Preconditions`, `StepsJson`, `Bucket`); add `QaLoopOrchestrator.
  AuthorUiCasesAsync(sessionId)` + `AuthorUiCasesInnerAsync` (sets `UiStatus=Authoring` → agent
  → persists `ApiTestDraft` rows with `DraftKind=UiCase`, `Level=Ui`, `Bucket` filled → `UiStatus=
  AwaitingReview`); `IQaLoopOrchestrator` entry; contract `AuthorUiCasesRequest`; `ApiSessionDetail`
  surfaces `HasUiPhase`/`UiStatus`; controller endpoint `POST sessions/{id}/author-ui-cases` (202).
  **AC:** B/T; unit test parses mock agent JSON → `UiCaseDraft[]`; malformed JSON throws `AgentException`.

- [x] **QLV2-204** — Playwright agents harness: new `IPlaywrightAgentService` /
  `PlaywrightAgentService` (spawns `claude --output-format stream-json` with framework-root cwd and
  loads `.mcp.json` Playwright MCP; feeds approved UI case as feature spec; returns generated
  `.ui.spec.ts` path); `QaLoopOrchestrator.GenerateUiScriptsAsync(sessionId)` + inner (sets
  `UiStatus=Drafting` → service per approved `UiCase` → saves `UiScript` draft with `SpecCode`/
  `SpecFilePath` + `DraftKind=UiScript` → `UiStatus=UiDrafted`); `IQaLoopOrchestrator` entry;
  controller endpoint `POST sessions/{id}/generate-ui-scripts` (202). **AC:** B/T; mock unit test
  verifies correct `claude` CLI args.

- [x] **QLV2-205** — Execution: `PlaywrightRunner` gains `EnableCapture` flag
  (`trace:'on-first-retry'`, `screenshot:'only-on-failure'`) activated for `DraftKind=UiScript` runs;
  `RunExecutionService` handles UI-script drafts; extend `EnsureLevelSuitesAsync` to resolve/create a
  "UI" parent suite with smoke/regression/e2e child suites; `TestRun` ADO link targets the correct
  bucket-child suite. **AC:** B/T; golden test for UI ADO suite-tree write; capture config is off for
  API runs (no regression).

- [x] **QLV2-206** — Heal action: `IPlaywrightAgentService.HealAsync(specFilePath, failureMessage)` runs
  the healer agent on a spec and overwrites it; `QaLoopOrchestrator.HealSpecsAsync(sessionId, int[]
  draftIds)` batch-heals (heals each `UiScript` draft → resets status to `Draft` so user can re-review);
  `IQaLoopOrchestrator` entry; controller `POST sessions/{id}/heal-specs` (202). **AC:** B/T; endpoint
  resolves; `HealSpecsAsync` unit test covers reset-to-Draft path.

- [x] **QLV2-207** — Frontend: `ScanStartRequest` += `hasUiPhase`; scan form UI-phase toggle; session
  config: `UiAuthoring`/`UiAwaitingReview` step (case list with bucket chip + override dropdown +
  approve/refine) + `UiDrafting`/`UiDrafted` step (script viewer + approve); QA Runs triage drawer
  += "Heal selected" multi-select button (calls `heal-specs`). **AC:** F; `npm run build` + `npm run
  lint` clean.

- [x] **QLV2-208** — Tests + docs: `QaLoopOrchestratorTests` for `AuthorUiCasesAsync` (rows have
  `DraftKind=UiCase`, `Bucket` set) + `GenerateUiScriptsAsync` (rows have `DraftKind=UiScript`,
  `SpecFilePath` set); full B/T/F. Feature doc `docs/features/qa-loop-ui-phase.md` +
  `docs/changelog/` entry. **AC:** all green; docs written.

## Slab 3 — Scripts Board module

Full file-level detail in `blueprint.md` → "SLAB 3". Verification for every task in this slab includes a
run against standing test story **177540** (see `.cursor/rules/ralph-loop-ops.mdc`).

- [x] **QLV2-301** — `QaMeta`/`QaMetaHeader` (build/parse/upsert/hash) in Core; baked into the API spec
  persist path (`QaLoopOrchestrator.PersistDraft`, incl. `RefineDraftsAsync` rewrites) and the UI
  script-gen path (`PlaywrightAgentService.GenerateScriptAsync` + `HealAsync`). **AC:** B/T; fresh API and
  UI specs both start with a valid `// @qa-meta` line; legacy specs parse as `null`, not an error.
- [x] **QLV2-302** — `ScriptBoardRun` entity + migration `AddScriptBoardRuns`; `IScriptInventoryService`/
  `ScriptInventoryService` scanning both spec roots, parsing `@qa-meta`, cross-referencing `ApiTestDraft`,
  producing `linked`/`orphan_disk`/`orphan_db`/`no_meta` reconcile states. **AC:** B/T; all four reconcile
  states covered by tests; UI-root scan no-ops gracefully when `PlaywrightRoot` unset.
- [ ] **QLV2-303** — `PlaywrightRunSpec` += `Headed`/`Workers` (additive, zero change for QA Runs);
  `IScriptBoardExecutionService`/service (filter → resolve specs → `ScriptBoardRun` row → execute via
  `IPlaywrightRunner`, `RunId = -runId` disk-namespace trick, reuses `RunCancellationRegistry`);
  `ScriptsBoardController` (`inventory`, `runs` POST/GET/cancel/stream). **AC:** B/T; end-to-end
  `POST /api/scripts-board/runs` round-trip locally; negative `RunId` verified non-colliding.
- [ ] **QLV2-304** — Extract `PlaywrightRunner`'s config-writing + result-parsing into shared
  `internal static` helpers (pure refactor, existing `PlaywrightRunnerTests` stay green); `EventKinds.Term`
  (live-only, not narrative); new `IScriptsBoardRunner`/`ScriptsBoardRunner` — separate raw-byte-stream
  spawn path (`FORCE_COLOR=1`, no line-buffering) used only by Scripts Board so the golden-tested QA-Runs
  `PlaywrightRunner.SpawnAsync` is never touched. **AC:** B/T; existing `PlaywrightRunnerTests` unchanged
  and green; manual raw-SSE capture of a run against story 177540 shows colored ANSI `term` events.
- [ ] **QLV2-305** — `IPlaywrightRunner.GetHistoryFilePath` (expose existing private Allure history path
  logic); `IScriptTrendsService`/service (`GetTrendsAsync` from `history.jsonl`, `GetFlakinessAsync` from
  per-test pass/fail flips across runs); trends/flakiness controller endpoints. **AC:** B/T; trends/flakiness
  populate after ≥2 runs against the same module (manual check against story 177540, run twice).
- [ ] **QLV2-306** — Frontend module scaffold: `paths.scriptsBoard`, `api/scriptsBoard.ts`,
  `pages/ScriptsBoardPage.tsx` (filter bar + inventory grid w/ reconcile-state chips + per-spec run action
  + runs history table + drawer), nav+route in `Layout.tsx`. **AC:** F.
- [ ] **QLV2-307** — `npm install xterm xterm-addon-fit`; `components/TermConsole.tsx` (raw ANSI
  passthrough fed by the `term` SSE kind); wired into the Scripts Board run drawer. **AC:** F; manual
  check — a run against story 177540 shows live colored output in the terminal panel.
- [ ] **QLV2-308** — Trends tab: simple table/bar rendering of `GetTrendsAsync`/`GetFlakinessAsync` (no
  new charting dependency unless one already exists for Reports). **AC:** F; renders real data after ≥2
  runs on the same module.
- [ ] **QLV2-309** — Full `dotnet test` + `npm run build`/`lint` sweep; end-to-end manual pass against
  story 177540 (scan → push → inventory `linked` w/ `@qa-meta` → run by module → live terminal → run again
  → trends/flakiness show 2 points → hand-added stray spec flagged `orphan_disk`); feature doc
  `docs/features/scripts-board.md` + changelog entry; PRD all `[x]`. **AC:** all green; docs written.
  **SLAB 3 COMPLETE.**
