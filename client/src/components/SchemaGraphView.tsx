import { useMemo, useState } from 'react'
import {
  Box, Chip, CircularProgress, Stack,
} from '@mui/material'
import { useQuery } from '@tanstack/react-query'
import EmptyState from './EmptyState'
import Section from './Section'
import ModuleSidebar, { type ModuleItem } from './schema/ModuleSidebar'
import ErdDiagram from './schema/ErdDiagram'
import TableDetailDialog from './schema/TableDetailDialog'
import { computeLayout } from './schema/layout'
import { buildServiceColorMap, prettifyModule } from './schema/palette'
import { MONO_FONT } from '../theme/tokens'
import { getScanGraph } from '../api/schema'

export default function SchemaGraphView({ scanId }: { scanId: number }) {
  const { data: g, isLoading } = useQuery({ queryKey: ['schema-graph', scanId], queryFn: () => getScanGraph(scanId) })

  const [selectedModuleId, setSelectedModuleId] = useState<number | null>(null)
  const [hoveredId, setHoveredId] = useState<number | null>(null)
  const [openTableId, setOpenTableId] = useState<number | null>(null)

  const serviceColor = useMemo(() => buildServiceColorMap((g?.services ?? []).map(s => s.id)), [g])
  const serviceName = useMemo(
    () => new Map((g?.services ?? []).map(s => [s.id, prettifyModule(s.name)])), [g])

  const modules: ModuleItem[] = useMemo(() =>
    [...(g?.services ?? [])]
      .sort((a, b) => b.tableCount - a.tableCount)
      .map(s => ({ id: s.id, label: prettifyModule(s.name), count: s.tableCount, color: serviceColor.get(s.id) ?? '#64748b' })),
  [g, serviceColor])

  // Default selection = the Customer domain (most relevant to QAs), else the largest module.
  const defaultModuleId = useMemo(() => {
    if (modules.length === 0) return null
    return (modules.find(m => m.label.toLowerCase() === 'customer') ?? modules[0]).id
  }, [modules])
  const effectiveModuleId = selectedModuleId ?? defaultModuleId

  const layout = useMemo(
    () => (g ? computeLayout(g, effectiveModuleId, serviceColor) : null),
    [g, effectiveModuleId, serviceColor])

  const tableById = useMemo(() => new Map((g?.tables ?? []).map(t => [t.id, t])), [g])

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>
  if (!g || g.tables.length === 0) return null

  const hasDiagram = layout && layout.nodes.length > 0
  const standalone = layout?.standalone ?? []

  return (
    <Stack direction="row" spacing={2} sx={{ alignItems: 'flex-start' }}>
      <ModuleSidebar modules={modules} selectedId={effectiveModuleId} onSelect={setSelectedModuleId} />

      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Stack spacing={1.5}>
          {hasDiagram ? (
            <ErdDiagram
              layout={layout!}
              hoveredId={hoveredId}
              onHover={setHoveredId}
              onNodeClick={setOpenTableId}
            />
          ) : (
            <EmptyState title="No relationships to draw"
              hint="This module's tables have no declared or inferred relationships." />
          )}

          {standalone.length > 0 && (
            <Section title="Standalone tables" dense
              subtitle={`${standalone.length} with no declared relationships`}>
              <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', gap: 0.75 }}>
                {standalone.map(id => {
                  const t = tableById.get(id)
                  if (!t) return null
                  return (
                    <Chip key={id} size="small" variant="outlined" clickable onClick={() => setOpenTableId(id)}
                      label={`${t.name} · ${t.columns.length}`} sx={{ fontFamily: MONO_FONT, fontSize: 11 }} />
                  )
                })}
              </Stack>
            </Section>
          )}
        </Stack>
      </Box>

      <TableDetailDialog
        open={openTableId != null}
        tableId={openTableId}
        graph={g}
        serviceName={serviceName}
        serviceColor={serviceColor}
        onClose={() => setOpenTableId(null)}
        onOpenTable={setOpenTableId}
      />
    </Stack>
  )
}
