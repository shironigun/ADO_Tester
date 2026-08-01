import { useEffect, useMemo, useRef, useState } from 'react'
import { Box, Chip, Stack, Typography } from '@mui/material'
import { alpha } from '@mui/material/styles'
import { motion, useReducedMotion } from 'framer-motion'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import AgentOrb from './AgentOrb'
import { AnimatedNumber } from '../motion/primitives'
import { useEventStream, type ConsoleEvent } from '../hooks/useEventStream'
import { MONO_FONT } from '../theme/tokens'

interface Line {
  id: number
  kind: ConsoleEvent['kind']
  phase: ConsoleEvent['phase']
  level: ConsoleEvent['level']
  text: string
  ts: number
  stream?: boolean
}

const PHASE_LABEL: Record<string, string> = {
  fetch: 'FETCH', retrieve: 'KB', analyst: 'ANALYST', tester: 'TESTER',
  bugwriter: 'BUG WRITER', push: 'PUSH', summarize: 'SUMMARIZE',
  discover: 'DISCOVER', 'code-verify': 'VERIFY',
  run: 'RUN',
  // KBG-108: QA Loop KB-grounded phases
  'retrieve-kb': 'KB', scan: 'SCAN', analyze: 'ANALYZE',
  'analyze-ui': 'UI ANALYSIS', 'run-ui': 'RUN UI', 'failure-explain': 'FAILURE',
}

function buildModel(events: ConsoleEvent[]): { lines: Line[]; chars: number; elapsed: number } {
  const lines: Line[] = []
  let statChars = 0
  let elapsed = 0
  let tokenChars = 0
  for (const ev of events) {
    if (ev.kind === 'stat') {
      const d = ev.data as { chars?: number; elapsed?: number } | null | undefined
      if (d) { statChars = d.chars ?? statChars; elapsed = d.elapsed ?? elapsed }
      continue
    }
    if (ev.kind === 'token' || ev.kind === 'thinking') {
      if (ev.kind === 'token') tokenChars += ev.text.length
      const last = lines[lines.length - 1]
      if (last && last.stream && last.kind === ev.kind && last.phase === ev.phase) {
        last.text += ev.text
      } else {
        lines.push({ id: ev.seq, kind: ev.kind, phase: ev.phase, level: ev.level, text: ev.text, ts: ev.ts, stream: true })
      }
    } else {
      lines.push({ id: ev.seq, kind: ev.kind, phase: ev.phase, level: ev.level, text: ev.text, ts: ev.ts })
    }
  }
  return { lines, chars: statChars || tokenChars, elapsed }
}

function lineColor(line: Line): string {
  if (line.level === 'error' || line.kind === 'error') return '#ff6b6b'
  if (line.level === 'success') return '#5ad6a0'
  if (line.level === 'warn') return '#ffcf6b'
  if (line.kind === 'thinking') return alpha('#9fb3d1', 0.62)
  if (line.kind === 'phase') return '#7fb2ff'
  if (line.kind === 'system') return alpha('#9fb3d1', 0.85)
  return '#dfe7f2' // token / default
}

function fmtTime(ts: number): string {
  const d = new Date(ts * 1000)
  const z = (n: number) => String(n).padStart(2, '0')
  return `${z(d.getHours())}:${z(d.getMinutes())}:${z(d.getSeconds())}`
}

/**
 * Live terminal-style console for an agent pipeline. Renders SSE events: phase headings,
 * streamed model thinking (dimmed) and output, stats, results. Auto-scrolls with a
 * follow-pause when the user scrolls up.
 */
export default function AgentConsole({
  streamUrl = null, seedEvents = [], title = 'Agent console', fillHeight = false,
  events: controlledEvents, connected: controlledConnected,
}: {
  streamUrl?: string | null; seedEvents?: ConsoleEvent[]; title?: string; fillHeight?: boolean
  events?: ConsoleEvent[]; connected?: boolean
}) {
  const reduce = useReducedMotion()
  // Controlled mode: the parent owns the stream and passes events/connected (so we don't open a second
  // EventSource). Uncontrolled mode: open our own stream from streamUrl + seedEvents.
  const stream = useEventStream(controlledEvents ? null : streamUrl, seedEvents)
  const events = controlledEvents ?? stream.events
  const connected = controlledConnected ?? stream.connected
  const { lines, chars, elapsed } = useMemo(() => buildModel(events), [events])

  const scrollRef = useRef<HTMLDivElement>(null)
  const [follow, setFollow] = useState(true)

  useEffect(() => {
    if (follow && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [lines, follow])

  function onScroll() {
    const el = scrollRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24
    setFollow(atBottom)
  }

  if (lines.length === 0 && !streamUrl && !connected) return null

  return (
    <Box sx={{ mt: 2, display: 'flex', flexDirection: 'column', flex: fillHeight ? 1 : undefined, minHeight: 0 }}>
      {/* Header */}
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
        {connected && <AgentOrb size={18} />}
        <Typography variant="overline" sx={{ color: 'text.secondary' }}>{title}</Typography>
        <Box sx={{ flex: 1 }} />
        {(chars > 0 || elapsed > 0) && (
          <Typography variant="caption" sx={{ fontFamily: MONO_FONT, color: 'text.secondary' }}>
            <AnimatedNumber value={chars} /> chars · {elapsed}s
          </Typography>
        )}
        <Box sx={{
          width: 8, height: 8, borderRadius: '50%',
          bgcolor: connected ? 'success.main' : 'text.disabled',
          boxShadow: theme => connected ? `0 0 8px ${theme.palette.success.main}` : 'none',
        }} />
      </Stack>

      {/* Terminal surface */}
      <Box sx={{ position: 'relative', flex: fillHeight ? 1 : undefined, minHeight: 0 }}>
        <Box
          ref={scrollRef}
          onScroll={onScroll}
          sx={{
            fontFamily: MONO_FONT, fontSize: 12.5, lineHeight: 1.55,
            bgcolor: '#0a0e16',
            color: '#dfe7f2',
            borderRadius: 2,
            border: theme => `1px solid ${alpha(theme.palette.primary.main, 0.25)}`,
            boxShadow: 'inset 0 2px 18px rgba(0,0,0,0.45)',
            p: 1.5,
            maxHeight: fillHeight ? 'none' : 360,
            height: fillHeight ? '100%' : undefined,
            minHeight: 120,
            overflowY: 'auto',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
          }}
        >
          {lines.length === 0 && (
            <Box sx={{ color: alpha('#9fb3d1', 0.6) }}>Waiting for the pipeline to start…</Box>
          )}
          {lines.map((line, i) => {
            const isLast = i === lines.length - 1
            return (
              <Box key={line.id} sx={{ display: 'flex', gap: 1 }}>
                <Box component="span" sx={{ color: alpha('#9fb3d1', 0.4), flexShrink: 0 }}>
                  {fmtTime(line.ts)}
                </Box>
                {line.phase && line.kind !== 'token' && line.kind !== 'thinking' && (
                  <Box component="span" sx={{
                    color: '#7fb2ff', flexShrink: 0, opacity: 0.85, minWidth: 72,
                  }}>
                    [{PHASE_LABEL[line.phase] ?? line.phase}]
                  </Box>
                )}
                <Box component="span" sx={{
                  color: lineColor(line),
                  fontStyle: line.kind === 'thinking' ? 'italic' : 'normal',
                  flex: 1,
                }}>
                  {line.text}
                  {isLast && line.stream && connected && (
                    <Box component={motion.span}
                      animate={reduce ? {} : { opacity: [1, 0.15, 1] }}
                      transition={{ repeat: Infinity, duration: 1 }}
                      sx={{ display: 'inline-block', ml: '1px' }}>▍</Box>
                    )}
                </Box>
              </Box>
            )
          })}
        </Box>

        {!follow && (
          <Chip
            size="small"
            icon={<KeyboardArrowDownIcon />}
            label="Jump to live"
            onClick={() => setFollow(true)}
            sx={{
              position: 'absolute', bottom: 10, left: '50%', transform: 'translateX(-50%)',
              cursor: 'pointer', bgcolor: 'primary.main', color: 'primary.contrastText',
              '& .MuiChip-icon': { color: 'primary.contrastText' },
            }}
          />
        )}
      </Box>
    </Box>
  )
}
