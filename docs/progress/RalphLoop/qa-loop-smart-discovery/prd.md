# PRD — QA Loop Smart Module Discovery

- [x] **QSD-101** Create Ralph-loop design, blueprint, PRD, and progress documents. **AC:** docs define
  discovery, trace, optional UI exploration, and verification rules.
- [x] **QSD-102** Make module route discovery use the selected scan repository/branch; make code search
  honor session repository/branch. **AC:** B/T; no global-default repository leakage.
- [x] **QSD-103** Add bounded branch-tree module/source discovery and deterministic frontend API extraction.
  **AC:** B/T; nested module finds route/component/API clients after an invalid explicit path.
- [x] **QSD-104** Integrate discovery evidence into `FetchSourceAsync` and `ModuleAnalyzerAgent`; persist
  discovery JSON, add migration/contracts/re-discover action. **AC:** B/T; discovered route/API/backend
  evidence produces non-empty three-level coverage.
- [x] **QSD-105** Complete UI Phase opt-in persistence and add authenticated MCP planner exploration before
  UI case authoring. **AC:** B/T; no browser for API-only scans; missing prerequisites are actionable.
- [x] **QSD-106** Add frontend evidence/retry/source-dir override and UI exploration views. **AC:** F.
- [ ] **QSD-107** Full tests, nested-module manual pass, feature doc/changelog, and PRD close-out.
  **AC:** B/T/F; ADO verification uses story 177540 or documented PAT blocker.
