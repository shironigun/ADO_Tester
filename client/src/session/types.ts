import type { ComponentType, ReactNode } from 'react'
import type { PipelineStep } from '../components/AgentPipeline'

// Shared session shapes — a superset that tolerates the few bug-only fields as optional.
export interface SessionSummary {
  id: number
  workItemId: number
  workItemTitle: string
  status: string
  createdAt: string
  errorMessage: string | null
}

export interface SessionDetail {
  id: number
  projectId?: number
  workItemId: number
  workItemTitle: string
  status: string
  currentActivity: string | null
  analysisJson: string | null
  errorMessage: string | null
  eventLogJson?: string | null
  createdAt?: string
  updatedAt?: string
  drafts: AnyDraft[]
  // bug-only (optional, tolerated)
  actualBehavior?: string | null
  codeContext?: string | null
  codeVerificationJson?: string | null
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
  key: 'author-tests' | 'find-bugs'
  apiBase: string
  route: string
  labels: { title: string; subtitle: string; newCta: string }
  moduleChip: { label: string; icon: ReactNode }
  /** Statuses that mean "still working" → poll + show live pulse. */
  inFlightStatuses: string[]
  pipelineSteps: PipelineStep[]
  activeStepIndex: (status: string, activity: string | null) => number
  analyticsColumns: AnalyticsColumn[]

  // data access
  listSessions: (take: number, skip: number) => Promise<{ total: number; items: SessionSummary[] }>
  getDetail: (id: number) => Promise<SessionDetail>
  streamUrl: (id: number) => string

  // module-specific UI
  StartForm: ComponentType<StartFormProps>
  DetailBody: ComponentType<DetailBodyProps>
}
