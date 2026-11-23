import type { ComponentType, ReactNode } from 'react'
import type { PipelineStep } from '../components/AgentPipeline'

// Shared session shapes — a superset that tolerates the few bug-only fields as optional.
export interface SessionSummary {
  id: number
  workItemId: number | null
  workItemTitle: string | null
  status: string
  createdAt: string
  errorMessage: string | null
}

export interface SessionDetail {
  id: number
  projectId?: number
  workItemId: number | null
  workItemTitle: string | null
  status: string
  currentActivity: string | null
  analysisJson: string | null
  errorMessage: string | null
  eventLogJson?: string | null
  createdAt?: string
  updatedAt?: string
  drafts: AnyDraft[]
  // author-tests context/refine (optional, tolerated)
  additionalContext?: string | null
  testerContext?: string | null
  referenceTestPlanId?: number | null
  referenceTestSuiteId?: number | null
  planNavigationHint?: string | null
  // bug-only (optional, tolerated)
  actualBehavior?: string | null
  codeContext?: string | null
  codeBehaviorJson?: string | null
  codeVerificationJson?: string | null
  // qa-loop only (optional, tolerated)
  module?: string | null
  inventoryJson?: string | null
}

export interface AnyDraft {
  id: number
  status: 'Draft' | 'Approved' | 'Rejected' | 'Pushed'
  [k: string]: unknown
}

export interface WorkItemOption {
  id: number
  title: string
  workItemType: string
  state?: string | null
}

export interface AnalyticsColumn {
  key: string
  label: string
  align?: 'left' | 'right'
  /** Derive the cell value from a (possibly undefined while loading) detail. */
  value: (d: SessionDetail | undefined) => ReactNode
}

// Props the shell hands to a module's DetailBody.
export interface DetailBodyProps {
  session: SessionDetail
  /** Re-fetch the session detail after a mutation. */
  refetch: () => void
  /** Report an action error to the shell's snackbar. */
  onError: (e: unknown, fallback: string) => void
  /** True in the full-screen route (lets the body relax width constraints). */
  fullScreen?: boolean
}

// Props the shell hands to a module's StartForm (inside the New dialog).
export interface StartFormProps {
  onStarted: (sessionId: number) => void
  onError: (e: unknown, fallback: string) => void
  onClose: () => void
}

export interface SessionModuleConfig {
  key: 'author-tests' | 'find-bugs' | 'qa-loop'
  apiBase: string
  route: string
  labels: { title: string; subtitle: string; newCta: string }
  moduleChip: { label: string; icon: ReactNode }
  /** Statuses that mean "still working" → poll + show live pulse. */
  inFlightStatuses: string[]
  /** Optional: additional in-flight check for UI-phase status (e.g. uiStatus field). */
  getUiStatus?: (d: SessionDetail) => string | undefined
  uiInFlightStatuses?: string[]
  pipelineSteps: PipelineStep[]
  activeStepIndex: (status: string, activity: string | null) => number
  analyticsColumns: AnalyticsColumn[]
  /** Optional custom identity renderer; when absent, sessionIdentity falls back to "#id — title". */
  renderIdentity?: (s: SessionSummary | SessionDetail) => string

  // data access
  listSessions: (take: number, skip: number) => Promise<{ total: number; items: SessionSummary[] }>
  getDetail: (id: number) => Promise<SessionDetail>
  streamUrl: (id: number) => string
  /** Optional: permanently delete a session and all its drafts. */
  deleteSession?: (id: number) => Promise<void>

  // module-specific UI
  StartForm: ComponentType<StartFormProps>
  DetailBody: ComponentType<DetailBodyProps>
}
