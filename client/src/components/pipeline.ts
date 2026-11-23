/**
 * Centralized status → colour maps, previously duplicated across
 * AuthorTestsPage, FindBugsPage and ReportsPage. `StatusChip` reads these.
 * The union covers every status string any page emits.
 */
export type ChipColor = 'default' | 'primary' | 'secondary' | 'warning' | 'success' | 'error' | 'info'

// Session statuses (AuthorTests + FindBugs) and report statuses.
export const statusColors: Record<string, ChipColor> = {
  // in-flight
  Analyzing: 'warning',
  Verifying: 'warning',
  Drafting: 'warning',
  Pushing: 'warning',
  Generating: 'warning',
  // gates / interim
  AwaitingTester: 'info',
  AwaitingActual: 'info',
  Drafted: 'info',
  // terminal
  Done: 'success',
  Failed: 'error',
  Cancelled: 'default',
  // TestRun statuses
  Queued: 'info',
  Running: 'warning',
  Passed: 'success',
  Error: 'error',
}

// Draft lifecycle (test-case + bug drafts).
export const draftColors: Record<string, ChipColor> = {
  Draft: 'default', Approved: 'success', Pushed: 'success', Rejected: 'error',
}

// Bug severity.
export const sevColors: Record<string, ChipColor> = {
  Critical: 'error', High: 'warning', Medium: 'info', Low: 'default',
}

/** Statuses that mean an agent / background job is actively running. */
export const IN_FLIGHT = ['Analyzing', 'Verifying', 'Drafting', 'Pushing', 'Generating', 'Queued', 'Running']
