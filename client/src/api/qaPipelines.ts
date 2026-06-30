import { api } from './client'

// ── QA Loop Pipelines API — /api/qaloop/pipelines ────────────────────────────

export interface PipelineWatchDto {
  id: number
  adoPipelineId: number
  adoPipelineName: string
  module: string
  enabled: boolean
  lastBuildId: number | null
  lastRunAt: string | null
  createdAt: string
}

export interface AddWatchRequest {
  adoPipelineId: number
  adoPipelineName: string
  module: string
}

export interface UpdateWatchRequest {
  enabled?: boolean
  module?: string
}

// ── Methods ───────────────────────────────────────────────────────────────────

export async function listWatches(): Promise<{ items: PipelineWatchDto[] }> {
  const { data } = await api.get<{ items: PipelineWatchDto[] }>('/api/qaloop/pipelines')
  return data
}

export async function addWatch(req: AddWatchRequest): Promise<PipelineWatchDto> {
  const { data } = await api.post<PipelineWatchDto>('/api/qaloop/pipelines', req)
  return data
}

export async function updateWatch(id: number, req: UpdateWatchRequest): Promise<PipelineWatchDto> {
  const { data } = await api.patch<PipelineWatchDto>(`/api/qaloop/pipelines/${id}`, req)
  return data
}

export async function deleteWatch(id: number): Promise<void> {
  await api.delete(`/api/qaloop/pipelines/${id}`)
}

export async function runLatest(id: number): Promise<{ runId?: number; buildId?: number }> {
  const { data } = await api.post(`/api/qaloop/pipelines/${id}/run-latest`)
  return data
}
