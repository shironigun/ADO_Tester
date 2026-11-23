import { api } from './client'

// Settings config is a flat string/bool map keyed by dotted setting names (see SettingsPage `K`).
// Secret values are never echoed back; the backend instead returns a companion `<key>.set` boolean.
export type Config = Record<string, string | boolean>

interface AdoProject { id: string; name: string }

export interface ProviderActiveDetail {
  model?: string | null
  effort?: string | null
  context?: string | null
}

export interface ProviderInfo {
  current: string
  available: string[]
  active?: ProviderActiveDetail | null
}

export async function getProvider(): Promise<ProviderInfo> {
  const { data } = await api.get<ProviderInfo>('/api/settings/provider')
  return data
}

export async function setProvider(provider: string): Promise<void> {
  await api.put('/api/settings/provider', { provider })
}

export async function getConfig(): Promise<Config> {
  const { data } = await api.get<Config>('/api/settings/config')
  return data
}

export async function saveConfig(values: Record<string, string>): Promise<Config> {
  const { data } = await api.put<Config>('/api/settings/config', { values })
  return data
}

export async function getClaudeCliModels(): Promise<string[]> {
  const { data } = await api.get<{ suggestions: string[] }>('/api/settings/claude-cli/models')
  return data.suggestions ?? []
}

export async function getCopilotCliModels(): Promise<string[]> {
  const { data } = await api.get<{ suggestions: string[] }>('/api/settings/copilot-cli/models')
  return data.suggestions ?? []
}

// A selectable label/value option (effort level or context size), from a CLI catalog.
export interface CopilotOption { label: string; value: string }
// One catalog model with selectable reasoning-effort levels and context-window sizes.
export interface CopilotCatalogModel { id: string; label: string; efforts: CopilotOption[]; contexts: CopilotOption[] }

export async function getClaudeCliCatalog(): Promise<CopilotCatalogModel[]> {
  const { data } = await api.get<{ models: CopilotCatalogModel[] }>('/api/settings/claude-cli/catalog')
  return data.models ?? []
}

export async function getCopilotCliCatalog(): Promise<CopilotCatalogModel[]> {
  const { data } = await api.get<{ models: CopilotCatalogModel[] }>('/api/settings/copilot-cli/catalog')
  return data.models ?? []
}

export async function getCursorCliCatalog(): Promise<CopilotCatalogModel[]> {
  const { data } = await api.get<{ models: CopilotCatalogModel[] }>('/api/settings/cursor-cli/catalog')
  return data.models ?? []
}

export async function getAdoOrgs(): Promise<string[]> {
  const { data } = await api.get<{ organizations: string[] }>('/api/settings/ado/orgs')
  return data.organizations ?? []
}

export async function getAdoProjects(organization: string): Promise<AdoProject[]> {
  const { data } = await api.get<{ projects: AdoProject[] }>('/api/settings/ado/projects',
    { params: { organization } })
  return data.projects ?? []
}

// Both args optional: when omitted the backend falls back to the saved ado.organization / ado.project
// config (the Reports page calls this with no params to discover boards for the saved connection).
export async function getAdoAreaPaths(organization?: string, project?: string): Promise<string[]> {
  const params: Record<string, string> = {}
  if (organization) params.organization = organization
  if (project) params.project = project
  const { data } = await api.get<{ areaPaths: string[] }>('/api/settings/ado/area-paths', { params })
  return data.areaPaths ?? []
}

// Both args optional: when omitted the backend falls back to the saved ado.organization / ado.project.
// Drives the Reports "Ticket types" picker from the project's real process work-item types.
export async function getAdoWorkItemTypes(organization?: string, project?: string): Promise<string[]> {
  const params: Record<string, string> = {}
  if (organization) params.organization = organization
  if (project) params.project = project
  const { data } = await api.get<{ workItemTypes: string[] }>('/api/settings/ado/work-item-types', { params })
  return data.workItemTypes ?? []
}

export async function getAdoRepositories(): Promise<string[]> {
  const { data } = await api.get<{ repositories: string[] }>('/api/settings/ado/repositories')
  return data.repositories ?? []
}

// Optional repository arg: when omitted the backend falls back to the saved ado.repository.
export async function getAdoBranches(repository?: string): Promise<string[]> {
  const params: Record<string, string> = {}
  if (repository) params.repository = repository
  const { data } = await api.get<{ branches: string[] }>('/api/settings/ado/branches', { params })
  return data.branches ?? []
}

// Test plans / suites — for the QA Loop module-analysis searchable dropdowns.
export interface AdoTestPlan { id: number; name: string }
export interface AdoTestSuite { id: number; name: string; parentId?: number | null }

export async function getAdoTestPlans(): Promise<AdoTestPlan[]> {
  const { data } = await api.get<{ plans: AdoTestPlan[] }>('/api/settings/ado/test-plans')
  return data.plans ?? []
}

export async function getAdoTestSuites(planId: number): Promise<AdoTestSuite[]> {
  const { data } = await api.get<{ suites: AdoTestSuite[] }>('/api/settings/ado/test-suites',
    { params: { planId } })
  return data.suites ?? []
}

export type { AdoProject }
