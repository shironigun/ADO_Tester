# Author Tests Enhancements — Design Plan

> See `tracker.md` for live task status. This doc captures the architectural decisions made at planning time.

## Goal

Extend the Author Tests pipeline with three capabilities, delivered in phases:

1. **Free-text context + refine loops** — tester-supplied context at start and at the Analyst→Tester gate; Analyst refine (fill gaps without restarting); Tester refine (merge rather than wipe).
2. **Code discovery** — shared `ICodeContextService` extracts the feature's code from the ADO repo, anchored on linked PRs/commits + free-text route/screen hints; feeds real code into the Analyst.
3. **Existing-test dedup** — cached plan scan/index (`IExistingTestsIndexService`) so the Tester avoids recreating test cases already in ADO; post-gen similarity check drops clear duplicates, flags borderline ones.

## Pipeline shape (after all phases)

```
Fetch → Retrieve KB → Discover code → Analyst → (pause) → Tester
  0           1             2            3                    4
```

## Key design decisions

| Decision | Rationale |
|----------|-----------|
| `alwaysSearch: true` for Author Tests | Thin tickets have no linked PRs; search on hints/feature terms must run unconditionally |
| Bug-pipeline parity unchanged | `BugOrchestrator` left as-is; `CodeContextService` used by Author Tests only initially |
| 24h TTL for plan scan cache | Balances freshness vs. ADO API quota; "Rescan plan" button lets user force refresh |
| Merge (not wipe) on Tester refine | Preserving pushed tests is load-bearing; unflagged draft tests survive by default |
| Post-gen dedup: auto-drop clear, flag borderline | Never silently discard ambiguous cases |
| EF migration user-runs locally | No `dotnet` SDK on CI container |
| Docs updated after verification | Per documentation-discipline rule in `CLAUDE.md` |

## Shared services

- **`ICodeContextService`** (`Core/Abstractions/`, impl in `Infrastructure/CodeContext/`) — `DiscoverAsync` with `extraHintText` + `alwaysSearch` knobs; wraps ADO Git surface (`GetWorkItemGitRefsAsync`, `GetPullRequestDiffAsync`, `SearchCodeAsync`, `GetFileContentAsync`).
- **`IExistingTestsIndexService`** (`Core/Abstractions/`, impl in `Infrastructure/ExistingTests/`) — `EnsureIndexAsync` / `BuildTesterContextAsync` / `FindDuplicatesAsync`; SQLite FTS5 `test_case_index` virtual table; `TestPlanScan` + `ExistingTestCaseIndexEntry` EF models.

## Frontend injection points

| Gate | What's added |
|------|-------------|
| Start form (`AuthorStartForm`) | Additional context field; ref plan/suite pickers + nav hint |
| AwaitingTester banner | Extra Tester context field; ref plan/suite pickers + Rescan + scan status; nav hint |
| Discover panel (`sel === 2`) | Code snippet card or "no code context" fallback |
| Tester panel (`sel === 4`) | Dedup results card (skipped / flagged chips) |
