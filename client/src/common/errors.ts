import axios from 'axios'

/** Human-readable message from an unknown error — unwraps an axios response body (string, or an
 *  object with a `title`/`detail` field), else falls back to the error message. Shared by pages
 *  during the F1 migration (replaces the per-page `axiosMessage` copies). */
export function axiosMessage(e: unknown): string {
  if (!axios.isAxiosError(e)) return String(e)
  const d = e.response?.data as unknown
  if (typeof d === 'string') return d
  if (d && typeof d === 'object') {
    const obj = d as { title?: unknown; detail?: unknown }
    if (typeof obj.title === 'string') return obj.title
    if (typeof obj.detail === 'string') return obj.detail
  }
  return e.message
}
