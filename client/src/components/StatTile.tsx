import { Box, Card, CardContent, Stack, Typography } from '@mui/material'
import { alpha } from '@mui/material/styles'
import type { ReactNode } from 'react'
import { AnimatedNumber } from '../motion/primitives'

/**
 * A KPI tile for the dashboard — big animated number, icon chip in an accent
 * tint, optional sublabel. Inherits the card hover-lift from the theme.
 */
export default function StatTile({
  icon, label, value, sub, color = 'primary.main',
}: {
  icon: ReactNode
  label: string
  value: number
  sub?: ReactNode
  /** A theme palette path like 'primary.main' or 'success.main'. */
  color?: string
}) {
  return (
    <Card sx={{ height: '100%' }}>
      <CardContent sx={{ p: 2.5 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1.5 }}>
          <Box sx={theme => {
            // resolve "primary.main" → actual colour for the tint
            const [grp, shade] = color.split('.') as [string, string]
            const pal = (theme.palette as unknown as Record<string, { [k: string]: string }>)[grp]
            const hex = pal?.[shade] ?? theme.palette.primary.main
            return {
              width: 40, height: 40, borderRadius: 2.5, display: 'flex',
              alignItems: 'center', justifyContent: 'center',
              color: hex, bgcolor: alpha(hex, 0.14),
              '& svg': { fontSize: 22 },
            }
          }}>
            {icon}
          </Box>
          <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.2 }}>
            {label}
          </Typography>
        </Stack>
        <Typography variant="h3" sx={{ lineHeight: 1 }}>
          <AnimatedNumber value={value} />
        </Typography>
        {sub && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
            {sub}
          </Typography>
        )}
      </CardContent>
    </Card>
  )
}
