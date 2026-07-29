# Feature: QA Loop v2 — Free-Text Context + Merge-Refine (Slab 1)

| **Feature** | QA Loop v2 Slab 1 — Free-text context + merge-refine |
|---|---|
| **Date Shipped** | 2026-07-29 |
| **Branch** | `staging` |
| **PRD/Design** | `docs/progress/RalphLoop/qa-loop-v2/` |
| **Test coverage** | `QaLoopOrchestratorTests` (158/158 pass) |

## What it does

Layers three free-text touchpoints and a merge-refine loop onto the existing QA Loop
`Scan → Analyze → Review → Generate → Push` pipeline:

1. **Additional context at scan start** — a multiline "Additional context" field on the start form that feeds the Analyzer and all three generator agents. Stored as `ApiTestSession.AdditionalContext`.
2. **Context for generators at the Analyze→Generate gate** — a "Context for generators" field shown when the session is `AwaitingReview`. Passed to all three generators' `extraContext` parameter at generate time. Stored as `ApiTestSession.GenerateContext`.
3. **Refine analysis** — when a session is `AwaitingReview`, a "Refine analysis with feedback" box + button calls `RerunAnalyzeAsync`, which re-invokes `ModuleAnalyzerAgent.RefineInventoryAsync` with the prior inventory + surface matrix + feedback, returning an improved matrix and landing back at `AwaitingReview`. Feedback stored as `ApiTestSession.LastFeedback`.
4. **Regenerate drafts with feedback** — per-draft feedback fields + a Remove checkbox + an overall feedback box + a "Regenerate with feedback" button at the bottom of the Drafts tab. Calls `RefineDraftsAsync`, which:
   - Calls the matching generator's `RefineAsync` for each targeted Draft/Rejected row
   - Updates the draft in-place and rewrites the disk spec
   - Removes flagged rows (Draft/Rejected status only)
   - **Never touches Approved or Pushed rows**
   - Sets session to `Drafted` on completion

## Data changes (migration `AddQaLoopFreeTextContext`, 2026-07-29)

**`ApiTestSessions` — 3 new nullable columns:**
| Column | Type | Purpose |
|---|---|---|
| `AdditionalContext` | `TEXT?` | Free-text at scan start → Analyzer + generators |
| `GenerateContext` | `TEXT?` | Free-text at Analyze→Generate gate → all generators |
| `LastFeedback` | `TEXT?` | Most recent refine feedback (persisted for UI re-seed) |

Applies to both SQLite (default) and Postgres (`AddQaLoopFreeTextContext` Npgsql migration).

## Key files

| Area | File(s) |
|---|---|
| Entity | `Core/Models/ApiTestSession.cs` |
| Contracts | `Core/Contracts/QaLoopContracts.cs` (new: `RerunAnalyzeRequest`, `ApiDraftFeedback`, `RefineDraftsRequest`, `GenerateDraftsRequest`) |
| Interface | `Core/Abstractions/IQaLoopOrchestrator.cs` |
| Agents | `Infrastructure/Agents/ModuleAnalyzerAgent.cs` (additionalContext, `RefineInventoryAsync`) |
| Agents | `Infrastructure/Agents/ContractGeneratorAgent.cs`, `EndpointGeneratorAgent.cs`, `WorkflowGeneratorAgent.cs` (extraContext, `RefineAsync`) |
| Orchestrator | `Infrastructure/Orchestrators/QaLoopOrchestrator.cs` (`RerunAnalyzeAsync`, `RefineDraftsAsync`, route/workflow matchers) |
| Controller | `Api/Controllers/QaLoopController.cs` |
| Migrations | `Infrastructure/Data/Migrations/20260729013815_AddQaLoopFreeTextContext.cs` (SQLite) |
| Migrations (Npgsql) | `Migrations.Npgsql/Migrations/20260729021455_AddQaLoopFreeTextContext.cs` |
| Frontend API | `client/src/api/qaLoop.ts` |
| Frontend UI | `client/src/session/configs/qaLoop.tsx` |

## Parity guardrails preserved
- System prompts (embedded `.txt` resources) are **not touched** — all new instructions are in user prompts only.
- `AdoGoldenTests` green (no ADO-body changes).
- `BugOrchestrator` and Author Tests pipeline unchanged.
- 158/158 backend tests pass.
