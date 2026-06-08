import { Card, CardContent, Stack, Typography, Box } from '@mui/material'
import type { ReactNode } from 'react'

/**
 * A themed panel — replaces the repeated `<Card variant="outlined"><CardContent>`
 * idiom with an optional title row and a hover lift. Reused across all pages.
 */
export default function Section({
  title, subtitle, action, children, padding = 3, dense = false, sx,
}: {
  title?: ReactNode
  subtitle?: ReactNode
  action?: ReactNode
  children: ReactNode
  padding?: number
  dense?: boolean
  sx?: object
}) {
  return (
    <Card sx={sx}>
      <CardContent sx={{ p: padding, '&:last-child': { pb: padding } }}>
        {(title || action) && (
          <Stack direction="row"
            sx={{ alignItems: 'flex-start', justifyContent: 'space-between', mb: dense ? 1 : 2, gap: 1 }}>
            <Box>
              {title && (
                <Typography variant={dense ? 'subtitle1' : 'h6'}>{title}</Typography>
              )}
              {subtitle && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                  {subtitle}
                </Typography>
              )}
            </Box>
            {action && <Box sx={{ flexShrink: 0 }}>{action}</Box>}
          </Stack>
        )}
        {children}
      </CardContent>
    </Card>
  )
}
