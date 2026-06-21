import axios from 'axios'

/** Human-readable message from an unknown error — unwraps an axios response body (string, or an
 *  object with a `title`/`detail` field), else falls back to the error message. Shared by pages
 *  during the F1 migration (replaces the per-page `axiosMessage` copies).
 *
 *  `maxLen` guards against dumping a huge raw body (e.g. a 500 that returns an HTML error page) into
 *  the UI — when a string body exceeds it, a generic "Request failed (status)" is returned instead. */
export function axiosMessage(e: unknown, opts?: { maxLen?: number }): string {
  if (!axios.isAxiosError(e)) return String(e)
  const status = e.response?.status
  const d = e.response?.data as unknown
  if (typeof d === 'string') {
    if (opts?.maxLen && d.length > opts.maxLen) return `Request failed${status ? ` (${status})` : ''}`
    return d
  }
  if (d && typeof d === 'object') {
    const obj = d as { title?: unknown; detail?: unknown }
    if (typeof obj.title === 'string') return obj.title
    if (typeof obj.detail === 'string') return obj.detail
  }
  return e.message
}
