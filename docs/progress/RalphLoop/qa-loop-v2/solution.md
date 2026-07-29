# Technical Design Document — QA Loop v2

| **Project Title**    | QA Loop v2 — Free-Text Steering · UI Testing Phase · Scripts Board |
| -------------------- | ----------------------------------------------------------------- |
| **Document Version** | 1.0                                                               |
| **Date**             | July 29, 2026                                                     |
| **Prepared By**      | Mahmood Ahmad (BA / QA) + Claude                                  |
| **Companion docs**   | `Ralph loop.md` (ruleset) · per-slab `solution/blueprint/prd` · shared `progress.md` |

# Revision History

| **Version** | **Date**      | **Author**       | **Description of Change** |
| ----------- | ------------- | ---------------- | ------------------------- |
| 1.0         | July 29, 2026 | Mahmood + Claude | Initial finalized vision brief. Locked: three-slab carve; one QA-Loop run with two phases (API → UI); UI script-gen via the **official Playwright agents (planner/generator/healer)** over **Playwright MCP + Claude CLI** (no code grounding); human heal-or-bug triage; automated-login `storageState`; generic across any module; Scripts Board as a new coexisting module with DB+`@qa-meta` identity, xterm.js live terminal, and inventory/execution/trends/flakiness in v1. |

# Purpose

QA Loop today (`docs/qa-loop-reshape/`, shipped 2026-07-01) does **API testing only** — three levels
(Contract / Endpoint / Workflow) driven off a **module** — generating ADO Test Cases + Playwright
`.spec.ts`, executing them, mapping JUnit → ADO Test Runs, and running AI impact analysis on pipeline
builds. Separately, **Author Tests** (2026-07-28) gained free-text context, Analyst/Tester refine loops,
code discovery, and existing-test dedup — but it only produces **manual ADO Test Cases** and runs against
a **story**, not a module.

QA Loop v2 makes the QA Loop the single, complete QA authoring-and-execution surface by adding three
capabilities, each an independently-shippable **slab**:

1. **Free-text steering + refine in QA Loop** — a tester points at a module *and* describes scope / skip
   / focus in prose, and can regenerate with feedback (the pattern Author Tests just got).
2. **A UI testing phase** — a second phase of the QA-Loop run that authors UI test cases (house style),
   then builds Playwright **UI** scripts with the **official Playwright agents (planner / generator /
   healer)** driving a real browser, then executes them as a 4th level with ADO Test-Run linkage.
3. **A Scripts Board module** — inspired by `github.com/alaintruong77/playwright-dashboard` but far
   richer — to manage, track, and execute the whole generated automation framework (API + UI, all
   levels) with a real-terminal live execution view, history, trends, and flakiness.

# Scope

**In scope:** everything inside `ADO_Tester/`; the product repo stays read-only.
- Slab 1: three free-text touchpoints in QA Loop + merge-refine of drafts and analysis.
- Slab 2: two-phase run (API → UI); UI test-case authoring reusing the Analyst→Tester agents; UI script
  generation via the official Playwright agents (MCP + Claude CLI); automated-login `storageState`; UI as
  the 4th level with smoke/regression/e2e buckets + an ADO "UI" suite tree; UI capture; human heal-or-bug
  triage.
- Slab 3: the Scripts Board module — inventory + filtering, flexible execution, history + trends,
  flakiness, and a true xterm.js live terminal.

**Out of scope (documented follow-ups):** Page-Object layer per module (v1 uses locators-as-consts +
shared fixtures); non-Claude-CLI providers for UI script-gen (agentic MCP is Claude-CLI-only for now);
"sync approved specs to the product repo's `automation/` folder" for native CI; run/artifact retention
policy.

# What already exists (grounding, with references)

- **QA Loop:** `QaLoopOrchestrator` (singleton) — Analyze (`ModuleAnalyzerAgent.AnalyzeInventoryAsync`
  → `InventoryDoc` + `SurfaceMatrix`) → gate `AwaitingReview` → Generate (3 level agents fan out under
  `SemaphoreSlim(4)`) → `Drafted` → Push. `ApiTestDraft` carries `Level`, `SpecCode`, `SpecFilePath`,
  `Tags`, `SourceRoutesJson`, `CaseIndex`, `AdoTestCaseId`. Specs →
  `{DataDirectory}/{scripts}/{module}/{level.ToDirName()}/{slug}.spec.ts`; push rewrites
  `[<tcId-pending>]`→`[<id>]` on title + disk. `TestLevel.Ui` is reserved (`Enums.cs:64`),
  `ToDirName(Ui)`→`uis`.
- **Runner:** `PlaywrightRunner` selects via `playwright.config testMatch` (SpecFilePaths) or `--grep`
  tag alternation; reporters junit + json + allure; injects `AUTOMATION_PROFILE/BASE_URL/BEARER` on the
  child env; **explicitly no capture** (API MVP). `RunExecutionService` maps JUnit → ADO via the
  `\[(\d+)\]` regex + TestPoints + `CreateAdoTestRunAsync`/`UpdateAdoTestResultsAsync`/
  `CompleteAdoTestRunAsync`; writes `TestFailure` rows with deterministic `BugDraftJson`; `TestRun` has
  `LevelBreakdownJson`.
- **Author Tests (pattern to port):** singleton `AgentOrchestrator` runs
  Fetch→Retrieve→Discover→Analyst→(gate `AwaitingTester`)→Tester; **manual cases only** — no scripts.
  Free-text (`AdditionalContext`/`TesterContext`) + `RerunAnalystAsync` + `RefineTesterAsync` +
  `ReconcileDrafts` merge (`AgentOrchestrator.cs:643, :540`). `ICodeContextService` /
  `IExistingTestsIndexService` are reusable singletons.
- **Legacy UI generator (still wired):** `GeneratorAgent.GenerateE2eSpecAsync` +
  `generator_e2e_system.txt` emit a React/MUI browser Playwright spec (storageState auth, semantic
  locators as consts, `[id] @smoke` tag). `SpecNormalizer` does deterministic env/route fixups.
  **No planner and no healer agent exist.** — Slab 2 replaces this legacy path with the **official
  Playwright agents** per the locked decision.
- **Frontend:** a new module = 4 edits (`routes/paths.ts`, `api/*.ts`, `pages/*Page.tsx`, nav+route in
  `layout/Layout.tsx`) — Schema Explorer is the worked example. Live SSE console = `AgentConsole` +
  `hooks/useEventStream.ts` (`ConsoleEvent` shape); a `run` phase already exists. `QaRunsPage` is the
  list+drawer archetype.

# Slab designs

## Slab 1 — Free-text + merge-refine in QA Loop  *(build first)*

**Data (`ApiTestSession`, one additive migration):** `AdditionalContext`, `GenerateContext`,
`LastFeedback` (all `TEXT NULL`).

**Agents (system prompts byte-exact; new text in USER prompts):** `ModuleAnalyzerAgent` gains an
`## Additional Context (from tester)` section + a new `RefineInventoryAsync(prior, feedback, …)` with
`## Reviewer Feedback (refine, do not restart)`; the three generators gain an `## Extra Context (from
tester)` section (from `GenerateContext`) + a refine overload. Free-text is also fed to QA Loop's
existing `FetchSourceAsync` as search hints (no new `ICodeContextService` wiring — QA Loop already
fetches its own source and de-dupes via the coverage matrix).

**Orchestrator:** `RerunAnalyzeAsync(sessionId, feedback)` → `Analyzing` → `RefineInventoryAsync` →
`AwaitingReview`. `RefineDraftsAsync(sessionId, overallFeedback, perDraft[])` → `Drafting` → re-invoke
the correct level generator per targeted draft → **`ReconcileDrafts`-style merge** (update targeted
Draft/Rejected rows, insert new, **skip Approved + Pushed entirely**) → `Drafted`.

**API (`/api/qaloop`):** `scan/start` gains `additionalContext?`; `generate-drafts` gains
`generateContext?`; new `POST sessions/{id}/rerun-analyze {feedback}` and `POST sessions/{id}/refine-drafts
{overallFeedback?, perDraft:[{draftId,feedback?,remove}]}` (both 202).

**Frontend (`session/configs/qaLoop.tsx`):** an "Additional context" field on the scan form; a "Context
for generators" box + a "Refine analysis with feedback" box→button on the coverage step; per-draft
feedback + remove toggle + overall box + "Regenerate with feedback" on the drafts step. Controls ported
from `authorTests.tsx`.

## Slab 2 — Two-phase run: API phase → UI phase  *(build second)*

**One QA-Loop run, two phases.** Phase 1 (API) unchanged. Phase 2 (UI) is toggle-gated on the scan form
and generic across any module.

1. **Author UI test cases** — reuse the Analyst→Tester **agents** with Slab-1 free-text + KB + house
   style; **no code discovery** (grounding is the live app, not the repo). Output = manual UI test cases,
   each auto-bucketed **smoke / regression / e2e** (agent proposes, user overrides). → human review/approve.
2. **Generate UI scripts via the official Playwright agents.** The framework root is initialized once
   with `npx playwright init-agents --loop=claude`, producing `.claude/agents/{planner,generator,healer}.md`
   + `.mcp.json` (Playwright MCP). For each **approved** UI case, the backend spawns the **Claude CLI in
   agentic/MCP mode** (cwd = framework root, so it loads those agent defs), feeding the case as the
   planner's feature spec: **planner** explores the live app via MCP → Markdown plan; **generator** turns
   the plan into a verified `.ui.spec.ts` (title `[<tcId-pending>] @<bucket>`); **healer** is not run
   here. → human review/approve scripts.
   > This agentic MCP invocation is distinct from the single-shot `ILlmClient` completions and must run
   > WITH the framework `.claude/` + `.mcp.json` — i.e. NOT the QAL-069 `data/llm-scratch` isolation.
   > UI script-gen is **Claude-CLI-only**.
3. **Execute** — UI = the **4th level**; "all levels" includes it. Each script `[<tcId>]`-linked to its
   ADO Test Case; UI runs create ADO Test Runs like API. **Capture ON** for UI
   (`trace:'on-first-retry'`, `screenshot:'only-on-failure'`) — a deliberate divergence from the API
   no-capture rule; it feeds the healer.
4. **Failure handling = human-triaged batch**, reusing the existing QA Runs triage / file-bugs flow.
   A run's failures land in the triage view → **QA analyzes why each failed first** → multi-selects (or
   all) → **Heal** (spawn the healer agent on those specs via MCP → re-emit → user re-approves) **or**
   **Draft bugs** against the linked ADO Test Cases (existing `TestFailure` + `BugDraftJson` +
   `file-bugs`). **The healer never fires automatically.**
5. **Feedback after authoring** — the Slab-1 merge-refine box also applies to UI cases before script-gen.

**Storage / ADO divergence:** UI needs three sub-buckets, so the flat `level→dir` rule is extended:
scripts → `{scripts}/{module}/ui/{smoke|regression|e2e}/{slug}.ui.spec.ts`; the ADO push resolves/creates
a **"UI" suite with smoke/regression/e2e child suites**. `ApiTestDraft` gains a `Bucket` column (UI only)
+ an artifact-type marker (manual-case vs generated-script).

**App auth (prerequisite) = automated login global-setup:** app URL + credentials stored in Settings
(secret); a Playwright global-setup logs in and mints a `storageState` reused by both the agents
(generation) and the runner (execution), auto-refreshed on expiry. New keys `qaloop.app_url` +
`qaloop.app_username` / `qaloop.app_password` (secret); `storageState` persisted under the framework
`fixtures/`.

## Slab 3 — Scripts Board module  *(build third)*

A new top-level module (lighter `QaRunsPage`-style list+drawer page; 4-file add) that **coexists** with
QA Runs and shares the `PlaywrightRunner` + report backend. QA Runs keeps session-scoped + pipeline-driven
runs and triage; Scripts Board owns the framework-wide script inventory + ad-hoc execution + analytics.

- **Identity / discovery:** DB draft rows are the source of truth (they know
  module/type/level/bucket/tcId/path/tags); additionally a **`// @qa-meta {module,type,level,bucket,tcId,
  sourceDraftId,generatedAt,specHash}` header is baked into every generated spec** so the board
  reconciles disk↔DB, flags manually-added or drifted specs, and never loses track.
- **Framework layout:**
  ```
  scripts/
    playwright.config.base.ts        shared projects + reporters
    fixtures/    auth(storageState), base, data helpers
    {module}/
      contracts|endpoints|workflows/  *.spec.ts        (API)
      ui/ smoke|regression|e2e/        *.ui.spec.ts     (UI)
    node_modules/  (install-once runtime, junctioned — exists today)
  ```
- **Execution options:** run by single spec / bucket / level / type / module / tag / "all"; headed vs
  headless; worker count — spawned through `PlaywrightRunner` (its `testMatch` selection already accepts
  arbitrary file sets).
- **Live CLI (true xterm.js):** a raw-ANSI streaming path — spawn Playwright with `FORCE_COLOR=1`, stream
  raw stdout/stderr chunks over a new SSE event kind (`term`) into an `xterm.js` instance in `client/`
  (`xterm` + `xterm-addon-fit`). The line-parsed `AgentConsole` path stays for existing session/run streams.
- **v1 capabilities (all four):** inventory + filtering + per-spec drill-down; flexible execution;
  history + **trends** (Allure history is already kept keyed by module/pipeline); **flakiness** detection
  (pass-rate / retry deltas across runs). Report + ADO Test Run deep links throughout.

# Cross-cutting invariants (all slabs)

- **Byte-exact ADO parity stays green** — new agent instructions live in **user** prompts; `AdoGoldenTests`
  must pass; new ADO write paths (the UI suite tree) get their own golden coverage.
- **Background-task-with-own-DI-scope** for every long-running action; SSE via `IEventBus`; cancellation
  via `CancellationRegistry`; concurrency via `PipelineConcurrency`.
- **EF migrations** additive, apply on startup via `DatabaseInitializer`; new `qaloop.*` settings follow
  the two-layer (.env → Options → RuntimeSettings overlay) pattern + `AppSettingsService` registration.

# Verification (per slab, before its docs/changelog are marked done)

- **Slab 1:** `dotnet build`/`dotnet test` (esp. `AdoGoldenTests`); migration applies; manual — scan a
  module with skip/scope prose → matrix respects it; feedback on a level → only targeted drafts change,
  Approved/Pushed untouched; `npm run build`/`npm run lint`.
- **Slab 2:** `init-agents --loop=claude` seeds `.claude/agents/` + `.mcp.json`; automated login mints a
  `storageState` against the real app; a UI-toggle run authors → approves → planner/generator emit a
  passing `.ui.spec.ts` against the live app; a forced failure → triage → Heal fixes it AND the Draft-bug
  path files against the linked ADO TC; the UI ADO Test Run shows per-TC Pass/Fail; the product repo stays
  git-clean.
- **Slab 3:** the board lists every generated spec reconciled disk↔DB (incl. a hand-added spec flagged);
  each execution option spawns correctly; xterm.js renders live ANSI; trends + flakiness populate across
  ≥ 2 runs.

# Reference documents

- `docs/qa-loop-reshape/solution.md` — the API-phase design this builds on.
- `docs/progress/RalphLoop/author-tests-enhancements/solution.md` — the free-text + refine + shared
  services pattern Slab 1 ports and Slab 2 reuses.
- `CLAUDE.md` — architecture invariants (DI root, background-task DI scope, byte-exact parity, settings).
- Playwright test agents — `npx playwright init-agents --loop=claude` (planner/generator/healer +
  `.mcp.json`); Playwright MCP (`@playwright/mcp`); healer "fix the test or file a bug" model.
- Reference dashboard — `github.com/alaintruong77/playwright-dashboard` (design DNA for Scripts Board;
  Scripts Board adds history/trends/flakiness/filtering it lacks).
