import { api } from './client'
import { apiUrl } from '../config/env'
import type { SessionSummary, SessionDetail } from '../session/types'

// Find-bugs feature API — every call site for the Find Bugs pipeline routes through the centralized
// api client here. Session rows share the canonical SessionSummary shape.
export type { SessionSummary } from '../session/types'

export interface AttachedTestCase {
  key: string; title: string; preconditions: string; source: 'plan' | 'draft'
  adoTestCaseId?: number | null; steps: { action: string; expected: string }[]
}

export type LinkedChange = { kind: 'pr' | 'commit'; id: string; title: string; url: string }
export interface LinkedRefs { pullRequestIds: string[]; commitShas: string[]; changes: LinkedChange[] }

export interface RunBugWriterRequest {
  actualBehavior: string; verifyCode: boolean
  prIds: string[]; commitIds: string[]; selectedTestCases: string[]
}

export interface BugFromVerdictRequest {
  testCase: string; source?: 'plan' | 'draft'; adoTestCaseId?: number
  evidence: string; reasoning: string; failedStepIndex?: number
  steps: { action: string; expected: string; status?: string; actual?: string }[]
}

// Dashboard helper — items only.
export async function listSessions(projectId: number, take = 50): Promise<SessionSummary[]> {
  const { data } = await api.get<{ items: SessionSummary[] }>(
    `/api/find-bugs/sessions/${projectId}`, { params: { take } })
  return data.items ?? []
}

// Paged list (total + items) for the session grid.
export async function listSessionsPage(
  projectId: number, take: number, skip: number,
): Promise<{ total: number; items: SessionSummary[] }> {
  const { data } = await api.get<{ total: number; items: SessionSummary[] }>(
    `/api/find-bugs/sessions/${projectId}`, { params: { take, skip } })
  return data
}

export async function getSessionDetail(id: number): Promise<SessionDetail> {
  const { data } = await api.get<SessionDetail>(`/api/find-bugs/sessions/detail/${id}`)
  return data
}

export async function startSession(
  projectId: number, body: { workItemId: number },
): Promise<{ sessionId: number }> {
  const { data } = await api.post<{ sessionId: number }>(`/api/find-bugs/${projectId}/start`, body)
  return data
}

export async function getLinkedRefs(sessionId: number): Promise<LinkedRefs> {
  const { data } = await api.get<{ pullRequestIds?: string[]; commitShas?: string[]; changes?: LinkedChange[] }>(
    `/api/find-bugs/sessions/${sessionId}/linked-refs`)
  return {
    pullRequestIds: data.pullRequestIds ?? [],
    commitShas: data.commitShas ?? [],
    changes: data.changes ?? [],
  }
}

export async function getTestCases(sessionId: number): Promise<AttachedTestCase[]> {
  const { data } = await api.get<AttachedTestCase[]>(`/api/find-bugs/sessions/${sessionId}/test-cases`)
  return data ?? []
}

export async function runBugWriter(sessionId: number, body: RunBugWriterRequest): Promise<void> {
  await api.post(`/api/find-bugs/sessions/${sessionId}/run`, body)
}

export async function cancelSession(sessionId: number): Promise<void> {
  await api.post(`/api/find-bugs/sessions/${sessionId}/cancel`)
}

export async function patchDraft(id: number, body: Record<string, unknown>): Promise<void> {
  await api.patch(`/api/find-bugs/drafts/${id}`, body)
}

export async function approveAllDrafts(sessionId: number): Promise<void> {
  await api.post(`/api/find-bugs/sessions/${sessionId}/approve-all`)
}

export async function pushSession(sessionId: number): Promise<void> {
  await api.post(`/api/find-bugs/sessions/${sessionId}/push`)
}

export async function bugFromVerdict(sessionId: number, body: BugFromVerdictRequest): Promise<void> {
  await api.post(`/api/find-bugs/sessions/${sessionId}/bug-from-verdict`, body)
}

export async function saveAnalysis(sessionId: number, analysis: unknown): Promise<void> {
  await api.patch(`/api/find-bugs/sessions/${sessionId}/analysis`, analysis)
}

export const sessionStreamUrl = (id: number) => apiUrl(`/api/find-bugs/sessions/${id}/stream`)
