import { api } from './client'

// Settings config is a flat string/bool map keyed by dotted setting names (see SettingsPage `K`).
// Secret values are never echoed back; the backend instead returns a companion `<key>.set` boolean.
export type Config = Record<string, string | boolean>

interface AdoProject { id: string; name: string }

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

export async function getAdoOrgs(): Promise<string[]> {
  const { data } = await api.get<{ organizations: string[] }>('/api/settings/ado/orgs')
  return data.organizations ?? []
}

export async function getAdoProjects(organization: string): Promise<AdoProject[]> {
  const { data } = await api.get<{ projects: AdoProject[] }>('/api/settings/ado/projects',
    { params: { organization } })
  return data.projects ?? []
}

export async function getAdoAreaPaths(organization: string, project: string): Promise<string[]> {
  const { data } = await api.get<{ areaPaths: string[] }>('/api/settings/ado/area-paths',
    { params: { organization, project } })
  return data.areaPaths ?? []
}

export type { AdoProject }
