import { Box, Stack, Typography } from '@mui/material'
import { motion } from 'framer-motion'

export interface DonutSegment { label: string; value: number; color: string }

/**
 * Hand-rolled inline-SVG donut (no charting lib — matches the report ethos).
 * Segments animate their stroke in on mount. Center shows the total.
 */
export default function MiniDonut({
  segments, size = 132, thickness = 14, centerLabel = 'total',
}: { segments: DonutSegment[]; size?: number; thickness?: number; centerLabel?: string }) {
  const total = segments.reduce((s, x) => s + x.value, 0)
  const r = (size - thickness) / 2
  const c = 2 * Math.PI * r
  let offset = 0

  return (
    <Stack direction="row" spacing={2.5} sx={{ alignItems: 'center' }}>
      <Box sx={{ position: 'relative', width: size, height: size, flexShrink: 0 }}>
        <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
          <g transform={`rotate(-90 ${size / 2} ${size / 2})`}>
            <circle cx={size / 2} cy={size / 2} r={r} fill="none"
              stroke="currentColor" strokeOpacity={0.1} strokeWidth={thickness} />
            {total > 0 && segments.map((s, i) => {
              const frac = s.value / total
              const dash = frac * c
              const seg = (
                <motion.circle
                  key={i}
                  cx={size / 2} cy={size / 2} r={r} fill="none"
                  stroke={s.color} strokeWidth={thickness} strokeLinecap="round"
                  strokeDasharray={`${dash} ${c - dash}`}
                  initial={{ strokeDashoffset: c }}
                  animate={{ strokeDashoffset: -offset }}
                  transition={{ duration: 0.9, ease: [0.22, 1, 0.36, 1], delay: 0.1 + i * 0.08 }}
                />
              )
              offset += dash
              return seg
            })}
          </g>
        </svg>
        <Box sx={{
          position: 'absolute', inset: 0, display: 'flex',
          flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
        }}>
          <Typography variant="h5" sx={{ lineHeight: 1 }}>{total}</Typography>
          <Typography variant="caption" color="text.secondary">{centerLabel}</Typography>
        </Box>
      </Box>
      <Stack spacing={0.75}>
        {segments.map((s, i) => (
          <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Box sx={{ width: 10, height: 10, borderRadius: '3px', bgcolor: s.color, flexShrink: 0 }} />
            <Typography variant="body2" sx={{ fontWeight: 600, minWidth: 22 }}>{s.value}</Typography>
            <Typography variant="body2" color="text.secondary">{s.label}</Typography>
          </Stack>
        ))}
      </Stack>
    </Stack>
  )
}
