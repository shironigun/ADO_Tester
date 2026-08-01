# Blueprint — QA Loop KB-Grounded Redesign (file-by-file implementation guide)

All work respects the invariants in `solution.md`: byte-exact ADO parity (system prompts frozen), background-task-with-own-DI-scope, additive-only migrations, graceful degradation when FRS is absent. Tasks are grouped in the order they should be built — each task compiles and tests green before the next starts.

---

## KBG-101 — Ralph loop docs *(already done)*

`solution.md`, `Ralph loop.md`, `blueprint.md` (this file), `prd.md`, `progress.md`.

---

## KBG-102 — KB folder-path storage + tree upload + tree view

### `backend/src/QaAutomation.Core/Models/KnowledgebaseDocument.cs`
Add one nullable property after `UploadedAt`:
```csharp
/// <summary>Relative path from upload root (e.g. "modules", "atlas", "" for root files). Null on legacy documents.</summary>
public string? FolderPath { get; set; }
```

### `backend/src/QaAutomation.Core/Abstractions/IKnowledgebaseService.cs`
1. Extend `UploadAsync` signature (additive, default null = backward-compat):
```csharp
Task<KnowledgebaseDocument> UploadAsync(
    int projectId, string fileName, string markdown,
    string? folderPath = null, CancellationToken ct = default);
```
2. Add two new methods:
```csharp
/// <summary>Documents whose FolderPath equals folderPath or starts with "folderPath/".
/// Special case: folderPath="" returns only root docs (FolderPath="" or NULL).</summary>
Task<IReadOnlyList<KnowledgebaseDocument>> GetDocumentsByFolderAsync(
    int projectId, string folderPath, CancellationToken ct = default);

/// <summary>Exact FolderPath+FileName match. Latest UploadedAt when duplicates exist.</summary>
Task<KnowledgebaseDocument?> GetDocumentByPathAsync(
    int projectId, string folderPath, string fileName, CancellationToken ct = default);
```

### `backend/src/QaAutomation.Infrastructure/Knowledgebase/KnowledgebaseService.cs`
- In `UploadAsync`: pass `folderPath` into the new `KnowledgebaseDocument { ..., FolderPath = folderPath }` initializer.
- Implement `GetDocumentsByFolderAsync`: SQLite query — `WHERE ProjectId=@pid AND (FolderPath=@fp OR FolderPath LIKE @fp||'/%')`. Root case (`fp=""`) → `WHERE ProjectId=@pid AND (FolderPath='' OR FolderPath IS NULL)`.
- Implement `GetDocumentByPathAsync`: `WHERE ProjectId=@pid AND FolderPath=@fp AND FileName=@fn ORDER BY UploadedAt DESC LIMIT 1`.

### EF migration: `AddKbFolderPath`
```powershell
dotnet ef migrations add AddKbFolderPath --project src\QaAutomation.Infrastructure --startup-project src\QaAutomation.Api
```
Produces: `ALTER TABLE KnowledgebaseDocuments ADD COLUMN FolderPath TEXT NULL`. Both providers.

### `backend/src/QaAutomation.Core/Contracts/KnowledgebaseContracts.cs`
Add:
```csharp
public sealed record KbFolderUploadItem(string FolderPath, string FileName, string Content);
public sealed record KbFolderUploadResult(int Id, string FileName);
public sealed record KbFolderUploadError(string FileName, string Error);
public sealed record KbFolderUploadResponse(
    KbFolderUploadResult[] Uploaded, KbFolderUploadError[] Failed);
```

### `backend/src/QaAutomation.Api/Controllers/KnowledgebaseController.cs`
Add endpoint after existing `Upload`:
```csharp
[HttpPost("{projectId:int}/folder")]
public async Task<IActionResult> UploadFolder(
    int projectId, [FromBody] List<KbFolderUploadItem> items, CancellationToken ct)
```
Loop through items, call `_kb.UploadAsync(projectId, item.FileName, item.Content, item.FolderPath, ct)` per item. On item failure: add to `Failed` list, continue. Return `Ok(new KbFolderUploadResponse(uploaded, failed))`.

### `client/src/` — Knowledgebase page
File: whichever page renders the KB list (find by searching for `uploadAsync` or `KnowledgebasePage`).
- Add a folder-select button: `<input type="file" webkitdirectory multiple accept=".md" ref={folderInputRef} style={{display:'none'}} onChange={handleFolderSelect}>`.
- `handleFolderSelect`: for each `file` in `e.target.files`, split `file.webkitRelativePath` on `/`; discard first segment; `folderPath = segments.slice(1, -1).join('/')` (empty string for root files); `fileName = segments[segments.length - 1]`. Read content via `FileReader`. Batch into array. POST to `/api/knowledgebase/{projectId}/folder`. Show progress.
- Render a `SimpleTreeView` (MUI) instead of a flat list. Group documents by `folderPath`. Folder nodes show child count. Leaf nodes show `fileName` + delete button. Expand-all toggle. Fall back to flat list when all `folderPath` values are null.

---

## KBG-103 — Core model types

### `backend/src/QaAutomation.Core/Models/FrsModuleContext.cs` *(new)*
```csharp
namespace QaAutomation.Core.Models;
public sealed record FrsModuleContext(
    string Slug, string ModuleMarkdown, string AtlasMarkdown,
    string[] FrontendPathIncludes, string[] BackendPathIncludes,
    string[] KeywordsExclusive, string RulesMarkdown,
    string FeBackLinksMarkdown, string FeaturesJson);
```

### `backend/src/QaAutomation.Core/Models/CodeScanBundle.cs` *(new)*
```csharp
namespace QaAutomation.Core.Models;
public sealed record CodeScanBundle(
    CodeScanFile[] FrontendFiles, CodeScanFile[] BackendFiles,
    string[] ApiCallCandidates, string DiscoverySource);
public sealed record CodeScanFile(string Path, string Content);
```

### `backend/src/QaAutomation.Core/Models/QaLoopUiAnalystOutput.cs` *(new)*
```csharp
namespace QaAutomation.Core.Models;
public sealed record QaLoopUiAnalystOutput(
    UiAnalystFeature[] Features, UiAnalystFlow[] Flows, UiAnalystScreen[] Screens,
    string[] OpenQuestions, string[] MustTest, string[] MustNotTest,
    string AuthPermissionsSummary, string DmsVariantsSummary);
public sealed record UiAnalystFeature(string Id, string Name, string Description);
public sealed record UiAnalystFlow(string Name, string Trigger, string[] Steps, string[] Breaks);
public sealed record UiAnalystScreen(string Name, string Route, string[] Components);
```

No EF migration needed — these are in-memory/JSON-serialized types.

---

## KBG-104 — IFrsKbService + FrsKbService

### `backend/src/QaAutomation.Core/Abstractions/IFrsKbService.cs` *(new)*
```csharp
using QaAutomation.Core.Models;
namespace QaAutomation.Core.Abstractions;
public interface IFrsKbService
{
    Task<FrsModuleContext?> ResolveModuleAsync(
        string moduleName, int projectId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListSlugsAsync(
        int projectId, CancellationToken ct = default);
}
```

### `backend/src/QaAutomation.Infrastructure/QaLoop/FrsKbService.cs` *(new)*
Constructor: `IKnowledgebaseService kb`. Implementation:
- `ListSlugsAsync`: `GetDocumentsByFolderAsync(projectId, "modules")` → `DistinctBy(d => d.FileName.ToLowerInvariant())` → strip `.md`. Returns `IReadOnlyList<string>`.
- `ResolveModuleAsync`:
  1. `GetDocumentsByFolderAsync(projectId, "modules")` → deduplicate by `FileName` (latest `UploadedAt` per name).
  2. Exact slug match (case-insensitive `slug == moduleName`).
  3. Alias/keyword match: for each unmatched slug, read atlas from disk via `StoredPath`; parse YAML between first and second `---` lines (try/catch → skip on error); check `aliases[]` and `keywords_exclusive[]`.
  4. On match: read `modules/<slug>.md`, `atlas/<slug>.md`, `RULES.md` (all via `StoredPath`).
  5. From atlas full text: extract YAML (between `---` fences) → parse arrays.
  6. From RULES.md: string-range extract `## <slug>` to next `## `.
  7. From atlas: extract `## Frontend ↔ Backend links` table; extract first 3 000 chars of `## Disambiguation traps` section.
  8. Serialize `features[]` as compact `[{id,aliases,apis}]` JSON.
  9. Return `FrsModuleContext`. Empty `modules/` folder → return null.

Memoize atlas reads within the instance lifetime (scoped — one resolution per scan).

### `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
In the QA Loop registration block, add:
```csharp
services.AddScoped<IFrsKbService, FrsKbService>();
```

---

## KBG-105 — Session state machine + migration

### `backend/src/QaAutomation.Core/Models/Enums.cs`
1. Extend `ApiSessionStatus` (exact-name, stored as string):
```csharp
public enum ApiSessionStatus
{
    RetrievingKb,   // NEW
    Scanning,       // NEW
    Analyzing, AwaitingReview, Drafting, Drafted, Pushing,
    ApiDone,        // NEW — API phase complete, UI phase running/pending
    Done, Failed, Cancelled
}
```
2. Extend `ApiUiStatus`:
```csharp
public enum ApiUiStatus
{
    AnalyzingUi,              // NEW
    AwaitingUiAnalysisReview, // NEW
    Authoring, AwaitingReview, Drafting, Drafted,
    RunningUiScripts,         // NEW
    Pushing, Done, Failed, Cancelled
}
```
3. **Do NOT add `ApiDone` to `TerminalStatuses`** in `QaLoopOrchestrator.cs` (it's checked there as a `HashSet<ApiSessionStatus>`).

### `backend/src/QaAutomation.Core/Models/ApiTestSession.cs`
Add 6 nullable properties (after existing `UiStatus`):
```csharp
public string? FrsSlug { get; set; }
public string? KbContextJson { get; set; }   // FrsModuleContext — internal only, not in ApiSessionDetail
public string? CodeScanJson { get; set; }    // CodeScanBundle — internal only
public string? UiAnalysisJson { get; set; }  // QaLoopUiAnalystOutput — in ApiSessionDetail
public int? UiTestSuiteId { get; set; }
public int? LastUiRunId { get; set; }
```

### `backend/src/QaAutomation.Infrastructure/Data/QaDbContext.cs`
No explicit config needed — nullable `string?` maps by convention; nullable `int?` maps by convention. EF will detect the new properties automatically.

### EF migration: `AddQaLoopFrsColumns`
```powershell
dotnet ef migrations add AddQaLoopFrsColumns --project src\QaAutomation.Infrastructure --startup-project src\QaAutomation.Api
```
Produces 6 additive nullable columns on `ApiTestSessions`. Both providers (SQLite + Npgsql).

### `backend/src/QaAutomation.Core/Models/ConsoleEvent.cs` — EventPhases additions
Add to `EventPhases`:
```csharp
public const string RetrieveKb  = "retrieve-kb";
public const string Scan        = "scan";
public const string AnalyzeUi   = "analyze-ui";
public const string RunUi       = "run-ui";
```

---

## KBG-106 — IQaLoopCodeScanService + QaLoopCodeScanService

### `backend/src/QaAutomation.Core/Abstractions/IQaLoopCodeScanService.cs` *(new)*
```csharp
using QaAutomation.Core.Models;
namespace QaAutomation.Core.Abstractions;
public interface IQaLoopCodeScanService
{
    Task<CodeScanBundle> ScanAsync(
        string module, string repository, string branch,
        FrsModuleContext? frsContext, string key,
        CancellationToken ct = default);
}
```
`key` is the SSE session key for publishing `Warn` events.

### `backend/src/QaAutomation.Infrastructure/QaLoop/QaLoopCodeScanService.cs` *(new)*
Constructor: `IAzureDevOpsClient ado`, `IModuleSourceDiscoveryService discovery`, `IEventBus bus`.

`ScanAsync` implementation:
1. If `frsContext != null` AND `repository`/`branch` set: fetch files matching `FrontendPathIncludes` + `BackendPathIncludes` via `ado.GetFileContentAsync` (in parallel, capped). Collect as `CodeScanFile[]`.
2. Fallback (atlas yielded 0 files OR `frsContext == null`): invoke `discovery.DiscoverAsync(module, repository, branch, ct)` to get the source bundle via `IModuleSourceDiscoveryService`.
3. Run deterministic API extractor on frontend file contents (Axios/fetch regex patterns) → `ApiCallCandidates[]`.
4. Enforce caps: FE ≤ 40, BE ≤ 20, total content ≤ 400 KB (truncate to cap, emit `Warn`).
5. On ADO failure (401/404/etc.): `Pub(key, EventKinds.Result, EventPhases.Scan, "Code scan failed: ...", EventLevels.Warn)` and return empty `CodeScanBundle`.
6. Return `CodeScanBundle { FrontendFiles, BackendFiles, ApiCallCandidates, DiscoverySource }` where `DiscoverySource` = `"atlas"` or `"discovery"` or `"none"`.

### `backend/src/QaAutomation.Infrastructure/DependencyInjection.cs`
```csharp
services.AddScoped<IQaLoopCodeScanService, QaLoopCodeScanService>();
```

---

## KBG-107 — QaLoopOrchestrator: steps 1 (RetrieveKb) and 2 (Scan)

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`

Inside `RunPipelineInnerAsync`, before the existing "Fetch source from ADO repo" block, insert two new steps:

**Step 1 — Retrieve KB:**
```csharp
session.Status = ApiSessionStatus.RetrievingKb;
session.UpdatedAt = DateTime.UtcNow;
await db.SaveChangesAsync(ct);
Pub(key, EventKinds.Phase, EventPhases.RetrieveKb, $"Retrieving FRS KB for module '{session.Module}'");
var frsKb = scope.ServiceProvider.GetRequiredService<IFrsKbService>();
var frsContext = await frsKb.ResolveModuleAsync(session.Module, session.ProjectId, ct);
if (frsContext != null)
{
    session.FrsSlug = frsContext.Slug;
    session.KbContextJson = JsonSerializer.Serialize(frsContext);
    Pub(key, EventKinds.Result, EventPhases.RetrieveKb,
        $"KB: {frsContext.Slug}", EventLevels.Success);
}
else
{
    Pub(key, EventKinds.Result, EventPhases.RetrieveKb,
        "No FRS KB found — continuing without KB context", EventLevels.Warn);
}
```

**Step 2 — Code Scan:**
```csharp
session.Status = ApiSessionStatus.Scanning;
session.UpdatedAt = DateTime.UtcNow;
await db.SaveChangesAsync(ct);
Pub(key, EventKinds.Phase, EventPhases.Scan, $"Code scan for module '{session.Module}'");
var codeScanSvc = scope.ServiceProvider.GetRequiredService<IQaLoopCodeScanService>();
var codeScanBundle = await codeScanSvc.ScanAsync(
    session.Module, session.Repository ?? "", session.Branch ?? "", frsContext, key, ct);
session.CodeScanJson = JsonSerializer.Serialize(codeScanBundle);
Pub(key, EventKinds.Result, EventPhases.Scan,
    $"Scanned: {codeScanBundle.FrontendFiles.Length} frontend, {codeScanBundle.BackendFiles.Length} backend ({codeScanBundle.DiscoverySource})",
    EventLevels.Success);
```

Then continue to the existing `Analyzing` state / `ModuleAnalyzerAgent` call (the old `FetchSourceAsync` is now replaced by the above two steps; **remove** the old `FetchSourceAsync` call from `RunPipelineInnerAsync` and pass `codeScanBundle` to the analyzer instead — see KBG-108).

**Push step — `ApiDone` vs `Done`:**
In `PushRunAsync`/`RunPushAsync` (wherever `session.Status = ApiSessionStatus.Done` is set after push):
```csharp
session.Status = session.HasUiPhase ? ApiSessionStatus.ApiDone : ApiSessionStatus.Done;
```
After setting `ApiDone`, immediately start UI phase inline:
```csharp
if (session.HasUiPhase)
    _ = Task.Run(() => RunUiPhaseAsync(session.Id, ct), ct);
```
`RunUiPhaseAsync` is the new method added in KBG-112.

---

## KBG-108 — API Analyst KB enrichment + generator extraContext

### `backend/src/QaAutomation.Infrastructure/Agents/ModuleAnalyzerAgent.cs`
- `BuildInventoryPrompt` signature: add `FrsModuleContext? frsContext = null`.
- When `frsContext != null`, prepend a `## Knowledge Base Context (FRS: {frsContext.Slug})` block before `## Module source code`, containing:
  - Module brief: `frsContext.ModuleMarkdown[..Math.Min(6000, frsContext.ModuleMarkdown.Length)]`
  - FE↔BE links: `frsContext.FeBackLinksMarkdown`
  - Domain rules: `frsContext.RulesMarkdown`
  - Disambiguation traps: extract `## Disambiguation traps` to next `##` from `frsContext.AtlasMarkdown`; take first 3 000 chars.
- System prompt bytes (`AgentPrompts.ModuleAnalyzerSystem`) **unchanged**.

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
In `RunPipelineInnerAsync`:
- Replace the `source = await FetchSourceAsync(...)` call with the `codeScanBundle` assembled in KBG-107.
- Build `sourceBundle` string from `codeScanBundle` (concatenate file contents with path headers, same format as existing `FetchSourceAsync` output).
- Pass `frsContext` to `new ModuleAnalyzerAgent(llm).AnalyzeInventoryAsync(session.Module, sourceBundle, existingCases, frsContext: frsContext, ...)`.

In `GenerateDraftsInnerAsync`, build enriched `extraContext` when `frsContext != null`:
```
{session.GenerateContext if non-empty}

## Domain Rules (FRS: {slug})
{RulesMarkdown}

## FE-BE Contracts
{FeBackLinksMarkdown}

## Features and suggested packs
{FeaturesJson}
For each generated test: include @feature-<id> in the test title, and @smoke/@regression/@e2e per the feature's suggested pack.
```
Pass as `extraContext` to each generator call. System prompts frozen.

### `backend/src/QaAutomation.Infrastructure/Agents/` — `QaMeta.cs`
Extend `QaMeta.Build` or `QaMetaHeader.Upsert`: after stamping the header, extract `@[a-z][a-z0-9\-]+` tokens from the first `test(` line of the spec code and populate the `tags` field. No structural change to the header format.

---

## KBG-109 — Contracts + frs-modules endpoint + scan form dropdown

### `backend/src/QaAutomation.Core/Contracts/QaLoopContracts.cs`
- `ApiSessionDetail`: add `string? FrsSlug`, `string? UiAnalysisJson`, `int? UiTestSuiteId`, `int? LastUiRunId`. Update `From(ApiTestSession s)` mapper.
- `KbContextJson` and `CodeScanJson` are **not** mapped into `ApiSessionDetail`.
- `RerunUiAnalysisRequest(string Feedback)` — new record.
- `ExplainFailureResponse(int DraftId, string Explanation)` — new record.
- `HealSpecsRequest`: add `string? UserContext = null`.

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
Add:
```csharp
[HttpGet("{projectId:int}/frs-modules")]
public async Task<IActionResult> GetFrsModules(int projectId, CancellationToken ct)
```
Resolve `IFrsKbService` from DI, call `ListSlugsAsync(projectId, ct)`, return `Ok(slugs)`.

```csharp
[HttpPatch("sessions/{id:int}/ui-suite")]
public async Task<IActionResult> PatchUiSuite(int id, [FromBody] PatchUiSuiteRequest request, CancellationToken ct)
```
Synchronous DB update (`session.UiTestSuiteId = request.SuiteId`). Return `Ok()`. Add `PatchUiSuiteRequest(int? SuiteId)` to `QaLoopContracts.cs`.

### `client/src/api/qaLoop.ts`
Add:
- `getFrsModules(projectId: number): Promise<string[]>` — `GET /api/qaloop/{projectId}/frs-modules`
- `patchUiSuite(id: number, suiteId: number | null): Promise<void>` — `PATCH /api/qaloop/sessions/{id}/ui-suite`
- `rerunUiAnalysis(id: number, feedback: string): Promise<void>` — `POST … /rerun-ui-analysis`
- `runUiScripts(id: number): Promise<void>` — `POST … /run-ui-scripts`
- `explainFailure(id: number, draftId: number): Promise<{draftId: number, explanation: string}>` — `POST … /explain-failure`
- Update `healSpecs` to accept `userContext?: string` in the request body.
- Update `ApiSessionDetail` type to include `frsSlug?`, `uiAnalysisJson?`, `uiTestSuiteId?`, `lastUiRunId?`.

### `client/src/session/configs/qaLoop.tsx`
1. **Module field** → replace `TextField` with MUI `Autocomplete` (`freeSolo=true`). Fetch options via `getFrsModules(projectId)` on mount. `"KB: {frsSlug}"` chip on the Coverage step when `session.frsSlug` is set.

2. **Advanced expander** → wrap existing `FrontendPath`, `SourceDirs`, `FrontendModule` fields in a `<Accordion>` labeled "Advanced".

3. **`activeStepIndex`** — update:
```typescript
case 'RetrievingKb':
case 'Scanning':       return 0
case 'Analyzing':
case 'AwaitingReview': return 1
case 'Drafting':       return 2
case 'Drafted': case 'Pushing': case 'ApiDone': case 'Done': return 3
```

4. **`PIPELINE_STEPS[0].label`** → change from `"Scan"` to `"Retrieve KB"`.

5. **`qaLoopConfig`**:
```typescript
inFlightStatuses: ['RetrievingKb', 'Scanning', 'AwaitingReview', 'Drafting', 'Pushing', 'ApiDone'],
getUiStatus: (d: ApiSessionDetail) => d.uiStatus,
uiInFlightStatuses: ['AnalyzingUi', 'RunningUiScripts'],
```

6. **`PushPanel` `isDone`** → `session.status === 'Done' || session.status === 'ApiDone'`.

7. **`CoverageMatrix` `readOnly`** → `!['Scanning', 'AwaitingReview'].includes(s.status)` (same as before, `RetrievingKb` is read-only by default since it's not in the list).

8. **`DraftsByLevel` `readOnly`** → `s.status === 'Done' || s.status === 'ApiDone'`.

9. **Add PHASE_LABEL entries** to `shared.tsx` and `AgentConsole.tsx`:
```typescript
'retrieve-kb': 'KB', 'scan': 'SCAN', 'analyze-ui': 'UI ANALYSIS',
'failure-explain': 'FAILURE', 'run-ui': 'RUN'
```

### `client/src/session/shared.tsx`
Extend `SessionModuleConfig<TDetail>` (or the `SessionModuleConfig` type — read the actual type definition first):
```typescript
getUiStatus?: (d: TDetail) => string | undefined;
uiInFlightStatuses?: string[];
```
In `useSessionDetail` `refetchInterval`:
```typescript
const uiInFlight = config.getUiStatus && config.uiInFlightStatuses
    ? config.uiInFlightStatuses.includes(config.getUiStatus(d) ?? '')
    : false;
return (mainInFlight || uiInFlight) ? 2500 : false;
```

---

## KBG-110 — UI Analyst agent + orchestrator step 7

### `backend/src/QaAutomation.Infrastructure/Agents/QaLoopUiAnalystAgent.cs` *(new)*
System prompt: embedded resource `qa_loop_ui_analyst_system.txt` (author this prompt during implementation — key instructions: analyze a CRM module from product brief + component code; output a JSON `QaLoopUiAnalystOutput`; no API contracts; focus on flows, screens, user journeys).

```csharp
public sealed class QaLoopUiAnalystAgent(ILlmClient llm)
{
    public async Task<QaLoopUiAnalystOutput> AnalyzeAsync(
        string module, string moduleMarkdown, CodeScanFile[] frontendFiles,
        StreamProgress? progress, Action<LlmEmit>? emit, CancellationToken ct = default)
    { ... }

    public async Task<QaLoopUiAnalystOutput> RefineAsync(
        string module, QaLoopUiAnalystOutput prior, string feedback,
        StreamProgress? progress, Action<LlmEmit>? emit, CancellationToken ct = default)
    { ... }
}
```
Both methods use `JsonUtils.CompleteJsonAsync` with `AgentPrompts.QaLoopUiAnalystSystem`. Parse response into `QaLoopUiAnalystOutput` via `JsonSerializer.Deserialize`.

### `backend/src/QaAutomation.Infrastructure/Agents/AgentPrompts.cs`
Add three new static properties:
```csharp
public static string QaLoopUiAnalystSystem => GetEmbedded("qa_loop_ui_analyst_system.txt");
public static string QaLoopUiCaseAuthorSystem => GetEmbedded("qa_loop_ui_case_author_system.txt");
public static string QaLoopFailureAnalystSystem => GetEmbedded("qa_loop_failure_analyst_system.txt");
```
Add the three `.txt` files to `Infrastructure/Agents/Prompts/` as **EmbeddedResource** in the `.csproj`.

### `backend/src/QaAutomation.Core/Abstractions/IQaLoopOrchestrator.cs`
Add:
```csharp
Task RerunUiAnalysisAsync(int sessionId, string feedback, CancellationToken ct = default);
Task RunUiScriptsAsync(int sessionId, CancellationToken ct = default);
```

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
Add `RunUiPhaseAsync(int sessionId, CancellationToken ct)` — called inline after `ApiDone` in KBG-107's push step. This method is NOT launched via `Launch(...)` directly (it's called after the main pipeline has already set `ApiDone`). It runs in the same background scope.

Inside `RunUiPhaseAsync`:
1. Set `UiStatus = AnalyzingUi`. Publish `Phase/AnalyzeUi`.
2. Deserialize `frsContext` from `session.KbContextJson`.
3. Deserialize `codeScanBundle` from `session.CodeScanJson` (null-safe: proceed with empty FE files).
4. Run `QaLoopUiAnalystAgent.AnalyzeAsync(module, frsContext?.ModuleMarkdown ?? "", codeScanBundle?.FrontendFiles ?? [])`.
5. Serialize result → `session.UiAnalysisJson`. Set `UiStatus = AwaitingUiAnalysisReview`. Persist.

`RerunUiAnalysisAsync(sessionId, feedback)` → `Launch(sessionId, ct => RerunUiAnalysisInnerAsync(sessionId, feedback, ct))`.
Inner: load session, guard `UiStatus = AwaitingUiAnalysisReview`, set `AnalyzingUi`, deserialize prior output, call `QaLoopUiAnalystAgent.RefineAsync(module, priorOutput, feedback)`, replace `UiAnalysisJson`, set `AwaitingUiAnalysisReview`.

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
Add:
```csharp
[HttpPost("sessions/{id:int}/rerun-ui-analysis")]
public IActionResult RerunUiAnalysis(int id, [FromBody] RerunUiAnalysisRequest request)
    => AcceptedOrConflict(id, () => _orchestrator.RerunUiAnalysisAsync(id, request.Feedback));
```
(Use whatever helper pattern the existing controller uses for 202 responses, e.g., `Launch` with conflict guard.)

---

## KBG-111 — UI Case Author agent + author-ui-cases update

### `backend/src/QaAutomation.Infrastructure/Agents/QaLoopUiCaseAuthorAgent.cs` *(new)*
System prompt: `qa_loop_ui_case_author_system.txt` (author during implementation — key: receive `QaLoopUiAnalystOutput` as JSON, produce `UiCaseDraft[]` with `Title`, `Preconditions`, `StepsJson [{action, expectedResult}]`, `Bucket`; module-scoped; no story link).

```csharp
public sealed class QaLoopUiCaseAuthorAgent(ILlmClient llm)
{
    public async Task<List<UiCaseDraft>> AuthorCasesAsync(
        string module, QaLoopUiAnalystOutput analysis,
        string? additionalContext = null,
        StreamProgress? progress = null, Action<LlmEmit>? emit = null,
        CancellationToken ct = default) { ... }
}
```
Uses `JsonUtils.CompleteJsonAsync` with `AgentPrompts.QaLoopUiCaseAuthorSystem`. Output parsed as `UiCaseDraft[]` (reuse existing `UiCaseDraft` record from `UiCaseAuthorAgent.cs` — same shape, or define a local alias).

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs` — `AuthorUiCasesInnerAsync`
**Backward-compat guard:**
```csharp
if (!string.IsNullOrEmpty(session.UiAnalysisJson))
{
    var analysis = JsonSerializer.Deserialize<QaLoopUiAnalystOutput>(session.UiAnalysisJson)!;
    cases = await new QaLoopUiCaseAuthorAgent(llm).AuthorCasesAsync(session.Module, analysis, ...);
}
else
{
    // Deprecated Slab 2 path — old sessions with no UiAnalysisJson
    var inventory = JsonSerializer.Deserialize<InventoryDoc>(session.InventoryJson!)!;
    cases = await new UiCaseAuthorAgent(llm).AuthorCasesAsync(session.Module, inventory, ...);
}
```

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
`author-ui-cases` endpoint: add guard — if `session.UiStatus != null && session.UiStatus != ApiUiStatus.AwaitingUiAnalysisReview` → return 409.

---

## KBG-112 — Playwright per-case scripts: route injection + guards

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs` — `GenerateUiScriptsInnerAsync`
- Add guard: if `session.UiStatus != ApiUiStatus.AwaitingReview` → log + return (do not throw).
- Deserialize `frsContext` from `session.KbContextJson`. Extract first route from atlas `routes:` YAML array if available.
- Pass `moduleRoute: firstRoute` to `PlaywrightAgentService.GenerateScriptAsync`.
- Use `SemaphoreSlim(2)` (already the pattern in the existing method or add it).
- Per-case timeout: wrap each `GenerateScriptAsync` call with `CancellationTokenSource(TimeSpan.FromMinutes(10))` linked to session token.
- Use `session.FrsSlug ?? session.Module` for spec directory path.

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
`generate-ui-scripts` endpoint: add guard → `UiStatus != AwaitingReview` → 409.

---

## KBG-113 — Inline execution (step 10b) + RunUiScriptsAsync

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
Add `RunUiScriptsAsync(sessionId)` → `Launch(sessionId, ct => RunUiScriptsInnerAsync(sessionId, ct))`.

`RunUiScriptsInnerAsync`:
1. Guard: `UiStatus = RunningUiScripts` → publish error + return (prevents double-start).
2. Set `UiStatus = RunningUiScripts`. Persist.
3. Collect `Approved UiScript` drafts with valid `SpecFilePath`. If 0 → Pub Warn + set `UiStatus = Drafted` + return.
4. Compute `runId = session.Id * 10_000 + (session.LastUiRunId == null ? 1 : session.LastUiRunId.Value % 10_000 + 1)`. Store `session.LastUiRunId = runId`.
5. Build `PlaywrightRunSpec { RunId = runId, SpecFilePaths = [...], EnableCapture = true, Headed = false, Workers = null, Module = session.FrsSlug ?? session.Module }`.
6. `var result = await _playwrightRunner.RunAsync(spec, EmitClosure(key, EventPhases.RunUi, ...), ct)`.
7. Parse JUnit from `result` (same `PlaywrightResultParser` used by `RunExecutionService`):
   - For each failing `<testcase>`: resolve `classname` to absolute path via `Path.GetFullPath(Path.Combine(PlaywrightRoot, classname))`. Find matching draft by normalized `SpecFilePath`. Set `draft.ErrorMessage = failure.InnerText`.
   - For each passing spec: find matching draft. Clear `draft.ErrorMessage = null`.
8. Persist all draft changes. Set `UiStatus = Drafted`.

Pub SSE events per spec: `Pub(key, EventKinds.Result, EventPhases.RunUi, $"Spec {fileName}: {pass|fail}", EventLevels.Success|Warn)`.

Respect `_concurrency` (`PipelineConcurrency`) and register with `_cancellations` (`CancellationRegistry`).

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
```csharp
[HttpPost("sessions/{id:int}/run-ui-scripts")]
public async Task<IActionResult> RunUiScripts(int id, CancellationToken ct)
```
Guards: load session, check `PlaywrightRoot` configured (else 400), check `UiStatus != RunningUiScripts` (else 409). Launch `_orchestrator.RunUiScriptsAsync(id)`. Return 202.

---

## KBG-114 — Failure explanation agent + endpoint + heal update

### `backend/src/QaAutomation.Infrastructure/Agents/QaLoopFailureAnalystAgent.cs` *(new)*
System prompt: `qa_loop_failure_analyst_system.txt` (author during implementation — key: receive Playwright error message + test case title + steps + optional screenshot directory; explain in plain English what failed, why, and what to do).

```csharp
public sealed class QaLoopFailureAnalystAgent(ILlmClient llm)
{
    public async Task<string> ExplainAsync(
        string failureMessage, string caseTitle, string stepsJson,
        string? screenshotDirectory = null,
        CancellationToken ct = default) { ... }
}
```
Single non-streaming `CompleteAsync` call. Returns explanation string.

### `backend/src/QaAutomation.Api/Controllers/QaLoopController.cs`
```csharp
[HttpPost("sessions/{id:int}/explain-failure")]
public async Task<IActionResult> ExplainFailure(
    int id, [FromBody] ExplainFailureRequest request, CancellationToken ct)
```
Add `ExplainFailureRequest(int DraftId)` to `QaLoopContracts.cs`.

Implementation (synchronous — NOT a 202 background task):
1. Load `ApiTestDraft` by `request.DraftId` (verify `SessionId == id`, `DraftKind == UiScript`, `ErrorMessage != null`).
2. Load session for `LastUiRunId`.
3. Screenshot dir: `Path.Combine(DataDirectory, "runs", session.LastUiRunId.ToString()!)` if `LastUiRunId != null` and dir exists; else null.
4. Resolve `ILlmClientFactory`, create LLM client.
5. `var explanation = await new QaLoopFailureAnalystAgent(llm).ExplainAsync(draft.ErrorMessage, draft.Title, draft.StepsJson, screenshotDir, ct)`.
6. Return `Ok(new ExplainFailureResponse(draft.Id, explanation))`.
Timeout: wrap controller action with `CancellationTokenSource(30s)` linked to request token. Controller already handles `OperationCanceledException` via `ExceptionHandlingMiddleware`.

### `backend/src/QaAutomation.Core/Abstractions/IPlaywrightAgentService.cs`
Extend `HealAsync` (additive default):
```csharp
Task HealAsync(
    string specFilePath, string failureMessage,
    IProgress<string>? progress, CancellationToken ct,
    string? userContext = null);
```

### `backend/src/QaAutomation.Infrastructure/QaLoop/PlaywrightAgentService.cs`
Extend `HealAsync` to accept `string? userContext = null`. Append to failure message: `failureMessage + (userContext is null ? "" : "\n\nUser context: " + userContext)` before passing to `BuildHealPrompt`.

### `backend/src/QaAutomation.Infrastructure/Orchestrators/QaLoopOrchestrator.cs` — `HealSpecsInnerAsync`
Pass `request.UserContext` to `PlaywrightAgentService.HealAsync(..., userContext: request.UserContext)`.

---

## KBG-115 — UiPhasePanel rewrite (frontend)

### `client/src/session/configs/qaLoop.tsx` — `UiPhasePanel`
Full state-machine-driven render. Read `uiStatus = session.uiStatus`:

```
null / AnalyzingUi       → loading spinner ("Running UI Analysis…")
AwaitingUiAnalysisReview → UiTestSuiteId picker (Autocomplete, same testPlanId)
                           UI Analysis card (render QaLoopUiAnalystOutput from session.uiAnalysisJson)
                           Refine text box + "Rerun UI Analysis" button
                           "Author Cases" button (triggers authorUiCases → only enabled when suite selected or fallback exists)
Authoring                → loading spinner ("Authoring test cases…")
AwaitingReview           → test case cards: title, bucket chip, approve/reject, per-case feedback
                           overall feedback + "Regenerate with feedback" button
                           "Generate Scripts" button (only when ≥1 Approved UiCase)
Drafting                 → per-case loading: show case title + spinner/done/failed per draft
Drafted                  → scripts tab: per-case cards showing SpecFilePath, view-spec button
                           "Approve" per script, "Run Scripts" button (only when ≥1 Approved UiScript)
                           If draft.errorMessage → "Explain" button → shows ExplainFailureCard
RunningUiScripts         → progress spinner + per-spec SSE events via AgentConsole
Pushing                  → loading spinner
Done                     → "All done — {n} UI tests pushed"
```

**`ExplainFailureCard`** sub-component: shows explanation text + "Heal" (text input + button) + "Draft Bug" (only when `draft.adoTestCaseId` non-null, i.e. after push).

**`UiTestSuiteId` picker**: an `Autocomplete` showing suites within `session.testPlanId`. Same plan as API tests. Calls `patchUiSuite(session.id, suiteId)` on change. Always visible in the UI Phase tab (not just at step 8).

---

## KBG-116 — Tests + docs close-out

### Tests (`backend/tests/QaAutomation.Tests/`)
- `FrsKbServiceTests.cs`: slug resolution (exact, alias, keyword), YAML parse, empty KB → null, RULES extraction, graceful non-FRS doc skip.
- `QaLoopCodeScanServiceTests.cs`: atlas seeds used first, discovery fallback on empty, caps enforced, ADO failure → Warn + empty bundle.
- `QaLoopOrchestratorTests.cs` (extend existing): `RunUiScriptsAsync` → draft `ErrorMessage` set on failure, cleared on pass; `RerunUiAnalysisAsync` → `UiAnalysisJson` replaced; backward-compat `AuthorUiCasesAsync` → deprecated agent used when `UiAnalysisJson = null`.
- `QaLoopFailureAnalystAgentTests.cs`: output is non-empty string; screenshot dir gracefully omitted when null.

### Docs
- `docs/features/qa-loop-frs-grounded.md` — feature doc (written after verification).
- `docs/changelog/` — entry (written after verification).
- `prd.md` — flip all tasks `[x]` (done during implementation as each task verifies).

### Verification (from `solution.md §Verification`)
Full checklist: KB tree upload → Retrieve KB → Code Scan → API Analyst (including `AdoGoldenTests`) → API push `ApiDone` → UI Analyst → backward compat → per-case Playwright → step 10b → failure explanation → heal → UI push → Scripts Board.
ADO PAT blocker: record pending per `ralph-loop-ops.mdc`.
