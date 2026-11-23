import { Stack, Typography } from '@mui/material'
import { PulsingAgentBadge } from '../motion/primitives'

/**
 * Inline "agent is working" row — a pulsing dot + the current activity text.
 * Replaces the bare `<CircularProgress size={14} />` + text pattern.
 */
export default function LiveActivityIndicator({
  active, text, center = false,
}: { active: boolean; text: string | null; center?: boolean }) {
  if (!text) return null
  return (
    <Stack direction="row" spacing={1}
      sx={{ alignItems: 'center', justifyContent: center ? 'center' : 'flex-start' }}>
      {active && <PulsingAgentBadge size={9} />}
      <Typography variant="body2"
        color={active ? 'primary.main' : 'text.secondary'}
        sx={{ fontWeight: active ? 600 : 400 }}>
        {text}
      </Typography>
    </Stack>
  )
}
