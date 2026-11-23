import { Box, CircularProgress } from '@mui/material'
import { alpha } from '@mui/material/styles'
import { motion } from 'framer-motion'
import CheckIcon from '@mui/icons-material/Check'
import type { ReactNode } from 'react'

/**
 * Animated pipeline step icon — extracted from the duplicated
 * PipelineStepIcon / PipelineIcon in AuthorTestsPage & FindBugsPage.
 *   - completed → ✓ on a success-tinted disc
 *   - running   → spinning ring + a gentle scale pulse (agent is working)
 *   - active    → primary-tinted disc, scaled up slightly
 *   - pending   → dim outline
 * Reads the `*.lighter` palette shades (now defined in the theme).
 */
export default function PipelineStepIcon({
  active, completed, running, failed = false, icon,
}: {
  active: boolean; completed: boolean; running: boolean
  failed?: boolean; icon: ReactNode
}) {
  const color = failed ? 'error.main' : completed ? 'success.main' : active ? 'primary.main' : 'text.disabled'
  const bg = failed ? 'error.lighter' : completed ? 'success.lighter' : active ? 'primary.lighter' : 'transparent'

  return (
    <Box
      component={motion.div}
      initial={false}
      animate={{ scale: running ? [1, 1.08, 1] : active || completed ? 1.04 : 1 }}
      transition={running
        ? { repeat: Infinity, duration: 1.6, ease: 'easeInOut' }
        : { type: 'spring', stiffness: 320, damping: 22 }}
      sx={{
        position: 'relative', width: 44, height: 44, borderRadius: '50%',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        bgcolor: bg, border: '2px solid', borderColor: color, color,
        transition: 'background-color 250ms, border-color 250ms, color 250ms, box-shadow 250ms',
        boxShadow: theme =>
          running ? `0 0 0 6px ${alpha(theme.palette.primary.main, 0.16)}, 0 0 22px ${alpha(theme.palette.primary.main, 0.5)}`
          : completed ? `0 0 0 4px ${alpha(theme.palette.success.main, 0.14)}`
          : active ? `0 0 0 5px ${alpha(theme.palette.primary.main, 0.12)}`
          : 'none',
      }}
    >
      {running && (
        <CircularProgress
          size={48} thickness={3}
          sx={{ position: 'absolute', top: -4, left: -4, color: 'primary.main' }}
        />
      )}
      {completed ? <CheckIcon /> : icon}
    </Box>
  )
}
