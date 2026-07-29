import { Box, Stack, Typography } from '@mui/material'
import type { ReactNode } from 'react'
import { FadeIn } from '../motion/primitives'

/**
 * Standard page header. The module name now lives in the AppBar (see Layout), so the title is no
 * longer rendered here — this just carries the optional subtitle and a right-aligned actions slot.
 * `title` is kept in the props for call-site compatibility. Renders nothing when there's nothing to show.
 */
export default function PageHeader({
  subtitle, actions,
}: { title?: string; subtitle?: ReactNode; actions?: ReactNode }) {
  if (!subtitle && !actions) return null
  return (
    <FadeIn sx={{ mb: 3 }} y={8}>
      <Stack direction="row" spacing={2}
        sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1.5 }}>
        <Box>
          {subtitle && (
            <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 720 }}>
              {subtitle}
            </Typography>
          )}
        </Box>
        {actions && <Box sx={{ flexShrink: 0 }}>{actions}</Box>}
      </Stack>
    </FadeIn>
  )
}
