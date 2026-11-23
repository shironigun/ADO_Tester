# QA Automation — client (React + Vite)

Frontend for the QA Automation app: pick an Azure DevOps story and run the Analyst → Tester / Bug
Writer pipelines, manage the knowledgebase, and generate board reports. Talks to the .NET backend
([`../backend`](../backend)) under `/api`.

Stack: **React 19 + Vite + MUI 9** with **Redux Toolkit** (client UI state) and **React Query**
(server state); SSE for live agent streams.

## Run

```powershell
npm install
npm run dev        # http://localhost:5173  (proxies /api -> http://localhost:5250)
```

Other scripts: `npm run build` (type-check + production build), `npm run preview` (serve the build on
`:4173`), `npm run lint`.

Start the backend first — see the [root README](../README.md) for the full two-terminal runbook.

## Configuration

`VITE_QA_API` sets the backend origin (see [`.env.example`](.env.example)). Leave it **empty** for
local dev — the Vite dev proxy forwards `/api` to `:5250` (see [`vite.config.ts`](vite.config.ts)).
Set an absolute origin (no trailing slash) to point a build at a remote backend. Every transport
(axios client, React Query, `EventSource` streams, the Reports iframe/download, the Settings preview
window, the error logger) builds its URLs from this via `src/config/env.ts`.

## Architecture (re-aligned to your-product conventions, F0–F5)

- `src/api/` — one axios instance (`client.ts`) + per-feature modules (`knowledgebase`, `settings`,
  `diagnostics`, `reports`, `authorTests`, `findBugs`, `batches`); shared `common/errors.ts`.
- `src/app/` — Redux store + slices (e.g. `colorModeSlice`). Redux holds **client UI state only**;
  server state lives in React Query (`useQuery` + status-gated `refetchInterval`, `useMutation`).
- `src/config/env.ts` — central `apiUrl()` + `PROJECT_ID` (the local project id is `1`).
- `src/routes/` — central route table (`AppRoutes.tsx`) + path builders (`paths.ts`); nav and all
  `navigate()` deep-links source their paths from here.
- Live agent streams use the `useEventStream` SSE hook.

`logger.ts` captures uncaught errors, unhandled rejections, React render errors, and failed axios
calls, batching them to `POST /api/logs` (persisted in the backend `ErrorLogs` table).
