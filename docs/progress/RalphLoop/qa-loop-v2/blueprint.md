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

## SLAB 3 — Scripts Board module  *(detailed)*

Pattern source: `PlaywrightRunner`/`RunExecutionService` (`backend/src/QaAutomation.Infrastructure/QaRuns/`)
for execution + Allure history; `QaRunsController`'s `Task.Run` + own `CreateScope()` + `RunCancellationRegistry`
(`Api/Controllers/QaRunsController.cs:99-133`) for the background-task pattern; `QaRunsPage.tsx` for the
list+drawer archetype; Schema Explorer for the 4-file frontend module add. Verification: use story
**177540** for every task's exercised run (see `.cursor/rules/ralph-loop-ops.mdc`).

**Key grounding facts (see explore-agent report in `progress.md` Slab 3 decomposition entry):**
- `PlaywrightRunner.RunAsync(PlaywrightRunSpec, eventKey, ct)` does **not** require a QA-Loop session —
  only needs `SpecFilePaths` (or `GrepTags`) + `Module` (used for `testDir`/Allure history key only).
  Reusable as-is for ad-hoc Scripts Board execution.
- Artifacts live at `{DataDirectory}/{RunsFolderName=runs}/{spec.RunId}/` — a **flat int keyed by
  `TestRun.Id`**. A brand-new `ScriptBoardRun` table would autoincrement from 1 too and **collide** on
  disk with `TestRun` ids. Fix: Scripts Board always calls `PlaywrightRunner` with
  **`spec.RunId = -scriptBoardRun.Id`** (negative) — guaranteed disjoint from `TestRun`'s positive ids,
  zero changes needed to `PlaywrightRunner`'s directory scheme. `RunCancellationRegistry` is just an
  `int`-keyed dictionary, so negative keys work there too (reuse the **same singleton registry**, do not
  add a second one).
- Allure history is file-based, not DB: `{DataDirectory}/allure-history/{module|_all}/{pipelineId|manual}/history.jsonl`,
  written by `PlaywrightRunner.HistoryFile` (private) via `allure … --history-path`. Scripts Board ad-hoc
  runs naturally land under `.../manual/` (no `TriggerPipelineId`) — already directly comparable/trend-able
  against QA-Runs manual runs for the same module.
- `ApiTestDraft` (`Core/Models/ApiTestSession.cs:52-83`) is the DB identity source: `Level`, `SpecFilePath`,
  `DraftKind` (null/UiCase/UiScript), `Bucket`, `Tags`, `AdoTestCaseId`, `Status`.
- On-disk today: API specs at `{DataDirectory}/scripts/{module}/{level.ToDirName()}/{slug}.spec.ts`
  (`contracts|endpoints|workflows`); **UI scripts (Slab 2) live at `{PlaywrightRoot}/generated/ui/{bucket}/{slug}-{tcId}.ui.spec.ts`**
  — a different root than the aspirational `scripts/{module}/ui/{bucket}/…` in `solution.md`. Slab 3 v1
  does **not** relocate Slab-2 UI output; the inventory scanner reads **both** roots as they exist today
  (documented deviation, follow-up if we ever move UI specs under `scripts/`).
- SSE `ConsoleEvent.Kind` already discriminates (`Core/Models/ConsoleEvent.cs`); adding `EventKinds.Term`
  is purely additive — existing `AgentConsole`/`Narrative` set (`{Phase,System,Result,Error,Stat}`)
  is untouched, `Term` is deliberately **excluded from `Narrative`** (live-only, like `Token`) since raw
  ANSI dumps aren't useful in a replayed narrative and would bloat `EventLogJson`.
- No `xterm`/`xterm-addon-fit` in `client/package.json` yet; no `FORCE_COLOR` handling anywhere in the repo.
- `PlaywrightRunner.SpawnAsync` (`:521-610`) uses `Process.BeginOutputReadLine()` (line-buffered events) —
  this is the tested QA-Runs path and must **not** be touched. Scripts Board's raw-ANSI live terminal uses
  a **separate, additive spawn path** in a new `ScriptsBoardRunner` (reads `process.StandardOutput.BaseStream`
  as raw bytes/chunks with `FORCE_COLOR=1`), so QA Runs' golden-tested behavior has zero risk of regression.
- No `@qa-meta`-style marker exists yet. Safe insertion point: **line 1 of the generated file, before any
  `import`**. `SpecNormalizer.Normalize` only rewrites env-var/route strings and ignores comments, so a
  leading `//` comment line survives untouched.

---

### QLV2-301: `@qa-meta` header convention

#### 1. `backend/src/QaAutomation.Core/Models/QaMeta.cs` (new)

```csharp
public sealed record QaMeta(
    string Module, string Type, string Level, string? Bucket,
    int? TcId, int? SourceDraftId, DateTime GeneratedAt, string SpecHash);

public static class QaMetaHeader
{
    // "// @qa-meta {"module":"leads","type":"api","level":"contract","bucket":null,"tcId":123,
    //   "sourceDraftId":45,"generatedAt":"2026-07-29T12:00:00Z","specHash":"..."}"
    public static string Build(QaMeta meta) => $"// @qa-meta {JsonSerializer.Serialize(meta, ...)}\n";

    // Returns null if the first line isn't a well-formed @qa-meta comment (legacy/manual specs).
    public static QaMeta? TryParse(string specCode) { /* read first line, strip "// @qa-meta ", JSON parse */ }

    // Idempotent: replaces an existing header line in-place, else prepends.
    public static string Upsert(string specCode, QaMeta meta) { ... }

    public static string ComputeHash(string specCodeWithoutHeader) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specCodeWithoutHeader)))[..12];
}
```
`Type` is `"api"` or `"ui"`. `Level` is the wire string (`contract|endpoint|workflow|ui`). Hash excludes
the header line itself (compute over the body, so re-serializing the header doesn't change the hash).

#### 2. Bake into the API spec persist path

`QaLoopOrchestrator.PersistDraft` (`:1278-1301`, wherever the on-disk `.spec.ts` is written for
Contract/Endpoint/Workflow drafts) — before `File.WriteAllTextAsync`, call
`QaMetaHeader.Upsert(specCode, new QaMeta(session.Module, "api", draft.Level.ToWire(), null,
draft.AdoTestCaseId, draft.Id, DateTime.UtcNow, QaMetaHeader.ComputeHash(specCode)))`. Applies to both the
initial generate path and `RefineDraftsAsync`'s rewrite (Slab 1, QLV2-107) — same helper, called every
time the file is (re)written, so the hash/timestamp always reflect the latest content.

#### 3. Bake into the UI script-gen path

`PlaywrightAgentService.GenerateScriptAsync` (`Infrastructure/QaLoop/PlaywrightAgentService.cs`) — after
the Claude CLI writes the `.ui.spec.ts` file and the method reads it back, call `QaMetaHeader.Upsert` with
`Type="ui"`, `Level="ui"`, `Bucket=bucket`, and rewrite the file before returning the path. Same in
`HealAsync` after the healer rewrites the spec (re-stamp `generatedAt`/`specHash`, keep the same `tcId`/
`sourceDraftId`/`bucket` — read the prior header first via `TryParse` to carry those fields forward).

#### 4. Unit tests

`QaMetaHeaderTests.cs` (new, `backend/tests/QaAutomation.Tests/`): round-trip `Build`→`TryParse`;
`Upsert` on a header-less file prepends; `Upsert` on an already-stamped file replaces in place (no
duplicate header, body untouched); `TryParse` on a plain legacy spec (no header) returns `null`.

**AC:** B/T; a freshly generated API and UI spec both start with a valid `// @qa-meta` line; `RefineDraftsAsync`
rewrites keep exactly one header line; legacy specs from before this task parse as `null` (not an error).

---

### QLV2-302: Data + inventory/reconcile service

#### 1. `backend/src/QaAutomation.Core/Models/ScriptBoardRun.cs` (new)

```csharp
public sealed class ScriptBoardRun
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    /// <summary>Human-readable description of what was selected, e.g. "module=leads, bucket=smoke".</summary>
    public string SelectionSummary { get; set; } = "";
    /// <summary>Resolved absolute spec file paths this run executed (JSON string[]), for re-run/audit.</summary>
    public string SpecFilePathsJson { get; set; } = "[]";
    public bool Headed { get; set; }
    public int? Workers { get; set; }
    public TestRunStatus Status { get; set; } = TestRunStatus.Queued;   // reuse existing enum
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? JunitPath { get; set; }
    public string? ReportPath { get; set; }
    public string? EventLogJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
```
Deliberately **no** `AdoTestRunId`/`TriggerSource`/ADO fields — Scripts Board runs are ADO-optional by
design (`solution.md` Slab 3: "Report + ADO Test Run deep links throughout" means *linking out* to a
spec's existing `AdoTestCaseId` via the inventory join, not creating new ADO Test Runs here).

#### 2. `QaDbContext` + migration

Add `DbSet<ScriptBoardRun> ScriptBoardRuns`; entity config mirrors `TestRun`'s (string enum conversion
for `Status` — reuse the existing `TestRunStatus` converter). Migration `AddScriptBoardRuns`.

#### 3. `backend/src/QaAutomation.Core/Abstractions/IScriptInventoryService.cs` (new)

```csharp
public sealed record ScriptInventoryItem(
    string SpecFilePath, string Module, string Type, string Level, string? Bucket,
    int? AdoTestCaseId, int? DraftId, string? Tags, QaMeta? Meta,
    string ReconcileState);  // "linked" | "orphan_disk" | "orphan_db" | "no_meta"

public interface IScriptInventoryService
{
    Task<IReadOnlyList<ScriptInventoryItem>> GetInventoryAsync(CancellationToken ct = default);
}
```

#### 4. `Infrastructure/ScriptsBoard/ScriptInventoryService.cs` (new, scoped — uses `QaDbContext`)

1. Scan disk: `Directory.EnumerateFiles({DataDirectory}/{ScriptsFolderName}, "*.spec.ts", AllDirectories)`
   (API) + `Directory.EnumerateFiles({PlaywrightRoot}/generated/ui, "*.ui.spec.ts", AllDirectories)` (UI,
   skip if `PlaywrightRoot` unset). Derive `Module`/`Level`/`Bucket` from the path segments (mirrors the
   known layout) as a fallback when `Meta` is null.
2. Read each file, `QaMetaHeader.TryParse` → `Meta`.
3. Load all `ApiTestDraft` rows with non-null `SpecFilePath` (+ join `ApiTestSession.Module`) into a
   `Dictionary<string, ApiTestDraft>` keyed by normalized absolute path.
4. Reconcile per disk file: DB row found + `Meta != null` → `"linked"`; DB row found + `Meta == null` →
   `"no_meta"` (legacy, pre-QLV2-301); no DB row → `"orphan_disk"` (hand-added or DB row deleted).
5. A second pass over DB rows whose `SpecFilePath` doesn't exist on disk anymore → synthetic
   `"orphan_db"` items (drifted: DB thinks a script exists, disk doesn't).
6. No caching (files are cheap to stat; call is on-demand from the controller, matches the "reconcile
   scan" language in `solution.md` — a live scan, not a persisted registry table).

#### 5. DI registration

`services.AddScoped<IScriptInventoryService, ScriptInventoryService>();` in `DependencyInjection.cs`
next to the other QaLoop/QaRuns registrations.

#### 6. Tests

`ScriptInventoryServiceTests.cs`: seed a temp scripts dir with a stamped spec (→ `linked` when a matching
draft row exists), an unstamped legacy spec with a draft row (→ `no_meta`), a hand-added file with no
draft row (→ `orphan_disk`), and a draft row whose `SpecFilePath` doesn't exist (→ `orphan_db`).

**AC:** B/T; all four reconcile states produced correctly; UI-root scan is skipped gracefully when
`PlaywrightRoot` is empty (no exception).

---

### QLV2-303: Execution service + controller

#### 1. `PlaywrightRunSpec` — two additive fields (`Core/Abstractions/IPlaywrightRunner.cs`)

```csharp
public bool Headed { get; init; } = false;
public int? Workers { get; init; }
```
`PlaywrightRunner.WriteRunConfig` — when `Headed`, add `use: { headless: false }`; `Workers` overrides
the existing `AUTOMATION_WORKERS` env default when present. Both default to today's behavior (headless,
env-driven workers) when unset — **zero behavior change for QA Runs**, which never sets these.

#### 2. `Core/Abstractions/IScriptBoardExecutionService.cs` (new)

```csharp
public interface IScriptBoardExecutionService
{
    /// <summary>Resolves a selection filter to concrete spec paths, creates a queued ScriptBoardRun row,
    /// launches it, and returns the row id immediately (fire-and-forget background execution).</summary>
    Task<int> StartAsync(ScriptBoardRunFilter filter, CancellationToken ct = default);
    Task ExecuteAsync(int runId, CancellationToken ct = default);   // called from the background Task
}

public sealed record ScriptBoardRunFilter(
    string? Module, string? Level, string? Type, string? Bucket, string? Tag,
    IReadOnlyList<string>? SpecFilePaths,   // explicit selection wins over the other filters
    bool Headed, int? Workers);
```

#### 3. `Infrastructure/ScriptsBoard/ScriptBoardExecutionService.cs` (new, scoped)

- `StartAsync`: if `filter.SpecFilePaths` non-empty, use them; else call `IScriptInventoryService.GetInventoryAsync`
  and filter in-memory by `Module`/`Level`/`Type`/`Bucket`/`Tag` (all optional = "all"). Empty result →
  throw a `400`-mappable `ArgumentException` ("no specs match the selection"). Insert `ScriptBoardRun`
  (`Status=Queued`, `SelectionSummary` built from the filter, `SpecFilePathsJson` = resolved list) →
  `SaveChangesAsync` → return `run.Id`.
- `ExecuteAsync(runId, ct)`: load the row, set `Status=Running`/`StartedAt`; build a `PlaywrightRunSpec`
  with `RunId = -runId` (the negative-namespace trick — see grounding above), `SpecFilePaths` from the
  stored JSON, `Module` = first distinct module in selection (or `"_all"` for Allure history keying when
  multi-module), `Headed`/`Workers` from the row; call `IPlaywrightRunner.RunAsync(spec, $"scripts-board-run:{runId}", ct)`;
  map the result (`Passed`/`Failed`/`Skipped`/`DurationMs`/`JunitPath`/`ReportPath`) onto the row;
  `Status=Completed` (or `Failed`/`Cancelled` per existing `TestRunStatus` semantics — mirror
  `RunExecutionService.ExecuteAsync`'s try/catch/cancellation handling exactly); persist `EventLogJson`
  from the bus narrative at completion (same pattern as `TestRun`).

#### 4. `backend/src/QaAutomation.Api/Controllers/ScriptsBoardController.cs` (new)

```
GET  /api/scripts-board/inventory                         → IScriptInventoryService.GetInventoryAsync
POST /api/scripts-board/runs        {ScriptBoardRunFilter} → StartAsync → 202 {runId}
GET  /api/scripts-board/runs                               → paginated ScriptBoardRun list (mirror QaRunsController.ListRuns)
GET  /api/scripts-board/runs/{id}                           → detail
POST /api/scripts-board/runs/{id}/cancel                    → RunCancellationRegistry.Cancel(-id) (same singleton, negative key)
GET  /api/scripts-board/runs/{id}/stream                    → SseWriter.StreamAsync(Response, bus, $"scripts-board-run:{id}", …)
```
`POST runs` background-task shape is a **direct copy** of `QaRunsController.Start`'s `Task.Run` block
(`:99-133`): `_runCancel.Register(-runId)` → `Task.Run` → `using var scope = _scopeFactory.CreateScope()`
→ resolve `IScriptBoardExecutionService` → `ExecuteAsync` → catch/log/finally `_runCancel.Remove(-runId)`.

#### 5. DI registration

`services.AddScoped<IScriptBoardExecutionService, ScriptBoardExecutionService>();`

#### 6. Tests

`ScriptBoardExecutionServiceTests.cs`: filter resolution (module+bucket narrows correctly; explicit
`SpecFilePaths` bypasses inventory filtering); empty selection throws; `ExecuteAsync` against a stub
`IPlaywrightRunner` updates the row to `Completed` with the stub's counts; a stub-thrown
`OperationCanceledException` leaves the row `Cancelled`.

**AC:** B/T; `POST /api/scripts-board/runs` round-trips end-to-end against a real tiny spec locally;
negative `RunId` namespace verified to not collide with an existing `TestRun` artifact dir in a manual check.

---

### QLV2-304: Raw-ANSI live terminal stream

#### 1. `EventKinds.Term` (`Core/Models/ConsoleEvent.cs`)

```csharp
public const string Term = "term";
// Narrative set is unchanged — Term is deliberately live-only, like Token/Thinking.
```

#### 2. `Infrastructure/ScriptsBoard/ScriptsBoardRunner.cs` (new — separate from `PlaywrightRunner`)

A thin sibling used **only** by `ScriptBoardExecutionService`, so `PlaywrightRunner`'s golden-tested
`SpawnAsync` is never touched. Reuses `PlaywrightRunner`'s public config-writing by extracting the
existing private `WriteRunConfig`/testDir/testMatch logic into an `internal static` helper class
(`PlaywrightConfigWriter`) that both `PlaywrightRunner` and `ScriptsBoardRunner` call — a pure refactor
of `PlaywrightRunner` (move code, no behavior change; existing `PlaywrightRunnerTests` must stay green).

`ScriptsBoardRunner.RunWithLiveTermAsync(PlaywrightRunSpec spec, string eventKey, CancellationToken ct)`:
- Writes the run config via the shared `PlaywrightConfigWriter` (same reporters: junit/json/allure).
- Spawns `npx playwright test …` with `psi.Environment["FORCE_COLOR"] = "1"` added on top of
  `PlaywrightRunner.ApplyAutomationEnv` (reused as-is, `internal` visibility already fits).
- Instead of `BeginOutputReadLine()`, reads `process.StandardOutput.BaseStream` /
  `process.StandardError.BaseStream` directly in a loop (`byte[4096]` buffer → UTF8 decode chunk →
  `Pub(eventKey, EventKinds.Term, phase: null, chunk, EventLevels.Info)`), preserving raw ANSI escape
  codes verbatim (no `TrimEnd`/line-splitting). Still accumulates a plain-text tail (strip ANSI via a
  small regex) for the same "diagnose a fast crash" fallback `PlaywrightRunner` has.
- After exit, parses `junit.xml`/`results.json` via the same (extracted, shared) parsing helpers
  `PlaywrightRunner` already uses (`internal static` extraction, second half of the refactor above).

#### 3. `ScriptBoardExecutionService.ExecuteAsync` — use `ScriptsBoardRunner`

Calls `IScriptsBoardRunner.RunWithLiveTermAsync` (new interface, singleton — mirrors `IPlaywrightRunner`)
instead of `IPlaywrightRunner.RunAsync`. Everything else (row updates, Allure history path, negative
`RunId`) is identical to QLV2-303's design.

#### 4. DI registration

`services.AddSingleton<IScriptsBoardRunner, ScriptsBoardRunner>();`

#### 5. Tests

Extracted `PlaywrightConfigWriter`/parsing-helper unit tests continue to pass unchanged (proves the
refactor is behavior-preserving). New `ScriptsBoardRunnerTests`: `ApplyAutomationEnv` + `FORCE_COLOR`
combination sets both; a fake short-lived process (`cmd /c echo hi` on Windows) round-trips through the
raw-chunk reader without throwing and produces at least one `Term` event via a captured `IEventBus`.

**AC:** B/T; existing `PlaywrightRunnerTests` unchanged and green (proves the extraction didn't regress
QA Runs); a manual Scripts Board run against story 177540's generated specs shows live colored ANSI output
in a raw SSE capture (`curl -N .../stream`) before the frontend terminal exists.

---

### QLV2-305: Trends + flakiness

#### 1. Expose Allure history reads

`PlaywrightRunner.HistoryFile` is `private` — add a public `string GetHistoryFilePath(string? module, int? pipelineId)`
wrapper (same logic, just exposed) to `IPlaywrightRunner` so a reader doesn't need write access.

#### 2. `Core/Abstractions/IScriptTrendsService.cs` (new)

```csharp
public sealed record TrendPoint(DateTime Ts, int Passed, int Failed, long DurationMs);
public sealed record FlakinessItem(string SpecTitle, int Runs, int Flips, double FlipRate);

public interface IScriptTrendsService
{
    Task<IReadOnlyList<TrendPoint>> GetTrendsAsync(string? module, CancellationToken ct = default);
    Task<IReadOnlyList<FlakinessItem>> GetFlakinessAsync(string? module, CancellationToken ct = default);
}
```

#### 3. `Infrastructure/ScriptsBoard/ScriptTrendsService.cs` (new, scoped)

- `GetTrendsAsync`: read+parse `history.jsonl` at `GetHistoryFilePath(module, null)` (manual/board runs)
  — Allure's history entries already carry pass/fail/duration per historical execution; map to
  `TrendPoint`. If the file doesn't exist, return `[]` (no runs yet — not an error).
- `GetFlakinessAsync`: query `ScriptBoardRuns` (+ optionally `TestRuns` for the same module) ordered by
  `CreatedAt`; for specs appearing in ≥2 runs, count pass→fail / fail→pass transitions per test title
  (from the retained `junit.xml`/history per-test entries) → `FlipRate = Flips / (Runs-1)`. v1 scope: this
  can start from the Allure history per-test breakdown (already has per-test pass/fail per run) rather
  than re-parsing every historical `junit.xml` from disk.

#### 4. Controller endpoints (`ScriptsBoardController.cs`)

```
GET /api/scripts-board/trends?module=
GET /api/scripts-board/flakiness?module=
```

#### 5. DI + tests

`services.AddScoped<IScriptTrendsService, ScriptTrendsService>();`. `ScriptTrendsServiceTests.cs`: a
seeded `history.jsonl` with ≥2 entries produces ordered `TrendPoint`s; a spec flipping pass/fail across
3 seeded entries computes the expected `FlipRate`; missing history file → empty lists, no throw.

**AC:** B/T; trends/flakiness populate after ≥2 Scripts Board runs against the same module (verify
manually against story 177540 by running twice).

---

### QLV2-306: Frontend module scaffold

#### 1. `client/src/routes/paths.ts`

Add `scriptsBoard: '/scripts-board'`.

#### 2. `client/src/api/scriptsBoard.ts` (new)

Axios wrappers: `getInventory()`, `startRun(filter)`, `listRuns(skip, take)`, `getRun(id)`,
`cancelRun(id)`, `getTrends(module?)`, `getFlakiness(module?)`, plus the `runStreamUrl(id)` helper
(mirrors `qaRuns.ts`'s pattern for `run:{id}` → here `scripts-board-run:{id}`).

#### 3. `client/src/pages/ScriptsBoardPage.tsx` (new)

Modeled on `QaRunsPage.tsx`'s list+drawer archetype, lighter:
- Header + filter bar (module/level/type/bucket/tag dropdowns sourced from the inventory response) +
  "Run selection" button (headed toggle + workers input) → `startRun`.
- Inventory grid/table: one row per `ScriptInventoryItem` (path, module, type, level, bucket, ADO TC
  link if `AdoTestCaseId`, a reconcile-state chip: linked/orphan-disk/orphan-db/no-meta) with per-spec
  "Run this one" quick action (`SpecFilePaths: [path]`) and a click-through drawer (raw spec code viewer
  via the existing code-viewer pattern used elsewhere, e.g. `qaLoop.tsx`'s spec viewer).
- Runs history table below (or a tab): `ScriptBoardRun` rows, click → drawer with Console tab (QLV2-307's
  terminal) + Passed/Failed/Skipped + report/junit links.
- Trends/Flakiness tab (QLV2-308).

#### 4. `client/src/layout/Layout.tsx`

Nav item (`{ label: 'Scripts Board', path: paths.scriptsBoard, icon: <TerminalIcon /> }`) + `<Route
path={paths.scriptsBoard} element={<ScriptsBoardPage />} />`, same shape as the Schema Explorer entry.

**AC:** F (`npm run build` + `npm run lint`); page renders inventory + empty runs list against a live
backend with zero Scripts Board runs yet.

---

### QLV2-307: xterm.js live terminal component

#### 1. `client/package.json`

`npm install xterm xterm-addon-fit` (in `client/`).

#### 2. `client/src/components/TermConsole.tsx` (new)

- `useRef` + `xterm.Terminal` + `FitAddon`; mounts into a div, disposes on unmount.
- Subscribes to the run's SSE stream (reuse `hooks/useEventStream.ts` — extend its `ConsoleEvent.kind`
  union type to include `'term'`) and calls `term.write(event.text)` for every `kind === 'term'` event
  (raw ANSI passthrough — xterm.js renders escape codes natively, no parsing needed on the client side).
- Non-`term` kinds (`phase`/`system`/`result`/`error`) can optionally still render above/below via the
  existing `AgentConsole` for narrative context, or be ignored — decide based on how noisy raw Playwright
  output already is (Playwright's own reporter output already narrates pass/fail).

#### 3. Wire into `ScriptsBoardPage`'s run drawer

Replace/augment the Console tab with `<TermConsole runId={run.id} streamUrl={runStreamUrl(run.id)} />`.

**AC:** F; a real Scripts Board run against story 177540 shows live colored Playwright output in the
terminal panel (manual visual check — screenshot or description in the verification note).

---

### QLV2-308: Trends + flakiness UI

Add a "Trends" tab to `ScriptsBoardPage` (or a dedicated section): a simple line/bar rendering of
`GetTrendsAsync` (passed/failed over time, duration) and a flakiness table (`GetFlakinessAsync`:
spec title, runs, flips, flip rate — sort by flip rate desc, highlight >20% as flaky). No new chart
dependency needed if a simple table + CSS bars suffice for v1 (avoid adding a charting library unless
the existing `client/package.json` already has one — check `Reports`/`ReportSummarizer`'s frontend
consumer first, since that page likely already renders some kind of stat visualization to copy from).

**AC:** F; trends/flakiness tab renders real data after ≥2 Scripts Board runs on the same module.

---

### QLV2-309: Tests + docs (SLAB 3 close-out)

- Full backend test sweep (`dotnet test`) including all new `*Tests.cs` from 301–305.
- `npm run build` + `npm run lint` clean.
- Manual verification pass against story **177540** end-to-end: scan → push specs → Scripts Board
  inventory shows them `linked` with `@qa-meta` → run by module → live terminal shows raw output → run
  again → trends/flakiness show 2 data points → hand-add a stray spec file → inventory flags it
  `orphan_disk`.
- Feature doc `docs/features/scripts-board.md` (what/why, data model, data flow, key files, config,
  limitations — same shape as `docs/features/qa-loop-ui-phase.md`) + a `docs/changelog/` entry.
- Flip all Slab 3 tasks `[x]` in `prd.md`; `progress.md` final entry: **SLAB 3 COMPLETE**.
