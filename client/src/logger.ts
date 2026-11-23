import { apiUrl } from './config/env'

// Free, DB-only frontend error/warning logger. Captures uncaught errors, unhandled promise
// rejections, React render errors (via ErrorBoundary), explicit logger.warn/error calls, and failed
// API requests, then batches them to the backend (POST /api/logs → ErrorLogs table). There is no
// viewer — debug by querying the SQLite ErrorLogs table directly.
//
// Failed API requests are logged by the response interceptor on the shared axios instance in
// api/client.ts (every feature calls through it). Sending uses fetch (NOT axios) on purpose, so that
// interceptor cannot recurse into logging when the /api/logs request itself fails.

type Level = 'warn' | 'error'

interface Entry {
  level: Level
  message: string
  stack?: string
  url?: string
  source?: string
  userAgent?: string
  context?: string
}

const MAX_QUEUE = 50
const DEDUP_WINDOW_MS = 5000
const FLUSH_DELAY_MS = 2000

const queue: Entry[] = []
const recent = new Map<string, number>()
let flushTimer: ReturnType<typeof setTimeout> | undefined

function safeJson(v: unknown): string | undefined {
  if (v == null) return undefined
  try {
    return typeof v === 'string' ? v : JSON.stringify(v)
  } catch {
    return undefined
  }
}

function enqueue(entry: Entry): void {
  const now = Date.now()
  const key = `${entry.level}|${entry.message}`
  const last = recent.get(key)
  if (last != null && now - last < DEDUP_WINDOW_MS) return // collapse rapid duplicates
  recent.set(key, now)

  if (queue.length >= MAX_QUEUE) queue.shift()
  queue.push({
    ...entry,
    url: entry.url ?? location.href,
    userAgent: navigator.userAgent,
  })

  if (flushTimer == null) flushTimer = setTimeout(flush, FLUSH_DELAY_MS)
}

function flush(): void {
  flushTimer = undefined
  if (queue.length === 0) return
  const batch = queue.splice(0, queue.length)
  try {
    void fetch(apiUrl('/api/logs'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(batch),
      keepalive: true,
    }).catch(() => {
      /* swallow — logging must never throw or recurse */
    })
  } catch {
    /* ignore */
  }
}

export const logger = {
  error(message: string, extra?: { stack?: string; source?: string; context?: unknown }): void {
    enqueue({ level: 'error', message: String(message), stack: extra?.stack, source: extra?.source, context: safeJson(extra?.context) })
  },
  warn(message: string, extra?: { source?: string; context?: unknown }): void {
    enqueue({ level: 'warn', message: String(message), source: extra?.source, context: safeJson(extra?.context) })
  },
}

let installed = false

export function installGlobalErrorLogging(): void {
  if (installed) return
  installed = true

  window.addEventListener('error', (ev) => {
    const where = ev.filename ? `${ev.filename}:${ev.lineno}:${ev.colno}` : 'window'
    logger.error(ev.message || 'window.onerror', { stack: ev.error?.stack, source: where })
  })

  window.addEventListener('unhandledrejection', (ev) => {
    const reason = ev.reason as { message?: string; stack?: string } | undefined
    logger.error(`Unhandled promise rejection: ${reason?.message ?? String(ev.reason)}`, {
      stack: reason?.stack,
      source: 'unhandledrejection',
    })
  })

  window.addEventListener('beforeunload', () => flush())

  // Failed API calls are logged by the interceptor on the shared axios instance (api/client.ts).
}
