// Single source of truth for client route paths (F4). Pages, the sidebar nav, the session-module
// configs, and the full-screen navigate() calls all build URLs from here instead of repeating string
// literals, so a path only ever changes in one place.
export const paths = {
  dashboard: '/projects',
  knowledgebase: '/knowledgebase',
  qaLoop: '/qa-loop',
  qaRuns: '/qa-runs',
  qaPipelines: '/qa-pipelines',
  schemaExplorer: '/schema-explorer',
  authorTests: '/author-tests',
  findBugs: '/find-bugs',
  reports: '/reports',
  settings: '/settings',

  /** Full-screen single-session detail, e.g. /author-tests/123 (base = a config.route). */
  session: (base: string, id: number | string) => `${base}/${id}`,
  /** Full-screen epic/batch roll-up, e.g. /find-bugs/epic/45 (base = a config.route). */
  epic: (base: string, batchId: number | string) => `${base}/epic/${batchId}`,
} as const
