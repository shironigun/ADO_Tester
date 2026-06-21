// Single source of truth for the API base URL and shared client config (F1/F5). Every transport —
// axios, React Query, EventSource, the Reports iframe, the Settings preview window — must build URLs
// from here, so the app can be pointed at a different backend purely via the VITE_QA_API env var.
//
// Empty VITE_QA_API → same-origin (the Vite dev proxy forwards /api → :5250 in dev; a reverse proxy
// or static-host rewrite serves /api in prod). A set value (e.g. https://api.example.com) is used verbatim.
const API_BASE = (import.meta.env.VITE_QA_API ?? '').replace(/\/+$/, '')

/** Build an API URL from a path. Accepts "/api/..." or "api/..." and prefixes the configured base. */
export function apiUrl(path: string): string {
  return `${API_BASE}${path.startsWith('/') ? path : `/${path}`}`
}

export { API_BASE }

/** The single local workspace id (matches the backend's seeded Project id=1). Centralized here so the
 *  six pages that hardcoded `PROJECT_ID = 1` share one definition. */
export const PROJECT_ID = 1
