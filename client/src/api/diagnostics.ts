import { api } from './client'

export interface EnvStatus {
  activeProvider: string
  adoPatPresent: boolean
  anthropicKeyPresent: boolean
  azureOpenAiConfigured: boolean
  claudeCliAvailable: boolean
  claudeCliVersion: string | null
}

export interface WorkItem {
  id: number; title: string; workItemType: string; state: string; assignedTo?: string
}

export async function getEnvStatus(): Promise<EnvStatus> {
  const { data } = await api.get<EnvStatus>('/api/diagnostics')
  return data
}

export async function getWorkItem(id: number | string): Promise<WorkItem> {
  const { data } = await api.get<WorkItem>(`/api/diagnostics/workitem/${id}`)
  return data
}

export async function pingLlm(): Promise<{ provider: string; reply: string }> {
  const { data } = await api.get<{ provider: string; reply: string }>('/api/diagnostics/llm')
  return data
}
