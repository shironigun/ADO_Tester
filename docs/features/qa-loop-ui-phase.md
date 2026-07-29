# Feature: QA Loop v2 — UI Testing Phase (Slab 2)

**Status:** Complete (QLV2-201..208, verified 2026-07-29)
**Tasks:** QLV2-201..208 · **Tests:** 163/163 pass

---

## What it is

The UI Phase extends the QA Loop with a second, automated testing layer on top of the existing API
test-case pipeline. After the API phase generates and pushes test cases, the user can optionally
trigger the UI Phase which:

1. **Authors UI test cases** (LLM-generated, bucket-tagged manual test cases)
2. **Generates Playwright scripts** (Claude CLI + Playwright MCP agentic run per case)
3. **Routes to ADO bucket sub-suites** (Smoke / Regression / E2E under the Ui level suite)
4. **Heals failing specs** (agentic rewrite of the spec given the failure message)

---

## Data model

New columns (migration `AddQaLoopUiPhase`):

| Table | Column | Type | Notes |
|---|---|---|---|
| `ApiTestSessions` | `HasUiPhase` | `bool` | set true at scan start when UI phase enabled |
| `ApiTestSessions` | `UiStatus` | `ApiUiStatus?` | null until `author-ui-cases` called |
| `ApiTestDrafts` | `DraftKind` | `ApiDraftKind?` | `api_case` / `ui_case` / `ui_script`; null = legacy API case |
| `ApiTestDrafts` | `Bucket` | `string?` | `smoke` / `regression` / `e2e` for UI drafts |

Enum `ApiUiStatus`: `Authoring → AwaitingReview → Drafting → Drafted → Pushing → Done → Failed → Cancelled`

---

## Data flow

```
scan/start (hasUiPhase=true)
    → ApiTestSession.HasUiPhase=true
    → normal API analyze/generate/push flow

POST sessions/{id}/author-ui-cases
    → UiStatus: Authoring
    → UiCaseAuthorAgent → JSON array [{title, preconditions, steps, bucket}]
    → persists ApiTestDraft rows (Level=Ui, DraftKind=UiCase, Bucket filled)
    → UiStatus: AwaitingReview

POST sessions/{id}/generate-ui-scripts
    → UiStatus: Drafting
    → per approved UiCase: IPlaywrightAgentService.GenerateScriptAsync(...)
        → spawns: claude -p --output-format stream-json [--mcp-config .mcp.json] [--model ...]
        → writes .ui.spec.ts to {PlaywrightRoot}/generated/ui/{bucket}/{slug}-{caseId}.ui.spec.ts
    → inserts ApiTestDraft (DraftKind=UiScript, SpecCode, SpecFilePath)
    → UiStatus: Drafted

POST sessions/{id}/push (or auto-push)
    → EnsureUiBucketSuitesAsync: creates/finds Smoke/Regression/E2E sub-suites under Ui level suite
    → routes each UiScript draft to its bucket sub-suite
    → PlaywrightRunSpec.EnableCapture=true: injects screenshot/trace into playwright.config.ts

POST sessions/{id}/heal-specs  { draftIds: int[] }
    → per selected UiScript draft: IPlaywrightAgentService.HealAsync(specFilePath, draft.ErrorMessage)
        → spawns claude CLI healer prompt; spec overwritten in place
    → reads file back → updates SpecCode, clears ErrorMessage, resets Status=Draft
```

---

## ADO suite tree

```
Test Plan
  └── Module suite (e.g. "Deals")
        ├── Contract level suite
        ├── Endpoint level suite
        ├── Workflow level suite
        └── Ui level suite
              ├── Smoke          ← bucket sub-suite
              ├── Regression     ← bucket sub-suite
              └── E2E            ← bucket sub-suite
```

Created lazily on push by `EnsureUiBucketSuitesAsync`. Falls back to the Ui level suite if creation fails.

---

## Key files

| File | Role |
|---|---|
| `Core/Models/Enums.cs` | `ApiDraftKind`, `ApiUiStatus`, `UiPhaseEnumExtensions` |
| `Core/Models/ApiTestSession.cs` | `HasUiPhase`, `UiStatus` |
| `Core/Abstractions/IPlaywrightAgentService.cs` | service interface |
| `Infrastructure/QaLoop/PlaywrightAgentService.cs` | spawns Claude CLI; generate + heal |
| `Infrastructure/Agents/UiCaseAuthorAgent.cs` | UI case authoring via LLM |
| `Infrastructure/Agents/Prompts/ui_case_author_system.txt` | system prompt |
| `Infrastructure/Orchestrators/QaLoopOrchestrator.cs` | AuthorUiCasesAsync, GenerateUiScriptsAsync, HealSpecsAsync, EnsureUiBucketSuitesAsync |
| `Infrastructure/QaRuns/PlaywrightRunner.cs` | `EnableCapture` flag (screenshot/trace) |
| `Infrastructure/QaRuns/RunExecutionService.cs` | sets `EnableCapture` for Ui-level runs |
| `Api/Controllers/QaLoopController.cs` | author-ui-cases, generate-ui-scripts, heal-specs endpoints |
| `client/src/api/qaLoop.ts` | `authorUiCases`, `generateUiScripts`, `healSpecs` API; new fields |
| `client/src/session/configs/qaLoop.tsx` | `hasUiPhase` toggle; `UiPhasePanel`; UI Phase tab |
| `scripts/fixtures/auth.setup.ts` | Playwright global-setup: login → storageState.json |

---

## Configuration

```env
# App under test (for Playwright storageState generation)
AUTOMATION_APP_URL=http://localhost:3000
AUTOMATION_APP_USERNAME=qa@example.com
AUTOMATION_APP_PASSWORD=secret

# Root of the Playwright automation project
AUTOMATION_PLAYWRIGHT_ROOT=/path/to/playwright-root
```

Or set via the Settings UI under QA Loop section.

---

## Limitations

- `PlaywrightAgentService` requires the Claude CLI (`claude`) to be installed and on PATH or `CLAUDE_CLI_PATH`.
- The Playwright root must have `.mcp.json` with the Playwright MCP server configured.
- Script generation is one-shot per case; there is no incremental spec refinement (only heal-on-failure).
- `EnableCapture` (screenshot/trace) is set for all Ui-level runs; it cannot be toggled per-run yet.
