import { Box, Card, CardContent, Skeleton, Stack } from '@mui/material'
import AgentOrb from './AgentOrb'
import { ShimmerText } from '../motion/primitives'

/** A card-shaped skeleton placeholder for initial loads. */
export function CardSkeleton({ lines = 3 }: { lines?: number }) {
  return (
    <Card>
      <CardContent>
        <Skeleton variant="text" width="45%" height={28} />
        {Array.from({ length: lines }).map((_, i) => (
          <Skeleton key={i} variant="text" width={`${90 - i * 12}%`} />
        ))}
      </CardContent>
    </Card>
  )
}

/**
 * "Agent is working" placeholder — shimmering draft cards shown while an agent
 * is generating output but nothing has landed yet. More alive than static text.
 */
export function ThinkingShimmer({ label = 'Agent is drafting…' }: {
  label?: string
}) {
  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'center' }}>
        <AgentOrb size={22} />
        <ShimmerText sx={{ fontSize: '0.9rem' }}>{label}</ShimmerText>
      </Stack>
    </Box>
  )
}

/** A list of compact row skeletons — for session / report history panels. */
export function ListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <Stack spacing={1}>
      {Array.from({ length: rows }).map((_, i) => (
        <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Skeleton variant="rounded" width={64} height={22} />
          <Skeleton variant="text" sx={{ flex: 1 }} />
        </Stack>
      ))}
    </Stack>
  )
}
