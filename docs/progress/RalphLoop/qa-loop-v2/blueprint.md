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

## SLAB 2 — UI testing phase  *(detailed)*

Pattern source: Slab 1 orchestrator methods (`RerunAnalyzeInnerAsync`, `RefineDraftsInnerAsync`);
background-task-with-own-scope pattern; `AgentOrchestrator.RunAnalystInnerAsync` for agent styling.
Verification: use story **177540** with Anthropic provider model **`claude-haiku-4-5-20251001`** for all
manual test runs.

---

### QLV2-201: Data layer

#### 1. `backend/src/QaAutomation.Core/Models/Enums.cs`

Add after `QaLoopEnumExtensions`:

```csharp
// ── UI phase enums (Slab 2, QLV2-201) ────────────────────────────────────────

public enum ApiDraftKind
{
    [EnumMember(Value = "api_case")]   ApiCase,
    [EnumMember(Value = "ui_case")]    UiCase,
    [EnumMember(Value = "ui_script")]  UiScript,
}

public enum ApiUiStatus
{
    Authoring, AwaitingReview, Drafting, Drafted, Pushing, Done, Failed, Cancelled,
}

public static class UiPhaseEnumExtensions
{
    public static string ToWire(this ApiDraftKind v) => v switch
    {
        ApiDraftKind.UiCase   => "ui_case",
        ApiDraftKind.UiScript => "ui_script",
        _                     => "api_case",
    };
    public static ApiDraftKind ToApiDraftKind(string s) => s switch
    {
        "ui_case"   => ApiDraftKind.UiCase,
        "ui_script" => ApiDraftKind.UiScript,
        _           => ApiDraftKind.ApiCase,
    };
}
```

Also fix `ToDirName` in `QaLoopEnumExtensions` (Ui was "uis", change to "ui"):
```csharp
public static string ToDirName(this TestLevel v) => v switch
{
    TestLevel.Ui => "ui",
    _            => v.ToWire() + "s",
};
```

#### 2. `backend/src/QaAutomation.Core/Models/ApiTestSession.cs`

Add to `ApiTestSession` after `ActivityEnabled`:
```csharp
public bool HasUiPhase { get; set; }
public ApiUiStatus? UiStatus { get; set; }
```

Add to `ApiTestDraft` after `SortOrder`:
```csharp
public ApiDraftKind? DraftKind { get; set; }
public string? Bucket { get; set; }
```

#### 3. `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs`

After `dmsProfileConverter`, add:
```csharp
var draftKindConverter = new ValueConverter<ApiDraftKind?, string?>(
    v => v.HasValue ? v.Value.ToWire() : null,
    s => s != null ? UiPhaseEnumExtensions.ToApiDraftKind(s) : (ApiDraftKind?)null);
var uiStatusConverter = new ValueConverter<ApiUiStatus?, string?>(
    v => v.HasValue ? v.Value.ToString() : null,
    s => s != null ? Enum.Parse<ApiUiStatus>(s) : (ApiUiStatus?)null);
```

In `ApiTestSession` entity block after `ActivityEnabled`:
```csharp
e.Property(x => x.HasUiPhase).HasColumnName("HasUiPhase").IsRequired().HasDefaultValue(false);
e.Property(x => x.UiStatus).HasColumnName("UiStatus").HasConversion(uiStatusConverter);
```

In `ApiTestDraft` entity block after `SortOrder`:
```csharp
e.Property(x => x.DraftKind).HasColumnName("DraftKind").HasConversion(draftKindConverter);
e.Property(x => x.Bucket).HasColumnName("Bucket");
```

#### 4. Migration command

```powershell
dotnet ef migrations add AddQaLoopUiPhase `
  --project src\QaAutomation.Infrastructure --startup-project src\QaAutomation.Api
```

---

### QLV2-202: Settings + global-setup scaffold

#### 1. `QaLoopOptions.cs` — add after `FrontendBaseUrl`

```csharp
public string AppUrl      { get; set; } = "";
public string AppUsername { get; set; } = "";
public string AppPassword { get; set; } = "";
```

#### 2. `DependencyInjection.cs` — `PostConfigure<RuntimeSettings>` for QaLoopOptions

```csharp
o.AppUrl      = rt.Get("qaloop.app_url")      ?? o.AppUrl;
o.AppUsername = rt.Get("qaloop.app_username")  ?? o.AppUsername;
o.AppPassword = rt.Get("qaloop.app_password")  ?? o.AppPassword;
```

#### 3. `AppSettingsService.cs` — add three new `KnownKey` entries

```csharp
new("qaloop.app_url",      "QA Loop UI: base URL of the app under test"),
new("qaloop.app_username", "QA Loop UI: browser-login username"),
new("qaloop.app_password", "QA Loop UI: browser-login password", IsSecret: true),
```

#### 4. `scripts/fixtures/auth.setup.ts` — scaffold

Reads `AUTOMATION_APP_URL` / `_USERNAME` / `_PASSWORD` env vars; headless Chromium login;
saves `storageState.json` to the same `fixtures/` dir. Exits gracefully if any env var is missing.

---

### QLV2-203: UI test-case authoring

#### 1. Embed `ui_case_author_system.txt`

Create `backend/src/QaAutomation.Infrastructure/Agents/Prompts/ui_case_author_system.txt`.
Mark as EmbeddedResource. Add `AgentPrompts.UiCaseAuthorSystem` static property in `AgentPrompts.cs`.

System prompt tone: you are a QA analyst generating manual UI test cases for a web module. For each
user flow in the inventory, emit a JSON array of `{title, preconditions, steps:[{action,expected}], bucket}`
objects. Bucket must be one of "smoke" / "regression" / "e2e". Follow the existing house style (no code,
numbered steps, concrete expected results). Emit only the JSON array.

#### 2. New `UiCaseAuthorAgent.cs`

`Infrastructure/Agents/UiCaseAuthorAgent.cs` — `AuthorCasesAsync(module, inventory, additionalContext?, …) → List<UiCaseDraft>`.
Parses `[` root (JsonArray). Each element: extract `title`/`preconditions`/`steps`/`bucket`.
Throws `AgentException` on bad shape (same as other agents).

#### 3. `QaLoopOrchestrator` — `AuthorUiCasesAsync` + inner

Set `UiStatus=Authoring` → `AuthorUiCasesAsync` → insert `ApiTestDraft` rows (`Level=Ui`, `DraftKind=UiCase`,
`Bucket`, `StepsJson`, `SpecCode=""`, `Status=Draft`) → `UiStatus=AwaitingReview`.

#### 4. `IQaLoopOrchestrator` entry + `QaLoopContracts` + controller

Add `Task AuthorUiCasesAsync(int sessionId, CancellationToken ct = default)`.
`ApiSessionDetail` += `HasUiPhase`, `UiStatus`.
Controller: `POST sessions/{id}/author-ui-cases` → 202.

---

### QLV2-204: Playwright agents harness

#### 1. One-time init (documented setup step)

```bash
cd scripts && npx playwright init-agents --loop=claude
```
Commits `.claude/agents/` + `.mcp.json` to the repo.

#### 2. `IPlaywrightAgentService` / `PlaywrightAgentService`

`Core/Abstractions/IPlaywrightAgentService.cs`:
```csharp
Task<string> GenerateScriptAsync(string caseTitle, string stepsJson, string bucket,
    int tcIdPending, IProgress<string>? progress, CancellationToken ct);
Task HealAsync(string specFilePath, string failureMessage,
    IProgress<string>? progress, CancellationToken ct);
```

`Infrastructure/QaLoop/PlaywrightAgentService.cs` — spawns `claude --output-format stream-json` with
cwd = scripts root; reads stdout for the generated file path. Register as scoped.

#### 3. `QaLoopOrchestrator.GenerateUiScriptsAsync` + inner

For each Approved `UiCase` draft (sequential): call service → create `UiScript` draft sibling with
`SpecCode`/`SpecFilePath`/`DraftKind=UiScript`/`Status=Draft`. Sets `UiStatus=UiDrafted` on completion.

Add to `IQaLoopOrchestrator` + controller `POST sessions/{id}/generate-ui-scripts` → 202.

---

### QLV2-205: Execution

#### 1. `PlaywrightRunner` — `EnableCapture` flag

`RunPlaywrightAsync(…, bool enableCapture = false)`: when true injects
`trace:'on-first-retry', screenshot:'only-on-failure'` into config. API runs always false.

#### 2. `RunExecutionService` — include `UiScript` drafts

Include drafts with `DraftKind=UiScript` or null in spec-paths; pass `enableCapture:true` for any run
that contains UI-level specs.

#### 3. `EnsureLevelSuitesAsync` — UI suite tree

Resolve/create "UI" parent suite → "UI – Smoke" / "UI – Regression" / "UI – E2E" child suites.
Return `Dictionary<string, int>` bucket → suite id.

#### 4. ADO push — route `UiScript` to bucket suite

`PushAllAsync`/`PushDraftAsync`: for `DraftKind=UiScript` use bucket suite id. Tags: `@ui @<bucket>`.
Add golden test for UI Test Case write + suite linkage.

---

### QLV2-206: Heal action

#### 1. `IPlaywrightAgentService.HealAsync` (already declared in QLV2-204)

Spawns healer agent (from `.claude/agents/healer.md`) in agentic mode; feeds spec path + failure message;
healer rewrites spec in place; backend reads file back → updates `SpecCode`.

#### 2. `QaLoopOrchestrator.HealSpecsAsync` + inner

Load each `UiScript` draft; call `HealAsync`; update `SpecCode` + set `Status=Draft`.

Add to `IQaLoopOrchestrator` + controller `POST sessions/{id}/heal-specs` body `{draftIds:int[]}` → 202.

---

### QLV2-207: Frontend

#### 1. `client/src/api/qaLoop.ts`

- `ScanStartRequest += hasUiPhase?: boolean`
- `ApiSessionDetail += hasUiPhase: boolean, uiStatus?: string | null`
- `ApiTestDraftDto += draftKind?: string | null, bucket?: string | null`
- Add `authorUiCases(id)`, `generateUiScripts(id)`, `healSpecs(id, draftIds: number[])`.

#### 2. `client/src/session/configs/qaLoop.tsx`

- Scan form: UI phase `<Switch>` toggle.
- UI authoring step (shown when `hasUiPhase && uiStatus matches Authoring/AwaitingReview`): case list
  with bucket chip + override dropdown + per-case approve/reject + "Author UI Cases" button.
- Script-gen step (UiDrafting/UiDrafted): spec viewer + approve + "Generate UI Scripts" button.
- QA Runs triage: "Heal selected" multi-select → `healSpecs`.

---

### QLV2-208: Tests + docs

`QaLoopOrchestratorTests.cs` — three new tests:
1. `AuthorUiCases_creates_UiCase_drafts_with_bucket`
2. `GenerateUiScripts_creates_UiScript_drafts`
3. `HealSpecs_resets_status_to_Draft`

Feature doc: `docs/features/qa-loop-ui-phase.md` + `docs/changelog/` entry.

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
