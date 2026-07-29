import { useCallback, useEffect, useState } from 'react'
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, Link, Stack, TextField, Typography,
} from '@mui/material'
import OpenInNewIcon from '@mui/icons-material/OpenInNewOutlined'
import {
  getClaudeAuthStatus, startClaudeLogin, completeClaudeLogin, claudeLogout,
  type ClaudeAuthStatus,
} from '../api/claudeAuth'

function errText(e: unknown): string {
  const r = e as { response?: { data?: { detail?: string } }; message?: string }
  return r?.response?.data?.detail ?? r?.message ?? 'Something went wrong.'
}

/**
 * Sign the local `claude` CLI in/out from the browser — no `docker exec` needed. Drop-in for the
 * Claude CLI provider card in Settings. Login opens the Anthropic OAuth page in a new tab; the user
 * pastes the returned code back here and the backend completes the exchange.
 */
export default function ClaudeAuthControls() {
  const [status, setStatus] = useState<ClaudeAuthStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [loggingOut, setLoggingOut] = useState(false)

  const [open, setOpen] = useState(false)
  const [starting, setStarting] = useState(false)
  const [loginId, setLoginId] = useState('')
  const [url, setUrl] = useState('')
  const [code, setCode] = useState('')
  const [completing, setCompleting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      setStatus(await getClaudeAuthStatus())
    } catch (e) {
      setStatus({ available: false, loggedIn: false, error: errText(e) })
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])

  const openLogin = useCallback(async () => {
    setOpen(true)
    setError(null)
    setCode('')
    setUrl('')
    setLoginId('')
    setStarting(true)
    try {
      const s = await startClaudeLogin()
      setLoginId(s.loginId)
      setUrl(s.url)
    } catch (e) {
      setError(errText(e))
    } finally {
      setStarting(false)
    }
  }, [])

  const submit = useCallback(async () => {
    setCompleting(true)
    setError(null)
    try {
      const res = await completeClaudeLogin(loginId, code.trim())
      if (res.success) {
        if (res.status) setStatus(res.status)
        else await refresh()
        setOpen(false)
      } else {
        setError(res.error ?? 'Sign-in failed.')
        if (res.status) setStatus(res.status)
      }
    } catch (e) {
      setError(errText(e))
    } finally {
      setCompleting(false)
    }
  }, [loginId, code, refresh])

  const signOut = useCallback(async () => {
    setLoggingOut(true)
    try {
      setStatus(await claudeLogout())
    } catch (e) {
      setStatus({ available: true, loggedIn: false, error: errText(e) })
    } finally {
      setLoggingOut(false)
    }
  }, [])

  return (
    <Box sx={{ p: 1.25, borderRadius: 1.5, bgcolor: 'action.hover' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.75 }}>
        <Typography variant="caption" sx={{ fontWeight: 700 }}>Subscription sign-in</Typography>
        {loading ? (
          <CircularProgress size={13} />
        ) : status?.loggedIn ? (
          <Chip size="small" color="success" label="Signed in" sx={{ height: 18, fontSize: 10 }} />
        ) : (
          <Chip size="small" color="warning" variant="outlined" label="Not signed in" sx={{ height: 18, fontSize: 10 }} />
        )}
      </Stack>

      {!loading && status?.loggedIn && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
          {status.email}{status.orgName ? ` · ${status.orgName}` : ''}
          {status.subscriptionType ? ` (${status.subscriptionType})` : ''}
        </Typography>
      )}
      {!loading && !status?.loggedIn && status?.available && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
          Sign in with your Claude Pro/Max subscription — no API key. Opens the Anthropic page in a new tab.
        </Typography>
      )}
      {!loading && status && !status.available && (
        <Typography variant="caption" color="error" sx={{ display: 'block', mb: 1 }}>
          Claude CLI not detected in the backend. {status.error}
        </Typography>
      )}

      <Stack direction="row" spacing={1}>
        {status?.loggedIn ? (
          <Button size="small" variant="outlined" color="inherit" onClick={signOut} disabled={loggingOut}>
            {loggingOut ? 'Signing out…' : 'Sign out'}
          </Button>
        ) : (
          <Button size="small" variant="contained" onClick={openLogin} disabled={!status?.available}>
            Sign in
          </Button>
        )}
      </Stack>

      <Dialog open={open} onClose={() => !completing && setOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Sign in to Claude</DialogTitle>
        <DialogContent dividers>
          {starting ? (
            <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', py: 2 }}>
              <CircularProgress size={18} />
              <Typography variant="body2">Starting sign-in…</Typography>
            </Stack>
          ) : (
            <Stack spacing={2}>
              {error && <Alert severity="error">{error}</Alert>}
              {url && (
                <>
                  <Stack spacing={0.5}>
                    <Typography variant="body2">
                      <b>1.</b> Open the Anthropic sign-in page and authorize:
                    </Typography>
                    <Link href={url} target="_blank" rel="noopener noreferrer"
                      sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, wordBreak: 'break-all' }}>
                      Open Claude sign-in <OpenInNewIcon sx={{ fontSize: 14 }} />
                    </Link>
                  </Stack>
                  <Divider />
                  <Stack spacing={1}>
                    <Typography variant="body2">
                      <b>2.</b> Copy the code shown after you authorize and paste it here:
                    </Typography>
                    <TextField
                      label="Authorization code" size="small" fullWidth value={code}
                      onChange={e => setCode(e.target.value)} autoFocus
                      onKeyDown={e => { if (e.key === 'Enter' && code.trim() && !completing) void submit() }} />
                  </Stack>
                </>
              )}
            </Stack>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)} disabled={completing}>Cancel</Button>
          <Button variant="contained" onClick={submit} disabled={starting || completing || !code.trim()}>
            {completing ? 'Completing…' : 'Complete sign-in'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}
