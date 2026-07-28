import { useCallback, useEffect, useRef } from 'react'
import { Box, Chip, Stack, Typography } from '@mui/material'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useEventStream, type ConsoleEvent } from '../hooks/useEventStream'
import { MONO_FONT } from '../theme/tokens'
import type { SessionDetail, SessionModuleConfig, SessionSummary } from './types'

// Re-export the shared error helper so the session-config files importing it from `./shared` keep
// working unchanged after the F1 consolidation into common/errors.
export { axiosMessage } from '../common/errors'

// eslint-disable-next-line react-refresh/only-export-components
export function sessionIdentity(config: SessionModuleConfig, s: SessionSummary | SessionDetail): string {
  if (config.renderIdentity) return config.renderIdentity(s)
  const title = 'workItemTitle' in s ? s.workItemTitle : null
  return title ? `#${s.id} — ${title}` : `#${s.id}`
}

const PHASE_LABEL: Record<string, string> = {
  fetch: 'FETCH', retrieve: 'KB', analyst: 'ANALYST', tester: 'TESTER',
  bugwriter: 'BUG WRITER', push: 'PUSH', summarize: 'SUMMARIZE',
  discover: 'DISCOVER', 'code-verify': 'VERIFY', dedup: 'DEDUP',
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
 * Fetch one session's detail and poll it every 2.5s while it is in-flight (F2: React Query).
 * The interval self-gates on the cached status, so it stops the moment the session goes terminal,
 * and `onTerminal` fires exactly once on the in-flight → terminal transition. The public shape
 * ({ session, polling, refetch }) is unchanged so consumers don't change.
 */
export function useSessionDetail(
  config: SessionModuleConfig,
  sessionId: number | null,
  onTerminal?: () => void,
) {
  const onTerminalRef = useRef(onTerminal)
  onTerminalRef.current = onTerminal

  const query = useQuery({
    queryKey: ['session-detail', config.key, sessionId],
    queryFn: () => config.getDetail(sessionId as number),
    enabled: sessionId != null,
    // Poll while in-flight; stop (false) once terminal. Reads the just-fetched data each tick.
    refetchInterval: (q) => {
      const d = q.state.data
      return d && config.inFlightStatuses.includes(d.status) ? 2500 : false
    },
  })

  const session = query.data ?? null
  const polling = session != null && config.inFlightStatuses.includes(session.status)

  // Fire onTerminal once when a session transitions out of an in-flight status.
  const wasInFlight = useRef(false)
  useEffect(() => {
    if (!session) { wasInFlight.current = false; return }
    const nowInFlight = config.inFlightStatuses.includes(session.status)
    if (wasInFlight.current && !nowInFlight) onTerminalRef.current?.()
    wasInFlight.current = nowInFlight
  }, [session, config])

  const refetch = useCallback(() => { void query.refetch() }, [query])

  return { session, polling, refetch }
}

/**
 * Load a page of sessions and keep statuses live (F2: React Query). Polls the list endpoint every
 * 3s, but ONLY while at least one visible card is in-flight (refetchInterval gates on the cached
 * page). React Query's structural sharing keeps unchanged rows referentially stable, so the grid
 * never remounts — replacing the old merge-by-id bookkeeping. keepPreviousData avoids a flash of
 * empty while paging. Public shape ({ summaries, total, loading, refresh }) is unchanged.
 */
export function useSessionsLive(
  config: SessionModuleConfig,
  take: number,
  skip: number,
) {
  const query = useQuery({
    queryKey: ['sessions', config.key, take, skip],
    queryFn: () => config.listSessions(take, skip),
    placeholderData: keepPreviousData,
    refetchInterval: (q) => {
      const items = q.state.data?.items ?? []
      return items.some(s => config.inFlightStatuses.includes(s.status)) ? 3000 : false
    },
  })

  const refresh = useCallback(() => { void query.refetch() }, [query])

  return {
    summaries: query.data?.items ?? [],
    total: query.data?.total ?? 0,
    loading: query.isLoading,
    refresh,
  }
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
