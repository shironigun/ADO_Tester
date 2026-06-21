import { api } from './client'
import type { SessionSummary } from './authorTests'

// Find-bugs feature API. Seeded with the session list the dashboard consumes; expanded with the
// session-config call sites in a later F1 step. Session rows share the author-tests SessionSummary shape.
export async function listSessions(projectId: number, take = 50): Promise<SessionSummary[]> {
  const { data } = await api.get<{ items: SessionSummary[] }>(
    `/api/find-bugs/sessions/${projectId}`, { params: { take } })
  return data.items ?? []
}
