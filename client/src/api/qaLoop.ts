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
  frontendModule?: string
  frontendPath?: string
  sourceDirs?: string[]
  extensions?: string[]
  workItemId?: number
  testPlanId?: number
  testSuiteId?: number
  autoHandoff?: boolean
}

// A product frontend module discovered from the SPA route file (QaLoopController /frontend-modules).
export interface FrontendModule {
  moduleName: string
  routePath: string
  componentPath: string
  url: string
}

export interface ScanStartResponse {
  sessionId: number
  existingSessionId?: number | null
}

// Wire shape of a SurfaceRow EXACTLY as it appears inside the raw analysisJson string — that blob is
// produced by QaLoopOrchestrator's own JsonSerializer.Serialize call (Infrastructure), not through the
// API's camelCase MVC pipeline, so it keeps C#'s default PascalCase property names, and TestLevel
// serializes as its ordinal (0=Contract, 1=Endpoint, 2=Workflow, 3=Ui) since no string enum converter
// applies there either. Do not "fix" the casing here — match what's actually on the wire.
export interface SurfaceRow {
  Id: string
  Level: number
  Label: string
  Status: 'gap' | 'covered'
  LinkedTestCaseId: number | null
  Selected: boolean
}

export const LEVEL_NAMES = ['Contract', 'Endpoint', 'Workflow', 'Ui'] as const

/** Wire shape of a QA Loop session summary (GET /api/qaloop). */
export interface ApiSessionSummary extends SessionSummary {
  module: string | null
  draftCount: number
  activityEnabled: boolean
}

/** Wire shape of a QA Loop session detail (GET /api/qaloop/{id}). */
export interface ApiSessionDetail extends SessionDetail {
  module: string | null
  testPlanId?: number | null
  testSuiteId?: number | null
  inventoryJson?: string | null
  activityEnabled: boolean
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

// ── API methods (routes match QaLoopController.cs exactly) ────────────────────

export async function startScan(projectId: number, req: ScanStartRequest): Promise<ScanStartResponse> {
  const { data } = await api.post<ScanStartResponse>(`/api/qaloop/${projectId}/scan/start`, req)
  return data
}

export async function listFrontendModules(repository?: string, branch?: string): Promise<FrontendModule[]> {
  const params: Record<string, string> = {}
  if (repository) params.repository = repository
  if (branch) params.branch = branch
  const { data } = await api.get<FrontendModule[]>('/api/qaloop/frontend-modules', { params })
  return data
}

export async function listSessionsPage(
  projectId: number, take: number, skip: number,
): Promise<{ total: number; items: ApiSessionSummary[] }> {
  const { data } = await api.get<{ total: number; items: ApiSessionSummary[] }>(
    `/api/qaloop/sessions/${projectId}`, { params: { take, skip } })
  return data
}

export async function getSessionDetail(id: number): Promise<ApiSessionDetail> {
  const { data } = await api.get<ApiSessionDetail>(`/api/qaloop/sessions/detail/${id}`)
  return data
}

/** Full-replace PATCH: send EVERY row (not just the selected ones) — omitted rows are dropped
 * from the matrix entirely, so deselecting a row must still resend it with Selected=false. */
export async function saveAnalysis(id: number, module: string, rows: SurfaceRow[]): Promise<void> {
  await api.patch(`/api/qaloop/sessions/${id}/analysis`, { module, rows })
}

export async function generateDrafts(id: number): Promise<void> {
  await api.post(`/api/qaloop/sessions/${id}/generate-drafts`)
}

/** Skip-null PATCH — omit keys to leave them unchanged. No sessionId in the route: drafts are
 * addressed by their own id. */
export async function patchDraft(draftId: number, req: UpdateApiDraftRequest): Promise<ApiTestDraftDto> {
  const { data } = await api.patch<ApiTestDraftDto>(`/api/qaloop/drafts/${draftId}`, req)
  return data
}

export async function approveAllDrafts(id: number): Promise<void> {
  await api.post(`/api/qaloop/sessions/${id}/approve-all`)
}

/** Skip-null PATCH — set testPlanId/testSuiteId/workItemId on a session that was started without them. */
export async function updateSessionPlan(
  id: number, req: { testPlanId?: number; testSuiteId?: number; workItemId?: number },
): Promise<ApiSessionDetail> {
  const { data } = await api.patch<ApiSessionDetail>(`/api/qaloop/sessions/${id}/plan`, req)
  return data
}

/** Reads Plan/Suite/Story from the session itself — no body. */
export async function pushSession(id: number): Promise<void> {
  await api.post(`/api/qaloop/sessions/${id}/push`)
}

export async function pushDraft(draftId: number): Promise<ApiTestDraftDto> {
  const { data } = await api.post<ApiTestDraftDto>(`/api/qaloop/drafts/${draftId}/push`)
  return data
}

export async function cancelSession(id: number): Promise<void> {
  await api.post(`/api/qaloop/sessions/${id}/cancel`)
}

export async function deleteSession(id: number): Promise<void> {
  await api.delete(`/api/qaloop/sessions/${id}`)
}

export const sessionStreamUrl = (id: number) => apiUrl(`/api/qaloop/sessions/${id}/stream`)

// Org-level ADO work-item deep link (unchanged from old qaLoop.ts).
export const adoWorkItemUrl = (id: number) =>
  `https://dev.azure.com/your-org/_workitems/edit/${id}`
