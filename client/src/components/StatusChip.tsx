import { Chip, type ChipProps } from '@mui/material'
import { motion } from 'framer-motion'
import { statusColors, draftColors, sevColors, IN_FLIGHT, type ChipColor } from './pipeline'

type Kind = 'status' | 'draft' | 'severity'

const MAPS: Record<Kind, Record<string, ChipColor>> = {
  status: statusColors,
  draft: draftColors,
  severity: sevColors,
}

/**
 * Themed status chip. Centralizes the colour maps that used to be copy-pasted
 * across pages. In-flight statuses (agent running) get a soft infinite pulse.
 */
export default function StatusChip({
  value, kind = 'status', ...rest
}: { value: string; kind?: Kind } & Omit<ChipProps, 'label' | 'color'>) {
  const color = MAPS[kind][value] ?? 'default'
  const pulsing = kind === 'status' && IN_FLIGHT.includes(value)

  const chip = (
    <Chip
      label={value}
      size="small"
      color={color}
      variant={color === 'default' ? 'outlined' : 'filled'}
      {...rest}
    />
  )

  if (!pulsing) return chip
  return (
    <motion.span
      style={{ display: 'inline-flex' }}
      animate={{ opacity: [0.65, 1, 0.65] }}
      transition={{ repeat: Infinity, duration: 1.6, ease: 'easeInOut' }}
    >
      {chip}
    </motion.span>
  )
}
