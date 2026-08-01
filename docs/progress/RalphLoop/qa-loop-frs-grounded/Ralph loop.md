# Ralph Loop Ruleset — QA Loop KB-Grounded Redesign

## Source of truth

This file is the ruleset for the `qa-loop-frs-grounded` Ralph loop. Read it every iteration.

**Design:** `solution.md` (this folder) — authoritative locked vision brief.
**Backlog:** `prd.md` (tasks `KBG-###`) — acceptance criteria per task.
**Guide:** `blueprint.md` — file-by-file implementation detail.
**Tracker:** `progress.md` — resume point, log, and blockers.

## Standard rules (from CLAUDE.md)

1. **One task per iteration.** Pick the highest-priority incomplete `[ ]` from `prd.md`, implement it, verify, flip to `[x]`, log in `progress.md`, then stop.
2. **`progress.md` is memory.** Update it every iteration before stopping. It is the only source of resume state.
3. **Never commit failing code.** `dotnet build` + `dotnet test` + `npm run build` + `npm run lint` must all be green before any commit.
4. **If a task is `[x]` in `prd.md` but has no log entry in `progress.md`, treat it as not started.**
5. **If stuck after 3 attempts, log the blocker in `progress.md` and move to the next task.**
6. **Verification before close.** Run the verification steps from `solution.md §Verification` for that task before flipping `[x]`.

## Machine-specific rules (from ralph-loop-ops.mdc)

- Every `Shell` tool call **must** pass `required_permissions: ["all"]`.
- Standing verification story: **177540**.
- If the ADO PAT is expired (401), record ADO-touching verification as pending rather than faking the check.

## Invariants specific to this loop

- **Zero sharing with Author Tests.** No `using` reference to Author Tests infrastructure namespaces in any QA Loop file. New agents, new types, new services — even if the implementation looks similar.
- **Frozen system prompts.** All existing system prompt bytes (`ModuleAnalyzerSystem`, generator system prompts) are untouched. All new content goes into user prompts only. New agents (`QaLoopUiAnalystAgent`, `QaLoopUiCaseAuthorAgent`) get their own new embedded resources.
- **Healer never fires automatically.** Every healer invocation requires an explicit user action.
- **UI Phase is suite-only.** No story / work-item link is ever presented for UI test cases in this loop.
- **KB directory structure is preserved.** `FolderPath` is always stored and never flattened or inferred post-upload.
- **`AdoGoldenTests` stays green.** `@qa-meta` structure is unchanged; only `module` and `tags` values change when FRS context is present.
- **Graceful degradation on missing KB.** If no FRS documents are uploaded, the pipeline emits a `Warn` event and continues without KB context.
