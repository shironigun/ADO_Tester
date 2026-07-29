# Progress: QA Loop v2 Implementation

## Overview
- **Design:** `solution.md` (authoritative vision brief) · **Ruleset:** `Ralph loop.md`
- **Backlog:** `prd.md` (tasks `QLV2-###`, grouped by slab, built in order) · **Guide:** `blueprint.md`
- **Started:** 2026-07-29
- **Status:** DOCS COMPLETE — full Ralph-loop doc set written (`solution.md` / `blueprint.md` / `prd.md` /
  `Ralph loop.md` / this file). **Implementation is executed LOCALLY** on the .NET-capable Windows machine
  (this authoring container has no .NET SDK and the network blocks Microsoft's dotnet hosts — see the
  blocker log below). Slab 1 is broken down to executable tasks (QLV2-101..113); Slabs 2–3 carry the
  locked design + epic backlogs (QLV2-2xx/3xx) to be decomposed to file level when each is implemented.
- **Resume point for local execution:** start at **QLV2-101**; follow `Ralph loop.md` every iteration.

## Slab status
| Slab | Name | Status |
|------|------|--------|
| 1 | Free-text + merge-refine in QA Loop | Not started |
| 2 | UI testing phase (Playwright agents via MCP + Claude CLI) | Not started |
| 3 | Scripts Board module | Not started |

---

## Log

### 2026-07-29 (scaffold)
- Finalized the three-slab vision with the user (decision-by-decision): one QA-Loop run / two phases
  (API → UI); Slab 1 = three free-text touchpoints + merge-refine; Slab 2 = UI phase built by the
  **official Playwright agents (planner/generator/healer) over Playwright MCP + Claude CLI** (no code
  grounding), automated-login `storageState`, UI 4th level + smoke/regression/e2e buckets + ADO UI suite
  tree, human heal-or-bug triage, generic across modules; Slab 3 = Scripts Board (new coexisting module,
  DB + `@qa-meta` identity, inventory/execution/trends/flakiness, xterm.js live terminal).
- Created `docs/progress/RalphLoop/qa-loop-v2/` with `Ralph loop.md` (ruleset) and the authoritative
  overarching `solution.md`. Established the three explore-agent codebase maps (Author Tests pipeline,
  QA Loop internals + legacy UI generator, frontend module patterns) referenced throughout the design.
- Wrote `blueprint.md` (Slab 1 detailed; Slabs 2–3 outlined) and `prd.md` (Slab 1 tasks QLV2-101..113;
  Slabs 2–3 epics QLV2-2xx/3xx).

### 2026-07-29 (BLOCKER — no .NET SDK in this environment)
- **Environment blocker for backend build/test.** This remote Linux container has **only Node/npm** (v22)
  — **no .NET SDK**, and the network policy **denies** the dotnet build hosts (`builds.dotnet.microsoft.com`
  → 403 on CONNECT; allowlist is npm/pypi/crates/go registries only, no Microsoft dotnet host). The repo's
  `launch.json` backend points at a Windows `C:\Program Files\dotnet\dotnet.exe` — the .NET backend is
  meant to be built on the user's Windows machine, not here.
- **Impact:** Slab 1 is ~90% backend (entity/migration/agents/orchestrator/controller/contracts). I cannot
  run `dotnet build`/`dotnet test` here, so I cannot satisfy the Ralph-loop "verify before commit" rule for
  backend tasks in-container. Frontend tasks (QLV2-110/111) ARE verifiable here (`npm run build`/`lint`).
- **Resolution (user):** author the **documentation** here; **execute the implementation locally**
  afterwards on the Windows machine. No backend code is written in this container — the doc set is the
  deliverable, and the local Ralph-loop run builds/tests/commits each task with real `dotnet`.
