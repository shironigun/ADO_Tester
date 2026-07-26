import { useMemo } from 'react'
import { Box, useTheme } from '@mui/material'
import ErdNode, { type NodeColors } from './ErdNode'
import type { Layout } from './layout'

interface Props {
  layout: Layout
  hoveredId: number | null
  onHover: (id: number | null) => void
  onNodeClick: (id: number) => void
}

// The ERD canvas: <defs> markers, edges (behind), nodes (front). Hover isolation
// is derived here from the hovered id + the layout's edges (cheap, no geometry recompute).
export default function ErdDiagram({ layout, hoveredId, onHover, onNodeClick }: Props) {
  const theme = useTheme()
  const dark = theme.palette.mode === 'dark'

  const nodeColors: NodeColors = {
    bg: dark ? '#1e293b' : '#ffffff',
    text: dark ? '#f1f5f9' : '#0f172a',
    sub: dark ? '#94a3b8' : '#64748b',
  }
  const edgeSolid = dark ? '#64748b' : '#475569'
  const edgeDashed = dark ? '#7c8ba1' : '#94a3b8'

  const { neighborIds, incidentEdgeIds } = useMemo(() => {
    if (hoveredId == null) return { neighborIds: null as Set<number> | null, incidentEdgeIds: null as Set<number> | null }
    const n = new Set<number>([hoveredId])
    const e = new Set<number>()
    for (const edge of layout.edges) {
      if (edge.sourceId === hoveredId || edge.targetId === hoveredId) {
        e.add(edge.id); n.add(edge.sourceId); n.add(edge.targetId)
      }
    }
    return { neighborIds: n, incidentEdgeIds: e }
  }, [hoveredId, layout.edges])

  const { width, height } = layout

  return (
    <Box
      sx={{
        border: 1, borderColor: 'divider', borderRadius: 2, overflow: 'auto',
        maxHeight: 620, bgcolor: 'background.default',
      }}
    >
      <svg
        viewBox={`0 0 ${width} ${height}`}
        width="100%"
        preserveAspectRatio="xMidYMin meet"
        style={{ display: 'block', minWidth: Math.min(width, 520), height }}
        onMouseLeave={() => onHover(null)}
      >
        <defs>
          <marker id="erd-arrow-solid" markerWidth="9" markerHeight="9" refX="7" refY="3"
            orient="auto" markerUnits="userSpaceOnUse">
            <path d="M0,0 L7,3 L0,6 Z" fill={edgeSolid} />
          </marker>
          <marker id="erd-arrow-dashed" markerWidth="9" markerHeight="9" refX="7" refY="3"
            orient="auto" markerUnits="userSpaceOnUse">
            <path d="M0,0 L7,3 L0,6 Z" fill={edgeDashed} />
          </marker>
        </defs>

        {layout.edges.map(e => {
          const active = incidentEdgeIds ? incidentEdgeIds.has(e.id) : true
          const opacity = incidentEdgeIds == null ? 1 : active ? 1 : 0.08
          return (
            <path
              key={e.id} d={e.path} fill="none"
              stroke={e.dashed ? edgeDashed : edgeSolid}
              strokeWidth={active && incidentEdgeIds ? 2.2 : 1.4}
              strokeDasharray={e.dashed ? '5 4' : undefined}
              markerEnd={`url(#${e.dashed ? 'erd-arrow-dashed' : 'erd-arrow-solid'})`}
              style={{ opacity, transition: 'opacity 120ms' }}
            >
              <title>{e.title}</title>
            </path>
          )
        })}

        {layout.nodes.map(n => (
          <ErdNode
            key={n.id}
            node={n}
            colors={nodeColors}
            dim={neighborIds != null && !neighborIds.has(n.id)}
            highlight={neighborIds != null && neighborIds.has(n.id)}
            onHover={onHover}
            onClick={onNodeClick}
          />
        ))}
      </svg>
    </Box>
  )
}
