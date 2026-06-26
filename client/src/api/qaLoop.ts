import { api } from './client'

// Track A "QA Loop" endpoints: KB extraction, spec generation, results ingest/triage.
// One module so the QA Loop page has a single import surface.

// ── KB extraction (POST /api/knowledgebase/{projectId}/extract) ──────────────
export interface KbExtractionRequest {
  module: string
  repository?: string // Azure Repos repo (which repo); null → configured ado.repository
  branch?: string // branch (which branch); null → configured ado.default_branch
  sourceRoot?: string // optional local clone path; when set, reads from disk instead of ADO
  flows: string[]
  sourceDirs?: string[]
  extensions?: string[]
  maxFileChars?: number
  maxBundleChars?: number
}
export interface KbDocResult {
  fileName: string
  kind: string // "flow" | "rules"
  charCount: number
  documentId: number
}
export interface KbExtractionResult {
  module: string
  filesRead: number
  bundleChars: number
  docs: KbDocResult[]
}
export async function extractKb(projectId: number, req: KbExtractionRequest): Promise<KbExtractionResult> {
  const { data } = await api.post<KbExtractionResult>(`/api/knowledgebase/${projectId}/extract`, req)
  return data
}

// ── Spec generation (POST /api/generation/{projectId}/spec) ──────────────────
export interface GenerateSpecRequest {
  module: string
  scenarioTitle: string
  scenarioIntent?: string
  kbFileName?: string
  kbFlowMarkdown?: string
  adoTestCaseId?: number
  createAdoTestCase?: boolean
  testPlanId?: number
  testSuiteId?: number
  storyId?: number
  smoke?: boolean
  outputRoot?: string
  sourceRoot?: string
  styleReferencePath?: string
  write?: boolean
}
export interface GenerateSpecResult {
  testTitle: string
  fileName: string
  filePath?: string
  written: boolean
  charCount: number
  code: string
  adoTestCaseId?: number
  adoTestCaseCreated: boolean
}
export async function generateSpec(projectId: number, req: GenerateSpecRequest): Promise<GenerateSpecResult> {
  const { data } = await api.post<GenerateSpecResult>(`/api/generation/${projectId}/spec`, req)
  return data
}

// ── Module analysis (POST /api/modules/{projectId}/analyze) ──────────────────
// Analyze a whole module: read its source + a plan/suite's existing test cases, discover every flow,
// and map coverage (covered vs gap). Specs are then generated per flow on demand (generateFlowSpec).
export interface ModuleAnalyzeRequest {
  module: string
  repository?: string
  branch?: string
  sourceRoot?: string
  planId: number
  suiteId: number
  recurse?: boolean
  maxFileChars?: number
  maxBundleChars?: number
}
export interface ModuleFlow {
  name: string
  summary: string
  covered: boolean
  coveringTestCaseIds: number[]
  coveringTestCaseTitles: string[]
}
export interface ModuleAnalyzeResult {
  module: string
  existingTestCaseCount: number
  filesRead: number
  flowCount: number
  coveredCount: number
  gapCount: number
  kbDocFileName: string
  flows: ModuleFlow[]
}
export async function analyzeModule(projectId: number, req: ModuleAnalyzeRequest): Promise<ModuleAnalyzeResult> {
  const { data } = await api.post<ModuleAnalyzeResult>(`/api/modules/${projectId}/analyze`, req)
  return data
}

// ── On-demand flow spec (POST /api/modules/{projectId}/flow-spec) ────────────
// Generate a Playwright spec for one analyzed flow: build its detailed KB, then emit the spec.
// Covered flow → tags its existing test case id; gap → creates + tags a new ADO Test Case.
export interface ModuleFlowSpecRequest {
  module: string
  repository?: string
  branch?: string
  sourceRoot?: string
  planId: number
  suiteId: number
  flowName: string
  flowSummary?: string
  coveringTestCaseIds: number[]
  coveringTestCaseTitles?: string[]
  storyId?: number
  smoke?: boolean
  outputRoot?: string
}
export interface ModuleFlowSpecResult {
  flowName: string
  wasGap: boolean
  kbDocFileName: string
  spec: GenerateSpecResult
}
export async function generateFlowSpec(projectId: number, req: ModuleFlowSpecRequest): Promise<ModuleFlowSpecResult> {
  const { data } = await api.post<ModuleFlowSpecResult>(`/api/modules/${projectId}/flow-spec`, req)
  return data
}

// ── Results ingest + triage (POST /api/results/{projectId}/ingest) ───────────
export interface IngestResultsRequest {
  junitXml?: string
  junitPath?: string
  artifactsRoot?: string
  kbFileName?: string
  storyId?: number
  fileBugs?: boolean
}
export interface FailureOutcome {
  testTitle: string
  adoTestCaseId?: number
  classification: string // real_bug | flaky | test_issue | stale_test
  confidence: string
  reason: string
  filed: boolean
  bugId?: number
  bugTitle: string
  severity: string
  preconditions: string
  reproSteps: string[]
  expected: string
  observations: string[]
  artifacts: string[]
}
export interface IngestResultsResult {
  totalTests: number
  failures: number
  bugsFiled: number
  outcomes: FailureOutcome[]
}
export async function ingestResults(projectId: number, req: IngestResultsRequest): Promise<IngestResultsResult> {
  const { data } = await api.post<IngestResultsResult>(`/api/results/${projectId}/ingest`, req)
  return data
}

// Org-level ADO work-item deep link (org redirects to the right project).
export const adoWorkItemUrl = (id: number) =>
  `https://dev.azure.com/ConstellationDealer/_workitems/edit/${id}`
