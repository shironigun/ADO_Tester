/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend API base URL. Empty → same-origin (dev proxy / prod rewrite). See config/env.ts. */
  readonly VITE_QA_API?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
