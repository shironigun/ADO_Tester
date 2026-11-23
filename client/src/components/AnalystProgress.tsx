import { useEffect, useMemo, useState } from 'react'
import { Box, Card, CardContent, LinearProgress, Stack, Typography } from '@mui/material'
import PsychologyIcon from '@mui/icons-material/Psychology'
import CheckIcon from '@mui/icons-material/Check'
import { useEventStream } from '../hooks/useEventStream'

// Each sub-step maps to a top-level key in the Analyst's JSON output. As that key appears in
// the streamed tokens, the step before it is complete and this one becomes active — so the
// transitions are driven by what the Analyst is *actually* writing, not a timer. The intra-step
// bar is a gentle filler (we can't know true sub-progress); it holds below 100 until the next
// section starts. For providers that don't stream token deltas, a time-based fallback advances
// the steps so it never stalls.
const STEPS: { label: string; key: string | null }[] = [
  { label: 'Reading ticket & knowledgebase', key: null },
  { label: 'Module & feature', key: 'module' },
  { label: 'Acceptance criteria', key: 'acceptanceCriteria' },
  { label: 'Business rules', key: 'businessRules' },
  { label: 'Edge cases', key: 'edgeCases' },
  { label: 'Negative cases', key: 'negativeCases' },
  { label: 'Permissions', key: 'permissions' },
  { label: 'Cross-flow impacts', key: 'crossFlow' },
  { label: 'Open questions', key: 'openQuestions' },
]
const LAST = STEPS.length - 1

export default function AnalystProgress({ streamUrl }: { streamUrl?: string | null }) {
  const { events } = useEventStream(streamUrl ?? null)

  // Accumulated analyst output (token deltas) + lifecycle signals.
  const buf = useMemo(() => events.filter(e => e.kind === 'token').map(e => e.text).join(''), [events])
  const analystStarted = useMemo(
    () => buf.length > 0 || events.some(e => e.kind === 'phase' && e.phase === 'analyst'),
    [events, buf],
  )
  const done = useMemo(() => events.some(e => e.data && (e.data as Record<string, unknown>).terminal), [events])

  // Highest step whose JSON key has appeared in the stream — the real signal.
  const detectedStep = useMemo(() => {
    let r = analystStarted ? 0 : -1
    STEPS.forEach((s, idx) => { if (s.key && buf.includes(`"${s.key}"`)) r = Math.max(r, idx) })
    return r
  }, [buf, analystStarted])

  // Steps only ever move FORWARD (monotonic) — never jump back to an earlier section even if a
  // later poll/render briefly recomputes. This is what kills the "loaders running again" effect.
  const [reached, setReached] = useState(0)
  const [pct, setPct] = useState(0)
  useEffect(() => {
    const target = done ? LAST : Math.max(0, detectedStep)
    setReached(r => (target > r ? target : r))
  }, [detectedStep, done])

  const displayStep = done ? LAST : reached
  useEffect(() => { setPct(0) }, [displayStep])

  // Gentle filler for the active step's bar — holds at 92% until the NEXT real section starts
  // (we can't know true sub-progress within a section, so we never claim 100% prematurely).
  useEffect(() => {
    if (done) return
    const t = setInterval(() => {
      setPct(p => (p >= 92 ? 92 : Math.min(92, p + 4 + Math.random() * 9)))
    }, 140)
    return () => clearInterval(t)
  }, [done, displayStep])

  return (
    <Card sx={{ mb: 2 }}>
      <CardContent>
        <Stack direction="row" sx={{ alignItems: 'center', mb: 1.5 }}>
          <PsychologyIcon fontSize="small" color="primary" sx={{ mr: 1 }} />
          <Typography variant="subtitle1">Analyst is working…</Typography>
        </Stack>
        <Stack spacing={1.25}>
          {STEPS.map((s, j) => {
            const isDone = done || j < displayStep
            const active = !done && j === displayStep
            return (
              <Box key={s.label}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  {isDone
                    ? <CheckIcon color="success" sx={{ fontSize: 16 }} />
                    : <Box sx={{ width: 16, height: 16, borderRadius: '50%', border: '2px solid', borderColor: active ? 'primary.main' : 'divider' }} />}
                  <Typography variant="body2" sx={{ flex: 1 }} color={isDone || active ? 'text.primary' : 'text.disabled'}>
                    {s.label}
                  </Typography>
                  {(isDone || active) && (
                    <Typography variant="caption" color="text.secondary">{isDone ? '100%' : `${Math.round(pct)}%`}</Typography>
                  )}
                </Stack>
                {active && <LinearProgress variant="determinate" value={pct} sx={{ mt: 0.5, height: 4, borderRadius: 2 }} />}
              </Box>
            )
          })}
        </Stack>
      </CardContent>
    </Card>
  )
}
