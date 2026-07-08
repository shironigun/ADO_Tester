import { useMemo, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Autocomplete, Box, Chip, CircularProgress,
  Paper, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import KeyIcon from '@mui/icons-material/KeyOutlined'
import { useQuery } from '@tanstack/react-query'
import { getScanGraph, type SchemaTableNode } from '../api/schema'

// Distinct-enough palette; services map to colors by their order in the graph.
const PALETTE = [
  '#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6',
  '#ec4899', '#14b8a6', '#f97316', '#22c55e', '#3b82f6', '#a855f7',
]
const NEIGHBOR_CAP = 40

// SVG canvas (scales to container width via viewBox).
const W = 960, H = 500, CX = W / 2, CY = H / 2, RX = 400, RY = 205, SHORTEN = 26

interface GNode { id: number; name: string; x: number; y: number; w: number; color: string; keyless: boolean; isCenter: boolean }
interface GEdge { id: number; sx: number; sy: number; tx: number; ty: number; dashed: boolean; title: string }

function nodeWidth(name: string) { return Math.max(64, Math.min(184, name.length * 7 + 22)) }
function shorten(fromX: number, fromY: number, toX: number, toY: number, by: number) {
  const dx = toX - fromX, dy = toY - fromY, len = Math.hypot(dx, dy) || 1
  return { x: toX - (dx / len) * by, y: toY - (dy / len) * by }
}

export default function SchemaGraphView({ scanId }: { scanId: number }) {
  const { data: g, isLoading } = useQuery({ queryKey: ['schema-graph', scanId], queryFn: () => getScanGraph(scanId) })
  const [selectedId, setSelectedId] = useState<number | null>(null)

  const tableById = useMemo(() => new Map((g?.tables ?? []).map(t => [t.id, t])), [g])
  const serviceName = useMemo(() => new Map((g?.services ?? []).map(s => [s.id, s.name])), [g])
  const serviceColor = useMemo(() => {
    const m = new Map<number, string>()
    ;(g?.services ?? []).forEach((s, i) => m.set(s.id, PALETTE[i % PALETTE.length]))
    return m
  }, [g])
  const colorOf = (t: SchemaTableNode | undefined) =>
    t?.serviceId != null ? (serviceColor.get(t.serviceId) ?? '#64748b') : '#64748b'

  // Focus defaults to Customer, else the most-connected table.
  const effectiveId = useMemo(() => {
    if (selectedId != null && tableById.has(selectedId)) return selectedId
    if (!g || g.tables.length === 0) return null
    const customer = g.tables.find(t => t.name === 'Customer')
    if (customer) return customer.id
    const deg = new Map<number, number>()
    for (const r of g.relations) {
      deg.set(r.sourceTableId, (deg.get(r.sourceTableId) ?? 0) + 1)
      deg.set(r.targetTableId, (deg.get(r.targetTableId) ?? 0) + 1)
    }
    return [...g.tables].sort((a, b) => (deg.get(b.id) ?? 0) - (deg.get(a.id) ?? 0))[0].id
  }, [g, selectedId, tableById])

  const { nodes, edges, moreCount } = useMemo(() => {
    if (!g || effectiveId == null) return { nodes: [] as GNode[], edges: [] as GEdge[], moreCount: 0 }
    const center = tableById.get(effectiveId)
    if (!center) return { nodes: [] as GNode[], edges: [] as GEdge[], moreCount: 0 }

    const incident = g.relations.filter(r => r.sourceTableId === effectiveId || r.targetTableId === effectiveId)
    const neighborIds: number[] = []
    for (const r of incident) {
      const other: number = r.sourceTableId === effectiveId ? r.targetTableId : r.sourceTableId
      if (other !== effectiveId && !neighborIds.includes(other)) neighborIds.push(other)
    }
    const shown = neighborIds.slice(0, NEIGHBOR_CAP)

    const pos = new Map<number, { x: number; y: number }>()
    pos.set(effectiveId, { x: CX, y: CY })
    const nodes: GNode[] = [{ id: center.id, name: center.name, x: CX, y: CY, w: nodeWidth(center.name), color: colorOf(center), keyless: center.isKeyless, isCenter: true }]
    shown.forEach((id, i) => {
      const t = tableById.get(id); if (!t) return
      const a = -Math.PI / 2 + (2 * Math.PI * i) / shown.length
      const x = Math.round(CX + RX * Math.cos(a)), y = Math.round(CY + RY * Math.sin(a))
      pos.set(id, { x, y })
      nodes.push({ id: t.id, name: t.name, x, y, w: nodeWidth(t.name), color: colorOf(t), keyless: t.isKeyless, isCenter: false })
    })

    const shownSet = new Set(shown)
    const seen = new Set<string>()
    const edges: GEdge[] = []
    for (const r of incident) {
      const s = r.sourceTableId, t = r.targetTableId
      if (!(s === effectiveId || shownSet.has(s)) || !(t === effectiveId || shownSet.has(t))) continue
      const key = `${s}-${t}-${r.sourceColumn}`
      if (seen.has(key)) continue
      seen.add(key)
      const sp = pos.get(s)!, tp = pos.get(t)!
      const a = shorten(tp.x, tp.y, sp.x, sp.y, SHORTEN)   // trimmed source end
      const b = shorten(sp.x, sp.y, tp.x, tp.y, SHORTEN)   // trimmed target end (arrow)
      edges.push({
        id: r.id, sx: a.x, sy: a.y, tx: b.x, ty: b.y,
        dashed: r.kind === 'Inferred',
        title: `${tableById.get(s)?.name} → ${tableById.get(t)?.name}${r.sourceColumn ? ` (via ${r.sourceColumn})` : ''} · ${r.kind}`,
      })
    }
    return { nodes, edges, moreCount: neighborIds.length - shown.length }
  }, [g, effectiveId, tableById]) // eslint-disable-line react-hooks/exhaustive-deps

  const center = effectiveId != null ? tableById.get(effectiveId) : undefined

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>
  if (!g || g.tables.length === 0) return null

  const tablesByService = new Map<number, SchemaTableNode[]>()
  for (const t of g.tables) {
    if (t.serviceId == null) continue
    const arr = tablesByService.get(t.serviceId) ?? []
    arr.push(t); tablesByService.set(t.serviceId, arr)
  }

  return (
    <Stack spacing={1.5}>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1, alignItems: 'center' }}>
        <Chip size="small" color="primary" label={`${g.services.length} services`} />
        <Chip size="small" color="info" label={`${g.tables.length} tables`} />
        <Chip size="small" color="secondary" label={`${g.relations.length} relations`} />
        <Autocomplete
          size="small" sx={{ minWidth: 280, ml: 'auto' }}
          options={[...g.tables].sort((a, b) => a.name.localeCompare(b.name))}
          getOptionLabel={t => `${t.name}${t.serviceId != null ? ` · ${serviceName.get(t.serviceId) ?? ''}` : ''}`}
          value={center ?? null}
          onChange={(_, t) => setSelectedId(t ? t.id : null)}
          renderInput={p => <TextField {...p} label="Focus table" placeholder="Search tables…" />}
        />
      </Stack>

      <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, overflow: 'hidden', bgcolor: 'background.default' }}>
        <svg viewBox={`0 0 ${W} ${H}`} width="100%" style={{ display: 'block', height: 460 }} preserveAspectRatio="xMidYMid meet">
          <defs>
            <marker id="arrow-solid" markerWidth="9" markerHeight="9" refX="7" refY="3" orient="auto" markerUnits="userSpaceOnUse">
              <path d="M0,0 L7,3 L0,6 Z" fill="#475569" />
            </marker>
            <marker id="arrow-dashed" markerWidth="9" markerHeight="9" refX="7" refY="3" orient="auto" markerUnits="userSpaceOnUse">
              <path d="M0,0 L7,3 L0,6 Z" fill="#94a3b8" />
            </marker>
          </defs>
          {edges.map(e => (
            <line key={e.id} x1={e.sx} y1={e.sy} x2={e.tx} y2={e.ty}
              stroke={e.dashed ? '#94a3b8' : '#475569'} strokeWidth={1.4}
              strokeDasharray={e.dashed ? '5 4' : undefined}
              markerEnd={`url(#${e.dashed ? 'arrow-dashed' : 'arrow-solid'})`}>
              <title>{e.title}</title>
            </line>
          ))}
          {nodes.map(n => (
            <g key={n.id} transform={`translate(${n.x - n.w / 2}, ${n.y - 15})`}
              style={{ cursor: n.isCenter ? 'default' : 'pointer' }}
              onClick={() => { if (!n.isCenter) setSelectedId(n.id) }}>
              <rect width={n.w} height={30} rx={7} fill={n.color}
                stroke={n.isCenter ? '#111827' : '#ffffff66'} strokeWidth={n.isCenter ? 3 : 1}
                strokeDasharray={n.keyless ? '4 3' : undefined} opacity={n.keyless ? 0.8 : 1} />
              <text x={n.w / 2} y={19} textAnchor="middle" fontSize={11}
                fontWeight={n.isCenter ? 700 : 500} fill="#fff" style={{ pointerEvents: 'none' }}>
                {n.name.length > n.w / 6.5 ? n.name.slice(0, Math.floor(n.w / 6.5)) + '…' : n.name}
                <title>{n.name}</title>
              </text>
            </g>
          ))}
        </svg>
      </Box>

      <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap', gap: 1, alignItems: 'center' }}>
        <Typography variant="caption" color="text.secondary">
          Click a table to re-center. Solid = declared FK/navigation; dashed = AI-inferred id-column link.
        </Typography>
        {moreCount > 0 && (
          <Chip size="small" color="warning" variant="outlined"
            label={`+${moreCount} more links (search to explore)`} />
        )}
      </Stack>

      {/* Focused table's columns */}
      {center && (
        <Paper variant="outlined" sx={{ p: 1.5 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1, flexWrap: 'wrap', gap: 0.5 }}>
            <Box sx={{ width: 12, height: 12, borderRadius: '50%', bgcolor: colorOf(center) }} />
            <Typography sx={{ fontWeight: 700, fontFamily: 'monospace' }}>{center.name}</Typography>
            {center.serviceId != null && <Chip size="small" variant="outlined" label={serviceName.get(center.serviceId)} />}
            {center.sqlSchema && <Chip size="small" variant="outlined" label={`schema ${center.sqlSchema}`} />}
            {center.isKeyless && <Chip size="small" color="warning" variant="outlined" label="keyless" />}
            <Chip size="small" variant="outlined" label={`${center.columns.length} columns`} />
          </Stack>
          {center.columns.length > 0 && (
            <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', gap: 0.75 }}>
              {center.columns.map(c => (
                <Tooltip key={c.name} title={`${c.dataType ?? '?'}${c.isForeignKey && c.referencesTable ? ` → ${c.referencesTable}` : ''}${c.isNullable ? ' · nullable' : ''}`}>
                  <Chip size="small"
                    color={c.isPrimaryKey ? 'primary' : c.isForeignKey ? 'info' : 'default'}
                    variant={c.isPrimaryKey ? 'filled' : 'outlined'}
                    icon={c.isPrimaryKey ? <KeyIcon /> : undefined}
                    label={c.name} sx={{ fontFamily: 'monospace', fontSize: 11.5 }} />
                </Tooltip>
              ))}
            </Stack>
          )}
        </Paper>
      )}

      {/* Full listing */}
      <Accordion disableGutters variant="outlined">
        <AccordionSummary expandIcon={<ExpandMoreIcon />}>
          <Typography sx={{ fontWeight: 600 }}>Browse all tables by service</Typography>
        </AccordionSummary>
        <AccordionDetails>
          <Stack spacing={1}>
            {g.services.map(svc => {
              const tables = (tablesByService.get(svc.id) ?? []).slice().sort((a, b) => a.name.localeCompare(b.name))
              return (
                <Box key={svc.id}>
                  <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                    {svc.name} <Typography component="span" variant="caption" color="text.secondary">· {svc.tableCount} tables</Typography>
                  </Typography>
                  <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
                    {tables.map(t => (
                      <Chip key={t.id} size="small" variant="outlined" clickable onClick={() => setSelectedId(t.id)}
                        label={t.name} sx={{ fontFamily: 'monospace', fontSize: 11 }} />
                    ))}
                  </Stack>
                </Box>
              )
            })}
          </Stack>
        </AccordionDetails>
      </Accordion>
    </Stack>
  )
}
