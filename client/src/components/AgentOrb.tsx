import { Box } from '@mui/material'
import { motion, useReducedMotion } from 'framer-motion'

/**
 * Animated "agent is thinking" orb — a gradient core with radar-pulse rings
 * expanding out of it. Used wherever an agent is actively working.
 * Honours reduced-motion (rings collapse to a static dot).
 */
export default function AgentOrb({ size = 22 }: { size?: number }) {
  const reduce = useReducedMotion()

  return (
    <Box sx={{
      position: 'relative', width: size, height: size, flexShrink: 0,
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    }}>
      {/* radar rings */}
      {!reduce && [0, 0.6].map(delay => (
        <Box key={delay} component={motion.span}
          initial={{ scale: 0.4, opacity: 0.6 }}
          animate={{ scale: 2.2, opacity: 0 }}
          transition={{ repeat: Infinity, duration: 1.8, ease: 'easeOut', delay }}
          sx={{
            position: 'absolute', width: size, height: size, borderRadius: '50%',
            border: '2px solid', borderColor: 'primary.main',
          }}
        />
      ))}
      {/* gradient core */}
      <Box component={motion.span}
        animate={reduce ? {} : { scale: [1, 1.18, 1] }}
        transition={{ repeat: Infinity, duration: 1.5, ease: 'easeInOut' }}
        sx={{
          width: size * 0.55, height: size * 0.55, borderRadius: '50%',
          backgroundImage: theme =>
            `linear-gradient(135deg, ${theme.palette.primary.main}, ${theme.palette.secondary.main})`,
          boxShadow: theme => `0 0 12px ${theme.palette.primary.main}`,
        }}
      />
    </Box>
  )
}
