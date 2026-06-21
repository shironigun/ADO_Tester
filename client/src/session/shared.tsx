import { useCallback, useEffect, useRef, useState } from 'react'
import { Box, Chip, Stack, Typography } from '@mui/material'
import { useEventStream, type ConsoleEvent } from '../hooks/useEventStream'
import { MONO_FONT } from '../theme/tokens'
import type { SessionDetail, SessionModuleConfig, SessionSummary } from './types'

// Re-export the shared error helper so the session-config files importing it from `./shared` keep
// working unchanged after the F1 consolidation into common/errors.
export { axiosMessage } from '../common/errors'

const PHASE_LABEL: Record<string, string> = {
  fetch: 'FETCH', retrieve: 'KB', analyst: 'ANALYST', tester: 'TESTER',
  bugwriter: 'BUG WRITER', push: 'PUSH', summarize: 'SUMMARIZE',
  discover: 'DISCOVER', 'code-verify': 'VERIFY',
}

function fmtClock(ts: number): string {
  const d = new Date(ts * 1000)
  const z = (n: number) => String(n).padStart(2, '0')
  return `${z(d.getHours())}:${z(d.getMinutes())}:${z(d.getSeconds())}`
}

export function parseEventLog(json: string | null | undefined): ConsoleEvent[] {
  if (!json) return []
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : [] } catch { return [] }
}

/**
 * Fetch one session's detail and poll it every 2.5s while it is in-flight.
 * Mirrors the per-page openSession/startPolling/stopPolling logic, unified.
 */
export function useSessionDetail(
  config: SessionModuleConfig,
  sessionId: number | null,
  onTerminal?: () => void,
) {
  const [session, setSession] = useState<SessionDetail | null>(null)
  const [polling, setPolling] = useState(false)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const onTerminalRef = useRef(onTerminal)
  onTerminalRef.current = onTerminal

  const stop = useCallback(() => {
    setPolling(false)
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null }
  }, [])

  const inFlight = useCallback(
    (s: string) => config.inFlightStatuses.includes(s),
    [config],
  )

  const refetch = useCallback(async () => {
    if (sessionId == null) return
    try {
      const data = await config.getDetail(sessionId)
      setSession(data)
      if (inFlight(data.status)) {
        if (!pollRef.current) {
          setPolling(true)
          pollRef.current = setInterval(async () => {
            try {
              const d = await config.getDetail(sessionId)
              setSession(d)
              if (!inFlight(d.status)) { stop(); onTerminalRef.current?.() }
            } catch { /* swallow transient poll errors */ }
          }, 2500)
        }
      } else {
        stop()
      }
    } catch { /* surfaced by caller actions */ }
  }, [config, sessionId, inFlight, stop])

  useEffect(() => {
    stop()
    setSession(null)
    if (sessionId != null) refetch()
    return stop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId])

  return { session, polling, refetch }
}

function mergeById(prev: SessionSummary[], next: SessionSummary[]): SessionSummary[] {
  if (prev.length === 0) return next
  const map = new Map(next.map(s => [s.id, s]))
  // Keep prior order/identity where possible; overwrite mutable fields.
  const merged = prev.map(p => map.get(p.id) ?? p)
  // Append any genuinely new sessions that weren't in prev.
  const seen = new Set(prev.map(p => p.id))
  for (const n of next) if (!seen.has(n.id)) merged.unshift(n)
  return merged
}

/**
 * Load a page of sessions and keep statuses live: poll the list endpoint every
 * 3s, but ONLY while at least one visible card is in-flight. Merge-by-id so the
 * grid never remounts (stable keys → no scroll/selection loss).
 */
export function useSessionsLive(
  config: SessionModuleConfig,
  take: number,
  skip: number,
) {
  const [summaries, setSummaries] = useState<SessionSummary[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const refresh = useCallback(async () => {
    try {
      const { items, total } = await config.listSessions(take, skip)
      setSummaries(prev => mergeById(prev, items ?? []))
      setTotal(total ?? 0)
    } catch { /* swallow */ }
    finally { setLoading(false) }
  }, [config, take, skip])

  // Reset + load when paging or module changes.
  useEffect(() => {
    setLoading(true)
    setSummaries([])
    refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, take, skip])

  // Gated poll: run only while a visible card is in-flight.
  const anyInFlight = summaries.some(s => config.inFlightStatuses.includes(s.status))
  useEffect(() => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null }
    if (!anyInFlight) return
    pollRef.current = setInterval(refresh, 3000)
    return () => { if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null } }
  }, [anyInFlight, refresh])

  return { summaries, total, loading, refresh }
}

/** Timeline tab — a slim, timestamped chronology of pipeline milestones (no verbose
 * token/thinking stream; that lives in the Detail tab's Activity log). */
export function SessionTimeline({
  config, session, live,
}: { config: SessionModuleConfig; session: SessionDetail; live: boolean }) {
  const { events } = useEventStream(
    live ? config.streamUrl(session.id) : null,
    parseEventLog(session.eventLogJson),
  )
  // Milestones only — drop token/thinking/stat noise.
  const rows = events.filter(e => !['token', 'thinking', 'stat'].includes(e.kind))

  if (rows.length === 0) {
    return <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>No timeline events yet.</Typography>
  }

  const dotColor = (e: ConsoleEvent) =>
    e.level === 'error' || e.kind === 'error' ? 'error.main'
    : e.level === 'success' ? 'success.main'
    : e.level === 'warn' ? 'warning.main'
    : e.kind === 'phase' ? 'primary.main' : 'text.disabled'

  return (
    <Stack spacing={0.25}>
      {rows.map(e => (
        <Stack key={e.seq} direction="row" spacing={1.25} sx={{ alignItems: 'baseline', py: 0.4, borderBottom: 1, borderColor: 'divider' }}>
          <Typography variant="caption" sx={{ fontFamily: MONO_FONT, color: 'text.secondary', flexShrink: 0 }}>
            {fmtClock(e.ts)}
          </Typography>
          <Box sx={{ width: 7, height: 7, borderRadius: '50%', bgcolor: dotColor(e), flexShrink: 0, alignSelf: 'center' }} />
          {e.phase && (
            <Chip size="small" variant="outlined" label={PHASE_LABEL[e.phase] ?? e.phase}
              sx={{ height: 18, flexShrink: 0, '& .MuiChip-label': { px: 0.75, fontSize: 10 } }} />
          )}
          <Typography variant="body2" sx={{ flex: 1, minWidth: 0 }}>{e.text}</Typography>
        </Stack>
      ))}
    </Stack>
  )
}
