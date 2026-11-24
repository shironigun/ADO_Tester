import { useEffect, useRef, useState } from 'react'
import { signOutIfSessionInvalid } from '../api/auth'

export interface ConsoleEvent {
  seq: number
  ts: number
  sessionKey: string
  kind: 'phase' | 'token' | 'thinking' | 'system' | 'stat' | 'result' | 'error'
  phase: 'fetch' | 'retrieve' | 'analyst' | 'tester' | 'bugwriter' | 'push' | 'summarize' | 'discover' | 'code-verify' | 'dedup' | null
  level: 'info' | 'success' | 'warn' | 'error'
  text: string
  data?: Record<string, unknown> | null
}

function maxSeq(events: ConsoleEvent[]): number {
  return events.reduce((m, e) => (e.seq > m ? e.seq : m), 0)
}

/**
 * Subscribe to a pipeline's live SSE console. Seeds once from a finished run's persisted
 * narrative (eventLogJson), then streams live events while `streamUrl` is set, appending in
 * the SSE callback. Dedupes by `seq` (EventSource auto-reconnect + Last-Event-ID may replay).
 *
 * IMPORTANT: the seed is applied via the useState initializer (not reset in an effect), so to
 * re-seed for a different session/report, remount this hook's component with a React `key`.
 * Opening a new socket for the SAME session (e.g. manual Run-Tester) keeps prior history.
 */
export function useEventStream(
  streamUrl: string | null,
  seed: ConsoleEvent[] = [],
): { events: ConsoleEvent[]; connected: boolean } {
  const [events, setEvents] = useState<ConsoleEvent[]>(seed)
  const [connected, setConnected] = useState(false)
  const lastSeqRef = useRef(maxSeq(seed))

  useEffect(() => {
    // No stream: nothing to open. `connected` is already false (initial state) or was
    // reset by the previous effect's cleanup when streamUrl became null.
    if (!streamUrl) return
    const es = new EventSource(streamUrl)
    es.onopen = () => setConnected(true)
    es.onmessage = (e: MessageEvent) => {
      try {
        const ev = JSON.parse(e.data) as ConsoleEvent
        if (ev.seq <= lastSeqRef.current) return
        lastSeqRef.current = ev.seq
        setEvents(prev => [...prev, ev])
        if (ev.data && (ev.data as Record<string, unknown>).terminal) {
          es.close()
          setConnected(false)
        }
      } catch { /* ignore malformed frame */ }
    }
    es.onerror = () => {
      setConnected(false) // browser auto-reconnects with Last-Event-ID
      void signOutIfSessionInvalid() // a 401 here (expired token) would otherwise retry forever
    }
    return () => { es.close(); setConnected(false) }
  }, [streamUrl])

  return { events, connected }
}
