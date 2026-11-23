import { memo } from 'react'
import { MONO_FONT } from '../../theme/tokens'
import type { LaidNode } from './layout'

export interface NodeColors {
  bg: string
  text: string
  sub: string
}

interface Props {
  node: LaidNode
  dim: boolean
  highlight: boolean
  colors: NodeColors
  onHover: (id: number | null) => void
  onClick: (id: number) => void
}

// A single ERD table node. Memoized on primitive-ish props so a hover only
// re-renders the few nodes whose dim/highlight flips.
function ErdNode({ node, dim, highlight, colors, onHover, onClick }: Props) {
  const { x, y, w, h, name, colCount, color, isExternal, isKeyless } = node
  const dashed = isExternal || isKeyless
  const label = name.length > w / 6.8 ? name.slice(0, Math.floor(w / 6.8)) + '…' : name

  return (
    <g
      transform={`translate(${x}, ${y})`}
      style={{ cursor: 'pointer', opacity: dim ? 0.22 : 1, transition: 'opacity 120ms' }}
      onMouseEnter={() => onHover(node.id)}
      onClick={() => onClick(node.id)}
    >
      <rect
        width={w} height={h} rx={9}
        fill={colors.bg}
        stroke={color}
        strokeWidth={highlight ? 2.5 : 1.25}
        strokeOpacity={highlight ? 1 : 0.65}
        strokeDasharray={dashed ? '5 3' : undefined}
      />
      {/* schema-color chip on the left edge */}
      <rect x={0} y={0} width={6} height={h} rx={3} fill={color} />
      <text
        x={16} y={18} fontSize={12.5} fontWeight={highlight ? 700 : 600}
        fill={colors.text} style={{ fontFamily: MONO_FONT, pointerEvents: 'none' }}
      >
        {label}
        <title>{name}{isExternal ? ' · external' : ''}{isKeyless ? ' · keyless' : ''}</title>
      </text>
      <text
        x={16} y={33} fontSize={10.5} fill={colors.sub}
        style={{ fontFamily: MONO_FONT, pointerEvents: 'none' }}
      >
        {colCount} cols{isExternal ? ' · external' : ''}
      </text>
    </g>
  )
}

export default memo(ErdNode)
