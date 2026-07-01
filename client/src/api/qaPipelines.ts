import { api } from './client'

// ── QA Loop Pipelines API — /api/qaloop/pipelines ────────────────────────────

// Matches PipelineWatchDto.cs exactly.
export interface PipelineWatchDto {
  id: number
  projectId: number
  adoPipelineId: number
  adoPipelineName: string
  module: string
  lastSeenBuildId: number | null
  lastPollAt: string | null
  lastPollError: string | null
  enabled: boolean
  createdAt: string
  updatedAt: string
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

export async function listWatches(projectId: number): Promise<{ total: number; items: PipelineWatchDto[] }> {
  const { data } = await api.get<{ total: number; items: PipelineWatchDto[] }>(`/api/qaloop/pipelines/${projectId}`)
  return data
}

export async function addWatch(projectId: number, req: AddWatchRequest): Promise<PipelineWatchDto> {
  const { data } = await api.post<PipelineWatchDto>(`/api/qaloop/pipelines/${projectId}`, req)
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
