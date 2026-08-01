# Feature: QA Loop FRS-Grounded Redesign

**Status:** Complete (KBG-102..116, verified 2026-08-01)  
**Tasks:** KBG-102..116 · **Tests:** backend suite green · **Frontend:** `npm run build` clean

---

## What it is

The QA Loop is grounded in an uploaded TargetCRM **FRS Knowledge Base** (modules + atlas + RULES). After API analyze/generate/push, an optional **UI Phase** runs a separate analyst → case author → Playwright scripts → inline run → explain/heal path — without sharing agents with Author Tests.

## Pipeline

```
1. Retrieve KB   → IFrsKbService.ResolveModuleAsync → FrsSlug + KbContextJson
2. Code Scan     → IQaLoopCodeScanService → CodeScanJson (FE+BE bundle)
3. API Analyst   → ModuleAnalyzerAgent + KB context block
4–6. Review / draft / push API  → Status = ApiDone when HasUiPhase, else Done
7. UI Analyst    → QaLoopUiAnalystAgent → UiAnalysisJson (after ApiDone)
8. UI Analysis review → RerunUiAnalysis / Author Cases
9. UI Case Author → QaLoopUiCaseAuthorAgent (fallback: deprecated UiCaseAuthorAgent)
10. Generate scripts → SemaphoreSlim(2), 10-min timeout, atlas routes[0] as moduleRoute
10b. Run scripts → PlaywrightRunner; draft.ErrorMessage from JUnit
11. Explain / Heal → QaLoopFailureAnalystAgent; HealAsync(userContext?)
12. Push UI      → suite UiTestSuiteId ?? TestSuiteId (no story link)
```

## Key types / columns

| Area | Addition |
|------|----------|
| KB docs | `FolderPath`; folder upload `POST /api/knowledgebase/{id}/folder` |
| Session | `FrsSlug`, `KbContextJson`, `CodeScanJson`, `UiAnalysisJson`, `UiTestSuiteId`, `LastUiRunId` |
| Status | `RetrievingKb`, `Scanning`, `ApiDone`; UI: `AnalyzingUi`, `AwaitingUiAnalysisReview`, `RunningUiScripts` |
| Core models | `FrsModuleContext`, `CodeScanBundle`, `QaLoopUiAnalystOutput` |

## Endpoints (new / extended)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/qaloop/{projectId}/frs-modules` | Slug list for Autocomplete |
| PATCH | `/api/qaloop/sessions/{id}/ui-suite` | `UiTestSuiteId` |
| POST | `…/rerun-ui-analysis` | 409 unless `AwaitingUiAnalysisReview` |
| POST | `…/author-ui-cases` | 409 unless null or `AwaitingUiAnalysisReview` |
| POST | `…/generate-ui-scripts` | 409 unless `AwaitingReview` |
| POST | `…/run-ui-scripts` | 400 PlaywrightRoot / no scripts; 409 if already running |
| POST | `…/explain-failure` | Sync; body `{ draftId }` |
| POST | `…/heal-specs` | `userContext?` |

## Scripts Board alignment

`@qa-meta.module = FrsSlug ?? Module`. Optional `@qa-meta.tags` (and `ApiTestDraft.Tags` JSON) from
`QaMetaHeader.ExtractTags` on the first `test(` title (`@feature-*`, `@smoke`/`@regression`/`@e2e`,
level tags). Generators get FRS RULES / FE↔BE / FeaturesJson via `BuildGeneratorExtraContext` when KB
is present.

## Key files

- `Infrastructure/QaLoop/FrsKbService.cs`, `QaLoopCodeScanService.cs`
- `Core/Models/QaMeta.cs` (`ExtractTags`, optional `tags`)
- `Infrastructure/Agents/QaLoopUiAnalystAgent.cs`, `QaLoopUiCaseAuthorAgent.cs`, `QaLoopFailureAnalystAgent.cs`
- `Infrastructure/Orchestrators/QaLoopOrchestrator.cs`
- `client/src/session/configs/qaLoop.tsx` (`UiPhasePanel`)
- `client/src/pages/KnowledgebasePage.tsx` (folder tree upload)

## Verification notes

- Backend xUnit suite green (includes FrsKb, CodeScan, KBG-108 extraContext/tags, RunUiScripts,
  ExplainFailure, heal userContext).
- Frontend production build clean.
- Live ADO (2026-08-01): work item **177540** OK after syncing PAT into `ado.pat`; messaging session 36
  through push (`ApiDone`, TC #178039) + UI Analyst + 18 UI cases authored + UI run **360002**
  (**1 passed**) on **ClaudeCli / haiku**. Agentic Playwright MCP script-gen timed out (10 min);
  run path verified with a seeded smoke spec. Also fixed UI `testDir`/`testMatch` for
  `{PlaywrightRoot}/generated/ui` specs.