import { Box, Stack, Typography } from '@mui/material'
import type { ReactNode } from 'react'
import { FadeIn } from '../motion/primitives'

/**
 * Standard page header — replaces the bare `<Typography variant="h5" gutterBottom>`
 * at the top of every page. Adds an optional subtitle and a right-aligned
 * actions slot, with a subtle fade-in.
 */
export default function PageHeader({
  title, subtitle, actions,
}: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <FadeIn sx={{ mb: 3 }} y={8}>
      <Stack direction="row" spacing={2}
        sx={{ alignItems: 'flex-end', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1.5 }}>
        <Box>
          <Typography variant="h4" sx={{ lineHeight: 1.1 }}>{title}</Typography>
          {subtitle && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, maxWidth: 720 }}>
              {subtitle}
            </Typography>
          )}
        </Box>
        {actions && <Box sx={{ flexShrink: 0 }}>{actions}</Box>}
      </Stack>
    </FadeIn>
  )
}
