# Feature: Author Tests Enhancements

| **Feature**      | Author Tests Enhancements — Free-Text Context, Code Discovery, Refine Loops, Existing-Test Dedup |
| ---------------- | ------------------------------------------------------------------------------------------------ |
| **Date Shipped** | 2026-07-28                                                                                       |
| **Branch**       | `claude/chat-session-flzhjh` → merged to `master` 2026-07-29                                     |
| **PRD/Design**   | `docs/progress/RalphLoop/author-tests-enhancements/`                                             |

## What it does

Layers four capabilities onto the existing Author Tests pipeline
(`Fetch → Retrieve KB → Analyst → Tester`), transforming it into:

```
Fetch → Retrieve KB → Discover code → Analyst → (pause) → Tester
```

### Phase 1 — Free-text context + refine loops
- **Additional context** field on the start form → fed to the Analyst via `## Additional Context` prompt section.
- **Extra Tester context** field at the Analyst→Tester gate → fed to the Tester via `## Extra Context` prompt section.
- **Analyst refine** (`POST /api/author-tests/sessions/{id}/rerun-analyst`): re-runs the Analyst with reviewer feedback, improving the *existing* analysis in place; session returns to `AwaitingTester`.
- **Tester refine** (`POST /api/author-tests/sessions/{id}/refine-tester`): MERGES per-test + overall feedback into existing drafts (update changed, flag remove, never touch `Pushed`).

### Phase 2 — Code discovery
- New `Discover code` pipeline step (index 2) backed by `ICodeContextService` / `CodeContextService`.
- Backtracks the feature via linked PRs/commits (`alwaysSearch:true` for thin tickets) and optional free-text route/screen hints.
- Code context persisted to `TestSession.CodeContext`; fed into the Analyst prompt.

### Phase 3 — Existing-test dedup
- Reference Test Plan/Suite pickers on both the start form and the Analyst→Tester gate.
- `IExistingTestsIndexService` / `ExistingTestsIndexService`: scans ADO for existing test cases, caches in SQLite (`TestPlanScans`, `ExistingTestCaseIndexEntries`) with a 24h TTL; FTS5 virtual table `test_case_index` mirrors titles + step summaries.
- Two enforcement layers:
  1. **Prompt-level** — `BuildTesterContextAsync` injects existing titles + title-pattern summary into `## Existing Test Cases in ADO` prompt section.
  2. **Post-gen** — `FindDuplicatesAsync` checks each generated test case (title-signature + Jaccard step overlap) against the index; auto-drops clear duplicates, flags borderline matches.
- Manual Rescan button via `POST /api/author-tests/rescan` + `GET /api/author-tests/scan-status`.

## Data changes (migration `AuthorTestsContextAndIndex`, 2026-07-29)

**`TestSessions` — 6 new nullable columns:**
| Column                 | Type       | Purpose                                   |
| ---------------------- | ---------- | ----------------------------------------- |
| `AdditionalContext`    | `TEXT?`    | Free-text passed to Analyst               |
| `CodeContext`          | `TEXT?`    | Code snippet assembled by code-discovery  |
| `TesterContext`        | `TEXT?`    | Free-text passed to Tester                |
| `ReferenceTestPlanId`  | `INTEGER?` | Dedup reference plan                      |
| `ReferenceTestSuiteId` | `INTEGER?` | Dedup reference suite (null = whole plan) |
| `PlanNavigationHint`   | `TEXT?`    | Display hint (plan/suite name)            |

**New tables:**
- `TestPlanScans` — one row per plan/suite scan, with `ScannedAt`, `CaseCount`, `TitlePatternSummary`.
- `ExistingTestCaseIndexEntries` — one row per existing ADO test case, with normalised `TitleSignature`, `StepsSummary`, `StepsHash`.
- `test_case_index` (FTS5 virtual table) — created at startup by `SqliteFts5Search.EnsureSchema`; mirrors `ExistingTestCaseIndexEntries` for fast title/step search.

## Key files

| Area         | File(s)                                                                                                                  |
| ------------ | ------------------------------------------------------------------------------------------------------------------------ |
| Entities     | `Core/Models/TestSession.cs`, `TestPlanScan.cs`, `ExistingTestCaseIndexEntry.cs`                                         |
| Contracts    | `Core/Contracts/AuthorTestsContracts.cs`                                                                                 |
| Interfaces   | `Core/Abstractions/IAgentOrchestrator.cs`, `ICodeContextService.cs`, `IExistingTestsIndexService.cs`                     |
| Agents       | `Infrastructure/Agents/AnalystAgent.cs`, `TesterAgent.cs`                                                                |
| Services     | `Infrastructure/ExistingTests/ExistingTestsIndexService.cs`, `Infrastructure/CodeContext/CodeContextService.cs`          |
| Orchestrator | `Infrastructure/Orchestrators/AgentOrchestrator.cs`                                                                      |
| Controller   | `Infrastructure/Api/Controllers/AuthorTestsController.cs`                                                                |
| DI           | `Infrastructure/DependencyInjection.cs`                                                                                  |
| Migration    | `Infrastructure/Data/Migrations/20260729012836_AuthorTestsContextAndIndex.cs`                                            |
| Frontend     | `client/src/session/configs/authorTests.tsx`, `client/src/api/authorTests.ts`, `client/src/session/SessionWorkspace.tsx` |

## Parity guardrails preserved
- System prompts (embedded `.txt` resources) are **not touched** — all new instructions are in user prompts only.
- `BugOrchestrator` behavior is unchanged.
- ADO write paths (Steps XML, work-item patch format) are unchanged.
- All 156 backend tests pass.
