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

export async function startSession(
  projectId: number, body: { workItemId: number; autoHandoff: boolean },
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

export async function runTester(sessionId: number): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/run-tester`)
}

export async function cancelSession(sessionId: number): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/cancel`)
}

export async function pushSession(
  sessionId: number, body: { testPlanId: number; testSuiteId: number },
): Promise<void> {
  await api.post(`/api/author-tests/sessions/${sessionId}/push`, body)
}

export async function saveAnalysis(sessionId: number, analysis: unknown): Promise<void> {
  await api.patch(`/api/author-tests/sessions/${sessionId}/analysis`, analysis)
}

export const sessionStreamUrl = (id: number) => apiUrl(`/api/author-tests/sessions/${id}/stream`)
