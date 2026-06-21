import { api } from './client'

// Author-tests feature API. Seeded here with the Test Plan / Suite reads that Settings consumes;
// expanded with the session-config call sites in a later F1 step.
export interface TestPlan { id: number; name: string }
export interface TestSuite { id: number; name: string }

// Dashboard-shaped session row, shared by the author-tests + find-bugs session lists (api/findBugs
// re-uses this shape). The full session-config detail type is added when those pages migrate.
export interface SessionSummary {
  id: number; workItemId: number; workItemTitle: string; status: string; createdAt: string
}

export async function listSessions(projectId: number, take = 50): Promise<SessionSummary[]> {
  const { data } = await api.get<{ items: SessionSummary[] }>(
    `/api/author-tests/sessions/${projectId}`, { params: { take } })
  return data.items ?? []
}

export async function getPlans(): Promise<TestPlan[]> {
  const { data } = await api.get<TestPlan[]>('/api/author-tests/plans')
  return data
}

export async function getSuites(planId: number): Promise<TestSuite[]> {
  const { data } = await api.get<TestSuite[]>(`/api/author-tests/plans/${planId}/suites`)
  return data
}
