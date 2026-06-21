import axios from 'axios'

/** Human-readable message from an unknown error — unwraps an axios response body (string, or an
 *  object with a `detail`/`title`/`error` field), else falls back to the error message. Shared by
 *  pages during the F1 migration (replaces the per-page `axiosMessage` copies).
 *
 *  Field precedence is detail → title → error: ProblemDetails `title` is generic ("Bad Request")
 *  while `detail` carries the specific cause, so detail wins when both are present.
 *
 *  `maxLen` guards against dumping a huge raw body (e.g. a 500 that returns an HTML error page) into
 *  the UI — when a string body exceeds it, a generic "Request failed (status)" is returned instead. */
export function axiosMessage(e: unknown, opts?: { maxLen?: number }): string {
  if (!axios.isAxiosError(e)) return e instanceof Error ? e.message : String(e)
  const status = e.response?.status
  const d = e.response?.data as unknown
  if (typeof d === 'string') {
    if (opts?.maxLen && d.length > opts.maxLen) return `Request failed${status ? ` (${status})` : ''}`
    return d
  }
  if (d && typeof d === 'object') {
    const obj = d as { detail?: unknown; title?: unknown; error?: unknown }
    if (typeof obj.detail === 'string') return obj.detail
    if (typeof obj.title === 'string') return obj.title
    if (typeof obj.error === 'string') return obj.error
  }
  return e.message
}
