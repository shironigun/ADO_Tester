import { useMemo } from 'react'
import { Box, Card, CardContent, Chip, CircularProgress, Stack, Typography } from '@mui/material'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import EditNoteIcon from '@mui/icons-material/EditNote'
import { useEventStream } from '../hooks/useEventStream'

export interface StreamCase { title: string; steps: number }

/**
 * Incrementally parse the Tester's streaming JSON array. As the LLM streams the array of
 * `{title, preconditions, steps:[…]}` objects, detect each COMPLETED top-level object and
 * surface its title + step count — so test cases reveal one-by-one as they finish, rather than
 * all at once at the end. `writing` is true while an object is mid-stream.
 *
 * String-aware brace scanner: nested `steps` arrays/objects only matter via brace depth, so an
 * object is "done" exactly when depth returns to 0. Starts at the first `[` (the array opener).
 */
export function parseStreamingCases(buf: string): { cases: StreamCase[]; writing: boolean } {
  const cases: StreamCase[] = []
  const start = buf.indexOf('[')
  if (start < 0) return { cases, writing: false }
  let inStr = false, esc = false, depth = 0, objStart = -1
  for (let i = start + 1; i < buf.length; i++) {
    const ch = buf[i]
    if (inStr) {
      if (esc) esc = false
      else if (ch === '\\') esc = true
      else if (ch === '"') inStr = false
      continue
    }
    if (ch === '"') { inStr = true; continue }
    if (ch === '{') { if (depth === 0) objStart = i; depth++ }
    else if (ch === '}') {
      depth--
      if (depth === 0 && objStart >= 0) {
        try {
          const o = JSON.parse(buf.slice(objStart, i + 1)) as { title?: string; steps?: unknown[] }
          const steps = Array.isArray(o.steps) ? o.steps.length : 0
          cases.push({ title: (o.title || '').trim(), steps })
        } catch { /* malformed slice — skip */ }
        objStart = -1
      }
    } else if (ch === ']' && depth === 0) {
      break // array closed — done
    }
  }
  return { cases, writing: depth > 0 }
}

/**
 * Live view of the Tester drafting test cases. Reads the pipeline's SSE stream, isolates the
 * Tester phase's token deltas (`kind:'token'`, `phase:'tester'`), and reveals each finished test
 * case with a running count. Mirrors AnalystProgress; shown only while status is Drafting and no
 * drafts have been persisted yet (after which the real draft tiles take over).
 */
export default function TesterProgress({ streamUrl }: { streamUrl?: string | null }) {
  const { events } = useEventStream(streamUrl ?? null)
  const buf = useMemo(
    () => events.filter(e => e.kind === 'token' && e.phase === 'tester').map(e => e.text).join(''),
    [events],
  )
  const { cases, writing } = useMemo(() => parseStreamingCases(buf), [buf])

  return (
    <Card variant="outlined" sx={{ mb: 2 }}>
      <CardContent>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: cases.length ? 1.5 : 0 }}>
          <CircularProgress size={16} />
          <Typography variant="subtitle2" sx={{ flex: 1 }}>Tester is writing test cases…</Typography>
          <Chip size="small" color="primary" variant="outlined"
            label={`${cases.length} generated`} sx={{ fontVariantNumeric: 'tabular-nums' }} />
        </Stack>
        <Stack spacing={0.75}>
          {cases.map((c, i) => (
            <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
              <CheckCircleIcon color="success" sx={{ fontSize: 16, mt: 0.25, flexShrink: 0 }} />
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2">{c.title || `Test case ${i + 1}`}</Typography>
                {c.steps > 0 && (
                  <Typography variant="caption" color="text.secondary">{c.steps} step{c.steps === 1 ? '' : 's'}</Typography>
                )}
              </Box>
            </Stack>
          ))}
          {(writing || cases.length === 0) && (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', opacity: 0.75 }}>
              <EditNoteIcon sx={{ fontSize: 16, flexShrink: 0 }} color="disabled" />
              <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
                Writing test case {cases.length + 1}…
              </Typography>
            </Stack>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
