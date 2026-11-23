import { api } from './client'
import { apiUrl } from '../config/env'

// ── Scripts Board API — /api/scripts-board ───────────────────────────────────

export type ReconcileState = 'linked' | 'orphan_disk' | 'orphan_db' | 'no_meta'

export interface ScriptInventoryItem {
  specFilePath: string
  module: string
  type: string
  level: string
  bucket: string | null
  adoTestCaseId: number | null
  draftId: number | null
  tags: string[]
  reconcileState: ReconcileState | string
  meta?: {
    module?: string
    type?: string
    level?: string
    bucket?: string
    tcId?: number | null
    draftId?: number | null
    hash?: string
  } | null
}

export interface ScriptSpecContent {
  specFilePath: string
  code: string
  draftId: number | null
  adoTestCaseId: number | null
  reconcileState: string
  meta: ScriptInventoryItem['meta']
}

export interface ScriptBoardRunFilter {
  module?: string
  level?: string
  type?: string
  bucket?: string
  tag?: string
  specFilePaths?: string[]
  headed?: boolean
  workers?: number
  /** Run profile key (free-form, config-driven) — AUTOMATION_PROFILE for Playwright. */
  profile?: string
}

export type ScriptBoardRunStatus =
  | 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Error' | 'Cancelled'

export interface ScriptBoardRunSummary {
  id: number
  selectionSummary: string
  module: string | null
  headed: boolean
  workers: number | null
  profile: string | null
  status: ScriptBoardRunStatus | string
  passed: number
  failed: number
  skipped: number
  durationMs: number
  reportPath?: string | null
  errorMessage: string | null
  startedAt: string | null
  finishedAt: string | null
  createdAt: string
}

export interface ScriptBoardRunDetail extends ScriptBoardRunSummary {
  specFilePaths: string[]
  junitPath: string | null
  reportPath: string | null
  eventLogJson: string | null
}

export async function getInventory(): Promise<{ total: number; items: ScriptInventoryItem[] }> {
  const { data } = await api.get<{ total: number; items: ScriptInventoryItem[] }>(
    '/api/scripts-board/inventory')
  return data
}

export async function getSpecContent(path: string): Promise<ScriptSpecContent> {
  const { data } = await api.get<ScriptSpecContent>('/api/scripts-board/spec-content', {
    params: { path },
  })
  return data
}

export async function startRun(filter: ScriptBoardRunFilter): Promise<{ runId: number }> {
  const { data } = await api.post<{ runId: number }>('/api/scripts-board/runs', filter)
  return data
}

export async function listRuns(skip = 0, take = 50): Promise<{ total: number; items: ScriptBoardRunSummary[] }> {
  const { data } = await api.get<{ total: number; items: ScriptBoardRunSummary[] }>(
    '/api/scripts-board/runs', { params: { skip, take } })
  return data
}

export async function getRun(id: number): Promise<ScriptBoardRunDetail> {
  const { data } = await api.get<ScriptBoardRunDetail>(`/api/scripts-board/runs/${id}`)
  return data
}

export async function cancelRun(id: number): Promise<{ cancelled: boolean }> {
  const { data } = await api.post<{ cancelled: boolean }>(`/api/scripts-board/runs/${id}/cancel`)
  return data
}

export const runStreamUrl = (id: number) => apiUrl(`/api/scripts-board/runs/${id}/stream`)

export const runReportUrl = (id: number) => apiUrl(`/api/scripts-board/runs/${id}/report/`)
