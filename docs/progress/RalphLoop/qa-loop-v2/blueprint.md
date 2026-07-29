# Blueprint — QA Loop v2 (file-by-file implementation guide)

One feature, three slabs, **one doc set**. This guide is **fully detailed for Slab 1** (built first);
Slabs 2–3 are structural outlines that get expanded to full file-level detail when we reach them (their
designs are locked in `solution.md`). All work respects the invariants in `solution.md` → "Cross-cutting
invariants" (byte-exact ADO parity, background-task-own-DI-scope, additive migrations, two-layer settings).

---

## SLAB 1 — Free-text + merge-refine in QA Loop  *(detailed)*

Pattern source (mirror closely): `AgentOrchestrator.RerunAnalystInnerAsync` (`:472`),
`RefineTesterInnerAsync` (`:540`), `ReconcileDrafts` (`:643`); Author Tests free-text UI in
`client/src/session/configs/authorTests.tsx` (`:832` context field, `:615-632` analyst refine,
`:715-730` tester refine).

### 1. `backend/src/QaAutomation.Core/Models/ApiTestSession.cs`
Add three nullable columns to `ApiTestSession` (after `AnalysisJson`/before `EventLogJson`):
- `public string? AdditionalContext { get; set; }` — scan-start prose (what/extent/skip).
- `public string? GenerateContext { get; set; }` — gate prose steering the generators.
- `public string? LastFeedback { get; set; }` — most recent refine feedback (persisted for reload).

### 2. EF migration
`dotnet ef migrations add AddQaLoopFreeTextContext --project src/QaAutomation.Infrastructure
--startup-project src/QaAutomation.Api`. Three additive nullable `TEXT` columns on `ApiTestSessions`;
no `QaDbContext` config needed (nullable strings map by convention). Applies on startup via
`DatabaseInitializer`.

### 3. `backend/src/QaAutomation.Infrastructure/Agents/ModuleAnalyzerAgent.cs`
- `AnalyzeInventoryAsync(...)` + `BuildInventoryPrompt(module, sourceBundle, existingCases, string?
  additionalContext = null)` — append, only when non-empty, a section:
  `## Additional Context (from tester)\n<additionalContext>` (before "Output the inventory..."). Thread
  `additionalContext` from the orchestrator.
- New `RefineInventoryAsync(module, InventoryDoc priorInventory, SurfaceMatrix priorSurface, string
  feedback, IReadOnlyList<LinkedTestCase> existingCases, string? additionalContext, fetchFile?, progress,
  emit, ct) → ModuleAnalysis`. Build a prompt: prior inventory + surfaceMatrix JSON + `## Reviewer
  Feedback (refine, do NOT restart)\n<feedback>` + the same output instruction; reuse
  `JsonUtils.CompleteJsonAsync` + `ParseAnalysis`. System prompt (`AgentPrompts.ModuleAnalyzerSystem`)
  **unchanged**.

### 4. Generator agents — `ContractGeneratorAgent.cs`, `EndpointGeneratorAgent.cs`, `WorkflowGeneratorAgent.cs`
For each `GenerateAsync(...)`: add `string? extraContext = null`; when non-empty inject a
`## Extra Context (from tester)\n<extraContext>` section into the **user** prompt builder (system prompts
untouched → `AdoGoldenTests` stay green). Add a refine overload
`RefineAsync(module, <route|workflow>, string priorSpec, IReadOnlyList<TestCaseStep> priorSteps, string
feedback, string? extraContext, …) → GeneratedSpec` that re-emits the spec/steps with the prior output +
`## Reviewer Feedback (refine, do NOT restart)`. (Read each agent's current `GenerateAsync`/`BuildPrompt`
signature first; keep the return type `GeneratedSpec`/`List<GeneratedSpec>` unchanged.)

### 5. `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
- `RunPipelineInnerAsync`: pass `session.AdditionalContext` into
  `AnalyzeInventoryAsync(session.Module, source, existingCases, additionalContext: session.AdditionalContext, fetchFile, …)`
  (`:250`). Optionally seed `session.AdditionalContext` tokens into the `FetchSourceAsync` search path.
- `GenerateDraftsInnerAsync`: pass `session.GenerateContext` as `extraContext` into each generator call
  (`:405,:415,:424`).
- New public `RerunAnalyzeAsync(int sessionId, string feedback, CancellationToken=default)` →
  `Launch(sessionId, ct => RerunAnalyzeInnerAsync(sessionId, feedback, ct))`. Inner: load session +
  deserialize `InventoryJson`/`AnalysisJson`; set `Status=Analyzing`, persist `LastFeedback=feedback`;
  call `RefineInventoryAsync`; re-apply the gap-selected default; persist `InventoryJson`/`AnalysisJson`;
  set `Status=AwaitingReview` + `PersistLog`. Model on `RerunAnalystInnerAsync` (`:472`).
- New public `RefineDraftsAsync(int sessionId, string? overallFeedback, IReadOnlyList<ApiDraftFeedback>
  perDraft, CancellationToken=default)` → `Launch(… RefineDraftsInnerAsync …)`. Inner: set
  `Status=Drafting`, persist `LastFeedback`; load drafts; compute `feedbackByDraft` +
  `removeIds`; **`toRemove` = flagged drafts whose Status ∈ {Draft, Rejected}** (never Approved/Pushed);
  for each **targeted** draft with Status ∈ {Draft, Rejected}, re-invoke the matching level generator
  (`RefineAsync`) with its prior spec/steps + feedback + `session.GenerateContext`; then a
  **`ReconcileApiDrafts`** helper (mirror `ReconcileDrafts` `:643`): update targeted rows in place
  (SpecCode/StepsJson/Title/SchemaJson), rewrite the on-disk spec via the existing `PersistDraft`/
  disk-write path, insert `ref:"new"` items, remove `toRemove`, **skip Approved+Pushed entirely** (do
  NOT reset Approved→Draft — user's locked stricter rule). Set `Status=Drafted` + `PersistLog`.
- Add both methods to `IQaLoopOrchestrator` (`Infrastructure/Orchestrators/IQaLoopOrchestrator.cs`).

### 6. `backend/src/QaAutomation.Core/Contracts/QaLoopContracts.cs`
- `ScanStartRequest`: add `string? AdditionalContext`.
- generate-drafts body: add `string? GenerateContext` (create `GenerateDraftsRequest` if the endpoint
  currently takes no body).
- New `RerunAnalyzeRequest(string Feedback)`; `RefineDraftsRequest(string? OverallFeedback,
  List<ApiDraftFeedback> PerDraft)`; `ApiDraftFeedback(int DraftId, string? Feedback, bool Remove)`.
- `ApiSessionDetail.From(...)`: surface `AdditionalContext`, `GenerateContext`, `LastFeedback` so the FE
  re-seeds inputs on reload.

### 7. `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
- `scan/start`: persist `req.AdditionalContext` onto the seeded session.
- `generate-drafts`: write `req.GenerateContext` onto the session before calling `GenerateDraftsAsync`.
- New `POST sessions/{id}/rerun-analyze` (body `RerunAnalyzeRequest`) → `RerunAnalyzeAsync` → 202.
- New `POST sessions/{id}/refine-drafts` (body `RefineDraftsRequest`) → `RefineDraftsAsync` → 202.

### 8. `client/src/api/qaLoop.ts`
- Extend `startScan` body with `additionalContext`; `generateDrafts` body with `generateContext`.
- Add `rerunAnalyze(sessionId, feedback)` and `refineDrafts(sessionId, {overallFeedback, perDraft})`.
- Add the three context fields to the session detail type.

### 9. `client/src/session/configs/qaLoop.tsx`
- `QaLoopStartForm`: add a multiline "Additional context (optional)" field (what to focus / skip) →
  into `startScan`. (Port `authorTests.tsx:832`.)
- Coverage/review step (`AwaitingReview`): a "Context for generators" box (saved into `generateDrafts`)
  + a "Refine analysis with feedback" box→button → `rerunAnalyze`. (Port `:615-632`.)
- Drafts step: per-draft feedback field + a "remove" toggle on each non-Approved/non-Pushed draft, an
  overall-feedback box, and a "Regenerate with feedback" button → `refineDrafts`. Approved/Pushed cards
  render locked. (Port `:715-730`.)

### 10. Tests — `backend/src/QaAutomation.Tests/QaLoopOrchestratorTests.cs`
- Refine merge: targeted Draft updated in place; `ref:"new"` inserted; a flagged Draft removed; an
  **Approved and a Pushed draft left untouched** (content + status). Analyzer refine returns a refreshed
  matrix and returns the session to `AwaitingReview`. Keep `AdoGoldenTests` green (no ADO-body changes).

**Slab 1 verification:** `dotnet build`/`dotnet test` (esp. `AdoGoldenTests`); migration applies on
startup; `npm run build`/`npm run lint`; manual — scan a module with skip/scope prose → matrix respects
it; refine a level → only targeted drafts change, Approved/Pushed untouched.

---

## SLAB 2 — UI testing phase  *(outline — expand when reached)*

Locked design in `solution.md` → Slab 2. Expected file-level work:
- **Data:** `ApiTestDraft` gains `Bucket` (smoke/regression/e2e; UI only) + an artifact-type marker
  (manual-case vs generated-script); a `TestType`/phase marker on `ApiTestSession` (API vs UI phase).
  Extend `ToDirName`/path logic so UI writes `{module}/ui/{bucket}/{slug}.ui.spec.ts`.
- **Settings:** `qaloop.app_url`, `qaloop.app_username`, `qaloop.app_password` (secret) — two-layer wiring
  + `AppSettingsService`. Playwright global-setup that mints `storageState` under framework `fixtures/`.
- **UI authoring:** reuse Analyst→Tester agents (no code discovery) driven by module + Slab-1 free-text;
  emit manual UI cases with a proposed bucket; human review/approve; Slab-1 merge-refine applies.
- **Playwright agents harness:** one-time `npx playwright init-agents --loop=claude` into the framework
  root (`.claude/agents/{planner,generator,healer}.md` + `.mcp.json`); a new **agentic Claude-CLI+MCP**
  invocation path (distinct from `ILlmClient`; cwd = framework root; NOT the QAL-069 llm-scratch
  isolation) that runs planner→generator per approved case → verified `.ui.spec.ts` (`[<tcId-pending>]
  @<bucket>`). Human review/approve scripts.
- **Execution:** UI as the 4th level in `PlaywrightRunner`/`RunExecutionService`; capture ON for UI
  (`trace:'on-first-retry'`, `screenshot:'only-on-failure'`); ADO push resolves/creates a "UI" suite with
  smoke/regression/e2e child suites (extend the existing `EnsureLevelSuitesAsync` at
  `QaLoopOrchestrator.cs:103`).
- **Failure triage:** reuse QA Runs triage/file-bugs; add a batch **Heal** action (spawn the healer agent
  via MCP on multi-selected/all failed specs → re-emit → re-approve) alongside the existing Draft-bugs
  path. Healer never auto-fires.

## SLAB 3 — Scripts Board module  *(outline — expand when reached)*

Locked design in `solution.md` → Slab 3. Expected file-level work:
- **Identity:** bake a `// @qa-meta {module,type,level,bucket,tcId,sourceDraftId,generatedAt,specHash}`
  header into every generated spec (generators + a backfill); a reconcile service (DB rows ⟷ disk scan)
  flagging manual/drifted specs.
- **Backend:** a scripts-board controller + service reusing `PlaywrightRunner` (its `testMatch`
  selection already accepts arbitrary file sets) for execution by spec/bucket/level/type/module/tag/all +
  headed/headless + workers; history/trends from the existing Allure history store; a flakiness metric
  (pass-rate / retry deltas across runs); a **raw-ANSI `term` SSE stream** (spawn with `FORCE_COLOR=1`,
  stream raw stdout/stderr chunks).
- **Frontend:** new module = `routes/paths.ts` + `api/scriptsBoard.ts` + `pages/ScriptsBoardPage.tsx` +
  nav/route in `layout/Layout.tsx` (Schema Explorer pattern); an `xterm.js` terminal component
  (`xterm` + `xterm-addon-fit`) fed by the `term` stream; inventory grid + filtering + per-spec drawer +
  trends + flakiness. Coexists with QA Runs.
