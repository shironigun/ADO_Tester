# Solution — QA Loop Smart Module Discovery

## Goal

A QA Loop scan with repository, branch, and module context discovers the frontend route and component
tree from the selected branch, traces API calls into backend handlers, and supplies grounded evidence
to the existing Contract, Endpoint, and Workflow generators.

The optional UI Phase remains separate: after API analysis it uses authenticated, headless Playwright
MCP exploration to ground UI cases and scripts. It is Claude CLI-only; the normal API analysis can use
the selected LLM provider.

## Flow

```text
repository + branch + module
  → list bounded branch tree
  → route/module discovery
  → component and API-client discovery
  → deterministic API-call extraction
  → backend handler resolution in same repo/branch
  → ModuleAnalyzer inventory + coverage matrix
  → Contract / Endpoint / Workflow drafts
  → optional UI Phase: auth storageState → MCP planner exploration → UI cases → approved scripts
```

## Decisions

- Explicit `FrontendPath` and `SourceDirs` win when valid; a 404/empty result triggers discovery rather
  than a top-level `{module}` folder assumption.
- API-call extraction is deterministic first (`fetch`, Axios, service wrappers, RTK Query); the LLM
  resolves ambiguity from bounded evidence rather than inventing routes.
- Code search must receive the session repository and branch; it cannot silently use global ADO defaults.
- Discovery provenance is persisted on the session for review, retry, reproducibility, and user override.
- UI Phase is opt-in via `HasUiPhase`; no browser launches for API-only scans.
- Browser exploration requires `AppUrl`, login configuration/storage state, `PlaywrightRoot`, and the
  official Playwright planner/generator/healer MCP setup.

## Invariants

- Product repo is read-only.
- New settings follow .env → typed options → runtime overlay.
- Generated API specs, ADO writes, suites, and review gates retain their current behavior.
- All discovery file lists and content bundles are capped to control cost/context size.
