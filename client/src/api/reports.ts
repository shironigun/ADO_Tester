import { api } from './client'
import { apiUrl } from '../config/env'

export type Theme = 'Executive' | 'Standup' | 'Branded'

export interface Report {
  id: number
  projectId: number
  fromUtc: string
  toUtc: string
  boards: string
  ticketCount: number
  status: 'Generating' | 'Done' | 'Failed'
  htmlPath: string | null
  errorMessage: string | null
  createdAt: string
  title: string
  trigger: string
  theme: Theme
  eventLogJson?: string | null
}

export interface GenerateReportRequest {
  fromUtc: string
  toUtc: string
  boards: string[]
  theme: Theme
}

export async function listReports(projectId: number): Promise<Report[]> {
  const { data } = await api.get<Report[]>(`/api/reports/${projectId}`)
  return data
}

export async function generateReport(projectId: number, body: GenerateReportRequest): Promise<void> {
  await api.post(`/api/reports/${projectId}/generate`, body)
}

// Direct-URL helpers for the browser (iframe src, download anchor, SSE stream) — these aren't axios
// calls, so they go through apiUrl() to honor VITE_QA_API the same way the api client does.
export const reportHtmlUrl = (id: number) => apiUrl(`/api/reports/html/${id}`)
export const reportDownloadUrl = (id: number) => apiUrl(`/api/reports/download/${id}`)
export const reportStreamUrl = (id: number) => apiUrl(`/api/reports/${id}/stream`)
