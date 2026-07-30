# Blueprint — QA Loop Smart Module Discovery

## QSD-102: branch-aware module discovery

- `client/src/session/configs/qaLoop.tsx`: pass current repository and branch to
  `listFrontendModules()` and rediscovery; seed form defaults from ADO settings.
- `QaLoopController.FrontendModules`: preserve request repository/branch.
- `FrontendModuleService`: use selected repo/branch in cache key and route extraction; add discovery
  result support for a selected module when its stored component path is invalid.
- `IAzureDevOpsClient` / `AzureDevOpsClient`: make code search use explicit repo and branch.

## QSD-103: source discovery + trace

- Add Core contracts for `ModuleDiscoveryResult`, frontend source files, API calls, backend files, and
  confidence/explanation.
- Add `IModuleSourceDiscoveryService` + scoped implementation under `Infrastructure/QaLoop`.
- List the branch tree once; use route/component filename scoring plus an LLM JSON path picker only when
  deterministic candidates are insufficient.
- Add a deterministic frontend API parser for Axios, `fetch`, service wrappers, and RTK-style endpoints.
- Resolve backend candidates in the same repository/branch and cap files/content before building the
  analyzer bundle.

## QSD-104: QA Loop integration

- Add nullable `DiscoveryJson` to `ApiTestSession`, EF mapping, and migration.
- Replace `FetchSourceAsync`'s invalid-path/module-root fallback with discovery service usage.
- Persist discovered frontend/backend dirs and clear SSE events for each stage.
- Feed route/API/handler evidence to `ModuleAnalyzerAgent` through its user prompt; preserve embedded
  system prompt bytes.
- Add a re-discover endpoint and session-detail mapping.

## QSD-105: opt-in UI exploration

- Add `HasUiPhase` to scan request and persist it at controller creation.
- Validate Playwright framework/MCP agents; invoke auth setup to create/refresh storage state.
- Add a planner exploration API returning bounded UI evidence for the discovered route.
- Feed evidence to UI-case authoring; keep manual approval before script generation.
- Add storage state/global setup to UI execution and map failures to healable draft evidence.

## QSD-106: frontend

- Add API types/actions for discovery evidence and re-discover.
- Show route, files, API calls, backend files, confidence, and optional source-dir override on the
  coverage step.
- Show UI exploration progress/evidence in UI Phase.

## QSD-107: verification/docs

- Unit tests for path fallback, parser patterns, session repo/branch isolation, backend resolution, and
  UI toggle persistence.
- Full backend/frontend verification; manual nested-module discovery; live UI Phase only when its
  credentials/framework are configured.
- Feature doc and changelog after verification.
