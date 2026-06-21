import { api } from './client'

// Author-tests feature API. Seeded here with the Test Plan / Suite reads that Settings consumes;
// expanded with the session-config call sites in a later F1 step.
export interface TestPlan { id: number; name: string }
export interface TestSuite { id: number; name: string }

export async function getPlans(): Promise<TestPlan[]> {
  const { data } = await api.get<TestPlan[]>('/api/author-tests/plans')
  return data
}

export async function getSuites(planId: number): Promise<TestSuite[]> {
  const { data } = await api.get<TestSuite[]>(`/api/author-tests/plans/${planId}/suites`)
  return data
}
