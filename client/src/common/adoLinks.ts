import { useQuery } from '@tanstack/react-query'
import { getConfig } from '../api/settings'

// Build "open in Azure DevOps" work-item URLs from the *configured* org/project (Settings →
// ado.organization / ado.project) instead of hardcoded constants, so the links stay correct when the
// workspace is pointed at a different ADO org/project. The config is read via React Query and shared
// by queryKey, so calling this hook from several components issues a single cached fetch.
export function useAdoWorkItemUrl() {
  const { data } = useQuery({ queryKey: ['settings-config'], queryFn: getConfig, staleTime: 60_000 })
  const org = String(data?.['ado.organization'] ?? '')
  const project = String(data?.['ado.project'] ?? '')
  return (id: number | string): string =>
    org && project
      ? `https://dev.azure.com/${encodeURIComponent(org)}/${encodeURIComponent(project)}/_workitems/edit/${id}`
      : '#'
}
