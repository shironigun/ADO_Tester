// Pure, dependency-free layered layout + bezier edge routing for one module's ERD.
// No React/MUI imports — just geometry, so it stays testable and cheap to memoize.

import type { SchemaGraph } from '../../api/schema'
import { NEUTRAL_COLOR } from './palette'

// ── Geometry constants ───────────────────────────────────────────────────────
export const NODE_H = 42          // tall enough for a color chip + name + "N cols"
export const ROW_GAP = 60         // vertical pitch between node tops in a column
export const COL_GAP = 96         // gap between a column's widest node and the next
export const MARGIN = 28

export function nodeWidth(name: string): number {
  return Math.max(104, Math.min(210, name.length * 7.2 + 46))
}

// ── Output types ──────────────────────────────────────────────────────────────
export interface LaidNode {
  id: number
  name: string
  colCount: number
  serviceId: number | null
  isKeyless: boolean
  isExternal: boolean            // owned by a different module than the selected one
  color: string
  x: number; y: number; w: number; h: number
}

export interface LaidEdge {
  id: number
  sourceId: number
  targetId: number
  sourceColumn: string | null
  dashed: boolean                // dashed = kind 'Inferred'
  path: string
  title: string
}

export interface Layout {
  nodes: LaidNode[]
  edges: LaidEdge[]
  standalone: number[]           // own table ids with no incident relation
  width: number
  height: number
}

// ── Edge routing: cubic bezier, source edge → target opposite edge ─────────────
function edgePath(s: LaidNode, t: LaidNode): string {
  const sy = s.y + s.h / 2
  const ty = t.y + t.h / 2
  let sx: number, tx: number, dirOut: number, dirIn: number
  if (t.x >= s.x + s.w) {          // target to the right
    sx = s.x + s.w; tx = t.x; dirOut = 1; dirIn = 1
  } else if (s.x >= t.x + t.w) {   // target to the left (common FK case)
    sx = s.x; tx = t.x + t.w; dirOut = -1; dirIn = -1
  } else {                          // overlapping x → bow out to the right
    sx = s.x + s.w; tx = t.x + t.w; dirOut = 1; dirIn = 1
  }
  const c = Math.max(36, Math.min(130, Math.abs(tx - sx) * 0.5))
  return `M ${sx} ${sy} C ${sx + dirOut * c} ${sy}, ${tx - dirIn * c} ${ty}, ${tx} ${ty}`
}

/**
 * Lay out the subgraph for `selectedModuleId`: the module's own tables plus any
 * foreign endpoints its relations touch (rendered as external nodes). Left-to-right
 * layered flow — foundational (most-referenced) tables on the left.
 */
export function computeLayout(
  g: SchemaGraph,
  selectedModuleId: number | null,
  serviceColor: Map<number, string>,
): Layout {
  const empty: Layout = { nodes: [], edges: [], standalone: [], width: 0, height: 0 }
  if (!g || selectedModuleId == null) return empty

  const tableById = new Map(g.tables.map(t => [t.id, t]))
  const ownIds = new Set(g.tables.filter(t => t.serviceId === selectedModuleId).map(t => t.id))
  if (ownIds.size === 0) return empty

  // Relations touching at least one own table, de-duped by source-target-column.
  const seen = new Set<string>()
  const relevant = g.relations.filter(r => {
    if (!ownIds.has(r.sourceTableId) && !ownIds.has(r.targetTableId)) return false
    if (r.sourceTableId === r.targetTableId) return false      // skip self-loops
    const key = `${r.sourceTableId}-${r.targetTableId}-${r.sourceColumn ?? ''}`
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })

  // Node set = own ids ∪ every endpoint of a relevant edge.
  const nodeIds = new Set<number>(ownIds)
  for (const r of relevant) { nodeIds.add(r.sourceTableId); nodeIds.add(r.targetTableId) }

  // Tables with at least one incident relevant edge participate in the diagram;
  // own tables with none go to the standalone grid.
  const incident = new Set<number>()
  for (const r of relevant) { incident.add(r.sourceTableId); incident.add(r.targetTableId) }
  const standalone = [...ownIds].filter(id => !incident.has(id))

  const diagramIds = [...nodeIds].filter(id => incident.has(id))
  if (diagramIds.length === 0) return { ...empty, standalone: [...ownIds] }

  // Adjacency for layering (source → target = source references target).
  const outEdges = new Map<number, number[]>()
  const inDegree = new Map<number, number>()
  for (const id of diagramIds) { outEdges.set(id, []); inDegree.set(id, 0) }
  for (const r of relevant) {
    if (!outEdges.has(r.sourceTableId) || !inDegree.has(r.targetTableId)) continue
    outEdges.get(r.sourceTableId)!.push(r.targetTableId)
    inDegree.set(r.targetTableId, (inDegree.get(r.targetTableId) ?? 0) + 1)
  }

  // ── Layering: cycle-safe memoized DFS. layer 0 = no outgoing FK (foundational). ──
  const layerOf = new Map<number, number>()
  const visiting = new Set<number>()
  const computeLayerFor = (id: number): number => {
    const cached = layerOf.get(id)
    if (cached != null) return cached
    if (visiting.has(id)) return 0                     // back-edge in a cycle
    visiting.add(id)
    let best = 0
    for (const t of outEdges.get(id) ?? []) {
      if (t === id || visiting.has(t)) continue        // skip self / back-edges
      best = Math.max(best, 1 + computeLayerFor(t))
    }
    visiting.delete(id)
    layerOf.set(id, best)
    return best
  }
  for (const id of diagramIds) computeLayerFor(id)

  // Bucket ids into layers.
  const maxLayer = Math.max(...diagramIds.map(id => layerOf.get(id) ?? 0))
  const layers: number[][] = Array.from({ length: maxLayer + 1 }, () => [])
  for (const id of diagramIds) layers[layerOf.get(id) ?? 0].push(id)

  const nameOf = (id: number) => tableById.get(id)?.name ?? ''

  // ── Row order: layer 0 by in-degree desc; later layers by neighbor barycenter. ──
  const rowIndex = new Map<number, number>()
  const assignRows = (ids: number[]) => ids.forEach((id, i) => rowIndex.set(id, i))

  layers[0].sort((a, b) =>
    (inDegree.get(b)! - inDegree.get(a)!) || nameOf(a).localeCompare(nameOf(b)))
  assignRows(layers[0])

  // Precompute reverse adjacency for barycenter (sources referencing each node).
  const inEdges = new Map<number, number[]>()
  for (const id of diagramIds) inEdges.set(id, [])
  for (const r of relevant) {
    if (inEdges.has(r.targetTableId) && outEdges.has(r.sourceTableId)) {
      inEdges.get(r.targetTableId)!.push(r.sourceTableId)
    }
  }

  for (let L = 1; L <= maxLayer; L++) {
    const bary = new Map<number, number>()
    for (const id of layers[L]) {
      const neighbors = [...(outEdges.get(id) ?? []), ...(inEdges.get(id) ?? [])]
        .map(n => rowIndex.get(n)).filter((v): v is number => v != null)
      bary.set(id, neighbors.length ? neighbors.reduce((a, b) => a + b, 0) / neighbors.length : Number.POSITIVE_INFINITY)
    }
    layers[L].sort((a, b) => (bary.get(a)! - bary.get(b)!) || nameOf(a).localeCompare(nameOf(b)))
    assignRows(layers[L])
  }

  // ── Coordinates: columns by cumulative max width; shorter columns centered. ──
  const colWidth = layers.map(ids => Math.max(...ids.map(id => nodeWidth(nameOf(id))), 0))
  const colX: number[] = []
  colX[0] = MARGIN
  for (let L = 1; L <= maxLayer; L++) colX[L] = colX[L - 1] + colWidth[L - 1] + COL_GAP
  const maxRows = Math.max(...layers.map(l => l.length), 1)

  const nodes: LaidNode[] = []
  for (let L = 0; L <= maxLayer; L++) {
    const topPad = ((maxRows - layers[L].length) * ROW_GAP) / 2
    layers[L].forEach((id, r) => {
      const t = tableById.get(id)!
      const isExternal = t.serviceId !== selectedModuleId
      const color = t.serviceId != null ? (serviceColor.get(t.serviceId) ?? NEUTRAL_COLOR) : NEUTRAL_COLOR
      nodes.push({
        id: t.id, name: t.name, colCount: t.columns.length, serviceId: t.serviceId,
        isKeyless: t.isKeyless, isExternal, color,
        x: colX[L], y: MARGIN + topPad + r * ROW_GAP, w: nodeWidth(t.name), h: NODE_H,
      })
    })
  }
  const nodeById = new Map(nodes.map(n => [n.id, n]))

  const edges: LaidEdge[] = []
  for (const r of relevant) {
    const s = nodeById.get(r.sourceTableId), t = nodeById.get(r.targetTableId)
    if (!s || !t) continue
    edges.push({
      id: r.id, sourceId: s.id, targetId: t.id, sourceColumn: r.sourceColumn,
      dashed: r.kind === 'Inferred',
      path: edgePath(s, t),
      title: `${s.name} → ${t.name}${r.sourceColumn ? ` (via ${r.sourceColumn})` : ''} · ${r.kind}`,
    })
  }

  const width = colX[maxLayer] + colWidth[maxLayer] + MARGIN
  const height = MARGIN * 2 + maxRows * ROW_GAP
  return { nodes, edges, standalone, width, height }
}
