import { useState } from 'react'
import {
  Alert, Box, Button, Card, CardContent, Chip, CircularProgress, Divider, IconButton,
  Snackbar, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/DeleteOutlined'
import EditIcon from '@mui/icons-material/EditOutlined'
import SaveIcon from '@mui/icons-material/Save'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { axiosMessage } from '../common/errors'
import {
  deleteConnection, getCurrentProduct, listConnections, testConnection, upsertConnection,
  type EnvironmentConnection,
} from '../api/schema'

export default function DatabaseSettings() {
  const qc = useQueryClient()
  const { data: product } = useQuery({ queryKey: ['schema-product'], queryFn: getCurrentProduct })
  const productId = product?.id
  const { data: connections = [] } = useQuery({
    queryKey: ['schema-connections', productId], queryFn: () => listConnections(productId!), enabled: productId != null,
  })

  const [environment, setEnvironment] = useState('')
  const [server, setServer] = useState('')
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')
  const [database, setDatabase] = useState('')
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [testMsg, setTestMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [toast, setToast] = useState('')

  function reset() {
    setEnvironment(''); setServer(''); setUser(''); setPassword(''); setDatabase('')
    setEditing(false); setTestMsg(null); setError('')
  }
  function startEdit(c: EnvironmentConnection) {
    setEnvironment(c.environment); setServer(c.server ?? ''); setUser(c.user ?? '')
    setDatabase(c.database ?? ''); setPassword(''); setEditing(true); setTestMsg(null); setError('')
  }
  function invalidate() { qc.invalidateQueries({ queryKey: ['schema-connections', productId] }) }

  async function doTest() {
    if (!productId) return
    setBusy(true); setTestMsg(null); setError('')
    try {
      const r = await testConnection(productId, { environment, server, user, password, database })
      setTestMsg({ ok: r.ok, text: r.ok ? 'Connection OK' : (r.error ?? 'Failed') })
    } catch (e) { setError(axiosMessage(e)) }
    finally { setBusy(false) }
  }
  async function doSave() {
    if (!productId) return
    if (!environment.trim() || !server.trim() || !user.trim() || !database.trim()) {
      setError('Environment, Server, User and Database are required.'); return
    }
    setBusy(true); setError('')
    try {
      await upsertConnection(productId, { environment: environment.trim(), server: server.trim(), user: user.trim(), password, database: database.trim() })
      invalidate(); setToast(`Saved ${environment.trim()}`); reset()
    } catch (e) { setError(axiosMessage(e)) }
    finally { setBusy(false) }
  }
  async function doRemove(env: string) {
    if (!productId) return
    setBusy(true); setError('')
    try { await deleteConnection(productId, env); invalidate(); if (env === environment) reset() }
    catch (e) { setError(axiosMessage(e)) }
    finally { setBusy(false) }
  }

  if (!product) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>

  return (
    <Box>
      <Box sx={{ mb: 2.5 }}>
        <Typography variant="h5" sx={{ mb: 0.5 }}>Database</Typography>
        <Typography variant="body2" color="text.secondary">
          Read-only SQL Server connections per environment for <strong>{product.name}</strong> (from the Azure DevOps
          project <strong>{product.adoProject || '—'}</strong>). The Schema Explorer only ever runs SELECT queries —
          use a read-only login. Passwords are stored locally and never returned.
        </Typography>
      </Box>

      <Card sx={{ mb: 2 }}>
        <CardContent sx={{ p: 3 }}>
          <Typography variant="subtitle2" sx={{ mb: 1.5 }}>Environments</Typography>
          {connections.length === 0
            ? <Typography variant="body2" color="text.secondary">None yet — add one below.</Typography>
            : (
              <Stack spacing={0.75}>
                {connections.map(c => (
                  <Stack key={c.environment} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                    <Chip size="small" color="success" label={c.environment} sx={{ minWidth: 96 }} />
                    <Typography variant="caption" color="text.secondary" sx={{ flexGrow: 1, fontFamily: 'monospace' }}>
                      {c.server}{c.database ? ` · ${c.database}` : ''}{c.user ? ` · ${c.user}` : ''}
                    </Typography>
                    <Tooltip title="Edit"><IconButton size="small" disabled={busy} onClick={() => startEdit(c)}><EditIcon fontSize="inherit" /></IconButton></Tooltip>
                    <Tooltip title="Remove"><IconButton size="small" color="error" disabled={busy} onClick={() => doRemove(c.environment)}><DeleteIcon fontSize="inherit" /></IconButton></Tooltip>
                  </Stack>
                ))}
              </Stack>
            )}
        </CardContent>
      </Card>

      <Card>
        <CardContent sx={{ p: 3 }}>
          <Typography variant="subtitle2" sx={{ mb: 1.5 }}>{editing ? `Edit ${environment}` : 'Add environment'}</Typography>
          <Stack spacing={2}>
            {error && <Alert severity="error">{error}</Alert>}
            {testMsg && <Alert severity={testMsg.ok ? 'success' : 'warning'}>{testMsg.text}</Alert>}
            <TextField label="Environment name" size="small" value={environment}
              onChange={e => setEnvironment(e.target.value)} disabled={editing} placeholder="e.g. QA" sx={{ maxWidth: 260 }} />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField label="Server" size="small" sx={{ flex: 1 }} value={server}
                onChange={e => setServer(e.target.value)} placeholder="host,1433" />
              <TextField label="Database" size="small" sx={{ flex: 1 }} value={database}
                onChange={e => setDatabase(e.target.value)} placeholder="MyProduct" />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField label="User" size="small" sx={{ flex: 1 }} value={user}
                onChange={e => setUser(e.target.value)} placeholder="readonly" />
              <TextField label="Password" size="small" type="password" sx={{ flex: 1 }} value={password}
                onChange={e => setPassword(e.target.value)}
                placeholder={editing ? 'leave blank to keep' : ''} />
            </Stack>
            <Divider />
            <Stack direction="row" spacing={1}>
              <Button onClick={doTest} disabled={busy || !server.trim() || !user.trim() || !database.trim()}>Test</Button>
              <Button variant="contained" startIcon={busy ? <CircularProgress size={14} /> : <SaveIcon />}
                onClick={doSave} disabled={busy}>{editing ? 'Update environment' : 'Add environment'}</Button>
              {editing && <Button onClick={reset} disabled={busy}>Cancel</Button>}
            </Stack>
          </Stack>
        </CardContent>
      </Card>

      <Snackbar open={!!toast} autoHideDuration={2500} onClose={() => setToast('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="success" variant="filled" onClose={() => setToast('')}>{toast}</Alert>
      </Snackbar>
    </Box>
  )
}
