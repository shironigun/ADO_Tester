import { api } from './client'

// Batches (Epic/Feature fan-out runs) feature API — children expansion, start, list, detail, cancel.
export interface BatchChild { id: number; title: string; workItemType: string; state?: string | null }
export interface ChildrenExpand {
  parentWorkItemId: number; parentTitle: string; children: BatchChild[]; truncated: boolean
}
export interface BatchChildSummary {
  sessionId: number; workItemId: number; workItemTitle: string; status: string
  currentActivity?: string | null; errorMessage?: string | null; count: number
}
export interface BatchSummary {
  id: number; module: string; parentWorkItemId?: number | null; title: string; mode?: string | null
  createdAt: string; childCount: number; doneCount: number; failedCount: number; runningCount: number; derivedStatus: string
}
export interface BatchDetail {
  id: number; module: string; parentWorkItemId?: number | null; title: string; mode?: string | null
  createdAt: string; updatedAt: string; derivedStatus: string; children: BatchChildSummary[]
}

export interface StartBatchRequest {
  parentWorkItemId: number | null; workItemIds: number[]; mode?: 'auto' | 'manual'
}
export interface StartBatchResult { batchId: number; sessionIds?: number[]; skipped?: number[] }

export async function expandChildren(parentWorkItemId: number, recursive = true): Promise<ChildrenExpand> {
  const { data } = await api.get<ChildrenExpand>('/api/batches/children-expand',
    { params: { parentWorkItemId, recursive } })
  return data
}

export async function startBatch(
  moduleKey: string, projectId: number, body: StartBatchRequest,
): Promise<StartBatchResult> {
  const { data } = await api.post<StartBatchResult>(`/api/batches/${moduleKey}/${projectId}/start`, body)
  return data
}

export async function listBatches(
  projectId: number, take = 100,
): Promise<{ total: number; items: BatchSummary[] }> {
  const { data } = await api.get<{ total: number; items: BatchSummary[] }>(
    `/api/batches/${projectId}`, { params: { take } })
  return data
}

export async function getBatchDetail(batchId: number): Promise<BatchDetail> {
  const { data } = await api.get<BatchDetail>(`/api/batches/detail/${batchId}`)
  return data
}

export async function cancelBatch(batchId: number): Promise<void> {
  await api.post(`/api/batches/${batchId}/cancel`)
}
