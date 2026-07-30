# Ralph Loop — QA Loop Smart Module Discovery

This effort makes a QA Loop scan discover a nested frontend module from its selected repository and
branch, trace frontend API calls to backend handlers, and produce Contract, Endpoint, and Workflow
coverage without requiring a manually correct source path.

## Task order

1. `QSD-101` — design/doc scaffold
2. `QSD-102` — branch-aware route/module discovery
3. `QSD-103` — deterministic frontend API-call trace + backend resolution
4. `QSD-104` — analyzer/orchestrator integration + persisted evidence
5. `QSD-105` — opt-in UI Phase contract and Playwright exploration wiring
6. `QSD-106` — frontend evidence/overrides
7. `QSD-107` — full verification and close-out docs

## Per-iteration rules

1. Read `progress.md`, select the highest incomplete PRD task, and implement only that task.
2. Preserve byte-exact ADO write behavior; new agent instructions belong in user prompts.
3. Use additive migrations only. Long-running actions use a background task with its own DI scope,
   `IEventBus` SSE events, and cancellation registry.
4. Run backend build/tests and frontend build/lint appropriate to the task before marking it complete.
5. Update both `prd.md` and `progress.md` after verified work. Do not commit failing code.
6. Use story **177540** for any manual ADO write/link verification. If the PAT blocks it, record the
   blocker rather than claiming a live pass.
