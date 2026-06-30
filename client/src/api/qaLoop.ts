import { api } from './client'
import { apiUrl } from '../config/env'
import type { SessionSummary, SessionDetail } from '../session/types'

// ── QA Loop Track B API — /api/qaloop ────────────────────────────────────────
// Re-export plan/suite/workitem pickers (module-agnostic, reuse author-tests endpoints).
export { getPlans, getSuites, searchWorkItems } from './authorTests'
export type { TestPlan, TestSuite } from './authorTests'

// ── Request / response types matching backend QaLoopContracts.cs ─────────────

export interface ScanStartRequest {
  module: string
  repository?: string
  branch?: string
  sourceDirs?: string[]
  extensions?: string[]
  workItemId?: number
  testPlanId?: number
  testSuiteId?: number
  autoHandoff?: boolean
}

export interface ScanStartResponse {
  sessionId: number
  existingSessionId?: number | null
}

export interface SurfaceRow {
  surface: string
  level: string
  route: string
  method?: string
  status: string
}

/** Wire shape of a QA Loop session summary (GET /api/qaloop). */
export interface ApiSessionSummary extends SessionSummary {
  module: string | null
  draftCount: number
}

/** Wire shape of a QA Loop session detail (GET /api/qaloop/{id}). */
export interface ApiSessionDetail extends SessionDetail {
  module: string | null
  testPlanId?: number | null
  testSuiteId?: number | null
  inventoryJson?: string | null
}

export interface ApiTestDraftDto {
  id: number
  sessionId: number
  status: string
  level: string
  title: string
  preconditions: string
  stepsJson: string | null
  specCode: string | null
  adoTestCaseId: number | null
  tags: string[]
  sourceRoutes: string[]
}

export interface UpdateApiDraftRequest {
  status?: string
  title?: string
  preconditions?: string
  stepsJson?: string
  specCode?: string
}

// ── API methods ───────────────────────────────────────────────────────────────

export async function startScan(req: ScanStartRequest): Promise<ScanStartResponse> {
  const { data } = await api.post<ScanStartResponse>('/api/qaloop/start', req)
  return data
}

export async function listSessionsPage(
  take: number, skip: number,
): Promise<{ total: number; items: ApiSessionSummary[] }> {
  const { data } = await api.get<{ total: number; items: ApiSessionSummary[] }>(
    '/api/qaloop', { params: { take, skip } })
  return data
}

export async function getSessionDetail(id: number): Promise<ApiSessionDetail> {
  const { data } = await api.get<ApiSessionDetail>(`/api/qaloop/${id}`)
  return data
}

/** Full-replace PATCH: replaces the session's analysis rows. */
export async function saveAnalysis(id: number, rows: SurfaceRow[]): Promise<void> {
  await api.patch(`/api/qaloop/${id}/analysis`, { rows })
}

export async function generateDrafts(id: number): Promise<void> {
  await api.post(`/api/qaloop/${id}/generate-drafts`)
}

/** Skip-null PATCH — omit keys to leave them unchanged. */
export async function patchDraft(
  sessionId: number, draftId: number, req: UpdateApiDraftRequest,
): Promise<ApiTestDraftDto> {
  const { data } = await api.patch<ApiTestDraftDto>(
    `/api/qaloop/${sessionId}/drafts/${draftId}`, req)
  return data
}

export async function approveAllDrafts(id: number): Promise<void> {
  await api.post(`/api/qaloop/${id}/approve-all`)
}

export async function pushSession(
  id: number, body: { testPlanId: number; testSuiteId: number; storyId?: number },
): Promise<void> {
  await api.post(`/api/qaloop/${id}/push`, body)
}

export async function pushDraft(
  sessionId: number, draftId: number,
  body: { testPlanId: number; testSuiteId: number; storyId?: number },
): Promise<ApiTestDraftDto> {
  const { data } = await api.post<ApiTestDraftDto>(
    `/api/qaloop/${sessionId}/drafts/${draftId}/push`, body)
  return data
}

export async function cancelSession(id: number): Promise<void> {
  await api.post(`/api/qaloop/${id}/cancel`)
}

export const sessionStreamUrl = (id: number) => apiUrl(`/api/qaloop/${id}/stream`)

// Org-level ADO work-item deep link (unchanged from old qaLoop.ts).
export const adoWorkItemUrl = (id: number) =>
  `https://dev.azure.com/ConstellationDealer/_workitems/edit/${id}`
