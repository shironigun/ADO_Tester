import { api } from './client'
import { apiUrl } from '../config/env'
import type { SessionSummary, SessionDetail, WorkItemOption } from '../session/types'

// Author-tests feature API — every call site for the Author Tests pipeline (session list/detail,
// draft edits, analyst/tester actions, push) routes through the centralized api client here.
export interface TestPlan { id: number; name: string }
export interface TestSuite { id: number; name: string }

// Re-exported so the dashboard + api/findBugs keep importing the canonical session shape from here.
export type { SessionSummary } from '../session/types'

export async function getPlans(): Promise<TestPlan[]> {
  const { data } = await api.get<TestPlan[]>('/api/author-tests/plans')
  return data
}

export async function getSuites(planId: number): Promise<TestSuite[]> {
  const { data } = await api.get<TestSuite[]>(`/api/author-tests/plans/${planId}/suites`)
  return data
}

export async function searchWorkItems(q: string): Promise<WorkItemOption[]> {
  const { data } = await api.get<WorkItemOption[]>('/api/author-tests/search', { params: { q } })
  return data
}

export interface StartSessionBody {
  workItemId: number
  autoHandoff: boolean
  additionalContext?: string
  referenceTestPlanId?: number
  referenceTestSuiteId?: number
  planNavigationHint?: string
}

export async function startSession(
  projectId: number, body: StartSessionBody,
): Promise<{ sessionId: number }> {
  const { data } = await api.post<{ sessionId: number }>(`/api/author-tests/${projectId}/start`, body)
  return data
}

// Dashboard helper — items only.
export async function listSessions(projectId: number, take = 50): Promise<SessionSummary[]> {
  const { data } = await api.get<{ items: SessionSummary[] }>(
    `/api/author-tests/sessions/${projectId}`, { params: { take } })
  return data.items ?? []
}

// Paged list (total + items) for the session grid.
export async function listSessionsPage(
  projectId: number, take: number, skip: number,
): Promise<{ total: number; items: SessionSummary[] }> {
  const { data } = await api.get<{ total: number; items: SessionSummary[] }>(
    `/api/author-tests/sessions/${projectId}`, { params: { take, skip } })
  return data
}

export async function getSessionDetail(id: number): Promise<SessionDetail> {
  const { data } = await api.get<SessionDetail>(`/api/author-tests/sessions/detail/${id}`)
  return data
}

export async function patchDraft(
  id: number, body: { status?: string; title?: string; stepsJson?: string },
): Promise<void> {
  await api.patch(`/api/author-tests/drafts/${id}`, body)
}

export async function approveAllDrafts(sessionId: number): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/approve-all`)
}

export interface RunTesterBody {
  additionalContext?: string
  referenceTestPlanId?: number
  referenceTestSuiteId?: number
  planNavigationHint?: string
}

export async function runTester(sessionId: number, body?: RunTesterBody): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/run-tester`, body ?? {})
}

// Re-run the Analyst with feedback (improve/fill gaps in the existing analysis).
export async function rerunAnalyst(sessionId: number, feedback: string): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/rerun-analyst`, { feedback })
}

export interface TestFeedbackItem { draftId: number; feedback?: string; remove?: boolean }

// Feedback-driven Tester refine (merge into existing drafts).
export async function refineTester(
  sessionId: number, body: { overallFeedback?: string; perTest: TestFeedbackItem[] },
): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/refine-tester`, body)
}

export async function cancelSession(sessionId: number): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/cancel`)
}

export async function pushSession(
  sessionId: number, body: { testPlanId: number; testSuiteId: number },
): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/push`, body)
}

// Push a single draft to ADO (synchronous — backs the per-row "Push" button).
export async function pushDraft(
  draftId: number, body: { testPlanId: number; testSuiteId: number },
): Promise<void> {
  await api.post(`/api/author-tests/drafts/${draftId}/push`, body)
}

export async function saveAnalysis(sessionId: number, analysis: unknown): Promise<void> {
  await api.patch(`/api/author-tests/sessions/${sessionId}/analysis`, analysis)
}

export const sessionStreamUrl = (id: number) => apiUrl(`/api/author-tests/sessions/${id}/stream`)

export interface RescanPlanResponse { scanId: number; caseCount: number; scannedAt: string }
export interface ScanStatusResponse { scanId: number | null; caseCount: number | null; scannedAt: string | null }

export async function rescanPlan(planId: number, suiteId?: number): Promise<RescanPlanResponse> {
  const { data } = await api.post<RescanPlanResponse>(
    `/api/author-tests/plans/${planId}/rescan`,
    null,
    { params: suiteId !== undefined ? { suiteId } : {} },
  )
  return data
}

export async function getScanStatus(planId: number, suiteId?: number): Promise<ScanStatusResponse> {
  const { data } = await api.get<ScanStatusResponse>(
    `/api/author-tests/plans/${planId}/scan-status`,
    { params: suiteId !== undefined ? { suiteId } : {} },
  )
  return data
}
