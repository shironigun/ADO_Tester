import { useEffect, useRef } from 'react'
import { Box, Typography } from '@mui/material'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { signOutIfSessionInvalid } from '../api/auth'

/**
 * Live raw-ANSI terminal for Scripts Board runs (SB-307).
 * Subscribes to SSE and writes only `kind === 'term'` chunks — not narrative events.
 */
export default function TermConsole({
  streamUrl, running, height = 320,
}: {
  streamUrl: string | null
  running: boolean
  height?: number
}) {
  const hostRef = useRef<HTMLDivElement | null>(null)
  const termRef = useRef<Terminal | null>(null)

  useEffect(() => {
    if (!hostRef.current) return
    const term = new Terminal({
      convertEol: true,
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      fontSize: 12,
      theme: { background: '#0d1117', foreground: '#c9d1d9' },
      disableStdin: true,
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.open(hostRef.current)
    fit.fit()
    termRef.current = term

    const onResize = () => fit.fit()
    window.addEventListener('resize', onResize)
    return () => {
      window.removeEventListener('resize', onResize)
      term.dispose()
      termRef.current = null
    }
  }, [])

  useEffect(() => {
    if (!streamUrl) return
    const term = termRef.current
    if (!term) return
    term.writeln(`\x1b[90m— connecting —\x1b[0m`)
    const es = new EventSource(streamUrl)
    es.onmessage = (e: MessageEvent) => {
      try {
        const ev = JSON.parse(e.data) as { kind?: string; text?: string; data?: { terminal?: boolean } }
        if (ev.kind === 'term' && ev.text) term.write(ev.text)
        if (ev.data?.terminal) {
          term.writeln('\r\n\x1b[90m— stream ended —\x1b[0m')
          es.close()
        }
      } catch { /* ignore */ }
    }
    es.onerror = () => { void signOutIfSessionInvalid() } // expired token → back to login
    return () => { es.close() }
  }, [streamUrl])

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
        {running ? 'Live output' : 'Console'}{streamUrl ? '' : ' (no stream)'}
      </Typography>
      <Box ref={hostRef} sx={{
        height, bgcolor: '#0d1117', borderRadius: 1, overflow: 'hidden',
        border: '1px solid', borderColor: 'divider',
        '& .xterm': { height: '100%', padding: '8px' },
        '& .xterm-viewport': { overflowY: 'auto !important' },
      }} />
    </Box>
  )
}
