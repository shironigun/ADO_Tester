# Progress — QA Loop Smart Module Discovery

## Status

- Started: 2026-07-30
- Resume point: `QSD-101`
- Parent design: `docs/progress/RalphLoop/qa-loop-v2/solution.md`
- Existing QA Loop v2 status: Slabs 1 and 2 marked complete; Scripts Board remains in progress.

## Known blockers

- Live ADO verification uses story 177540. If ADO PAT validation fails, record it rather than claiming a
  live pass.
- Browser exploration needs a configured Playwright root, official MCP agent definitions, app URL, and
  valid browser-login storage state.

## Log

### 2026-07-30 — scaffold

Created the dedicated Ralph-loop documentation set. The feature addresses the source-discovery gap where
QA Loop incorrectly treats a module name as a repository-root path after a nested frontend path fails.
The target is a repository/branch-grounded route → frontend API → backend handler trace before existing
three-level API generation, plus opt-in UI Phase browser exploration.

`QSD-101` verified: all five required Ralph-loop documents exist. Resume point: `QSD-102`.

### 2026-07-30 — QSD-102

- Module dropdown discovery and rediscovery now receive the selected repository and branch. Changing
  either selection clears stale module results.
- `AzureDevOpsClient.SearchCodeAsync` now honors its explicit repository argument; QA Loop passes the
  session repository when resolving analyzer-requested backend files.
- Resume point: `QSD-103`.

### 2026-07-30 — QSD-103 / QSD-104

- Added `ModuleSourceDiscoveryService`: it lists the selected branch root once, scores bounded
  frontend files, extracts Axios/client/fetch API calls, ranks backend `.cs` candidates, and creates
  an explicit frontend/backend source bundle for the analyzer.
- The normal fetch path invokes smart discovery only after configured frontend/source paths produce no
  files. It persists `DiscoveryJson` and derived source directories; the additive
  `AddQaLoopDiscoveryEvidence` migration was generated for SQLite and PostgreSQL.
- API session details expose discovery evidence; the scan form now includes an optional Source Dirs
  override. Backend regression tests pass (187/187); resume point: `QSD-105`.

### 2026-07-30 — QSD-105 / QSD-106

- `hasUiPhase` now persists from the scan request, enabling the existing UI Phase tab and endpoints for
  opted-in sessions. Playwright generation receives the selected frontend route and instructs its MCP
  agent to navigate and inspect that route before choosing locators.
- Playwright framework validation now fails clearly when `.mcp.json` has not been initialized with
  `npx playwright init-agents --loop=claude`.
- Frontend module results are branch-specific, changing repo/branch clears stale choices, and the scan
  form provides a newline/semicolon-delimited Source Dirs override.
- Backend regression tests pass (187/187); frontend production build passes. Resume point: `QSD-107`
  manual nested-module verification and feature-doc close-out.
