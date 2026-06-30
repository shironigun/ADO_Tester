import { api } from './client'
import { apiUrl } from '../config/env'

// ── QA Loop Runs API — /api/qaloop/runs ──────────────────────────────────────

export type RunStatus = 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Error' | 'Cancelled'

export interface TestRunSummary {
  id: number
  module: string | null
  profile: string
  status: RunStatus
  passed: number
  failed: number
  skipped: number
  durationMs: number
  triggerSource: string
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  adoTestRunId: number | null
  reportPath: string | null
}

export interface TestFailureDto {
  id: number
  runId: number
  testTitle: string
  adoTestCaseId: number | null
  classification: string
  confidence: string
  reason: string
  bugDraftJson: string | null
  bugId: number | null
}

export interface TestRunDetail extends TestRunSummary {
  levelFilter: string | null
  tagFilter: string | null
  triggerPipelineId: number | null
  triggerBuildId: number | null
  triggerCommitSha: string | null
  impactAnalysisJson: string | null
  errorMessage: string | null
  failures: TestFailureDto[]
}

export interface StartRunRequest {
  moduleFilter?: string
  levelFilter?: string
  tagFilter?: string
  profile: string
  draftIds?: number[]
}

export interface FileBugsRequest {
  failureIds: number[]
  storyId?: number
}

export interface FileBugsResponse {
  filedCount: number
  bugIds: number[]
}

// ── Methods ───────────────────────────────────────────────────────────────────

export async function startRun(req: StartRunRequest): Promise<{ runId: number }> {
  const { data } = await api.post<{ runId: number }>('/api/qaloop/runs/start', req)
  return data
}

export async function listRunsPage(
  take: number, skip: number,
): Promise<{ total: number; items: TestRunSummary[] }> {
  const { data } = await api.get<{ total: number; items: TestRunSummary[] }>(
    '/api/qaloop/runs', { params: { take, skip } })
  return data
}

export async function getRunDetail(id: number): Promise<TestRunDetail> {
  const { data } = await api.get<TestRunDetail>(`/api/qaloop/runs/${id}`)
  return data
}

export async function triage(runId: number): Promise<{ triagedCount: number; realBugCount: number; failures: TestFailureDto[] }> {
  const { data } = await api.post(`/api/qaloop/runs/${runId}/triage`)
  return data
}

export async function fileBugs(runId: number, req: FileBugsRequest): Promise<FileBugsResponse> {
  const { data } = await api.post<FileBugsResponse>(`/api/qaloop/runs/${runId}/file-bugs`, req)
  return data
}

export async function previewImpact(
  changedFiles: string[], commitMessages: string[],
): Promise<{ impactedDraftIds: number[]; reasoning: string; confidence: string }> {
  const { data } = await api.post('/api/qaloop/runs/preview-impact', { changedFiles, commitMessages })
  return data
}

export const runStreamUrl = (id: number) => apiUrl(`/api/qaloop/runs/${id}/stream`)
