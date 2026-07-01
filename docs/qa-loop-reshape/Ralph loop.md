# Ralph Loop - Agentic Development Pattern for ADO_Tester

Execute tasks iteratively following this cycle:

1. **Read** `progress.md` to see completed work and resume point.
2. **Select** the highest-priority incomplete task `[ ]` from `prd.md`.
3. **Implement** that single task following existing code patterns (backend in `backend/src/`, frontend in `client/src/`).
4. **Test** — Write/update xUnit tests for backend logic or Jest/Vitest for frontend.
5. **Verify** — Run `dotnet build` (backend) + `dotnet test` and `npm run build` + `npm run lint` (frontend), fix any failures.
6. **Update PRD** — Change `[ ]` to `[x]` for completed task.
7. **Update Progress** — Append to `progress.md`: task ID, files changed, summary. Commit after each verified chunk.
8. **Loop or Complete** — If all tasks done, output `<promise>COMPLETE</promise>`. Otherwise, repeat from step 1.

## Rules
- ONE task per iteration.
- ALWAYS update `progress.md` (your memory between iterations).
- NEVER commit failing code.
- If stuck after 3 attempts: document blocker in `progress.md`, skip to next task.
- Read existing code before writing new code.
- EVEN if a task is marked as completed in PRD and NOT STARTED in `progress.md`, treat it as NOT STARTED and proceed with implementation.
- Commit after each verified chunk (see `commit-after-changes` memory).

## Build/Test Commands

**Backend** (from `backend/` or root with `--project backend\src\QaAutomation.Api`):
```powershell
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~YourTestClass"  # single class
```

**Frontend** (from `client/`):
```bash
npm run build
npm run lint
```

**Full stack verification:**
```powershell
# Backend
cd backend && dotnet build && dotnet test
# Frontend
cd ../client && npm run build && npm run lint
```

## On Failure
- Compilation error → Fix and retry.
- Test failure → Fix code or test, retry.
- 3 failed attempts → Log in `progress.md` with blocker details, move to next task.

## Dev Server (for manual UI verification)
- Backend: `dotnet run --project backend\src\QaAutomation.Api` (`:5250`, Swagger at `/swagger`).
- Frontend: `npm run dev` (`:5173`, proxies `/api/*` to `:5250`).
- Open `http://localhost:5173` to test end-to-end.
