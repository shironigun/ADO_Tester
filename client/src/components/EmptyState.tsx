import { Box, Stack, Typography } from '@mui/material'
import { motion } from 'framer-motion'
import type { ReactNode } from 'react'
import { FadeIn } from '../motion/primitives'

/**
 * Friendly empty / placeholder state — replaces the scattered
 * "None yet." / "No sessions yet." / "Coming soon." inline text.
 */
export default function EmptyState({
  icon, title, hint, compact = false,
}: { icon?: ReactNode; title: string; hint?: ReactNode; compact?: boolean }) {
  return (
    <FadeIn>
      <Stack spacing={1} sx={{
        alignItems: 'center', textAlign: 'center',
        py: compact ? 2 : 5, px: 2, color: 'text.secondary',
      }}>
        {icon && (
          <Box component={motion.div}
            animate={{ y: [0, -6, 0] }}
            transition={{ repeat: Infinity, duration: 3.2, ease: 'easeInOut' }}
            sx={{
              display: 'flex', color: 'text.disabled',
              fontSize: compact ? 28 : 40, '& svg': { fontSize: 'inherit' },
            }}>
            {icon}
          </Box>
        )}
        <Typography variant={compact ? 'body2' : 'subtitle1'} sx={{ fontWeight: 600, color: 'text.primary' }}>
          {title}
        </Typography>
        {hint && (
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 380 }}>
            {hint}
          </Typography>
        )}
      </Stack>
    </FadeIn>
  )
}
