import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Chip, CircularProgress, Drawer, FormControlLabel,
  IconButton, List, ListItemButton, ListItemText, MenuItem, Stack, Table, TableBody,
  TableCell, TableHead, TableRow, TextField, Toolbar, Tooltip, Typography,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import RefreshIcon from '@mui/icons-material/Refresh'
import StopCircleIcon from '@mui/icons-material/StopCircle'
import OpenInFullIcon from '@mui/icons-material/OpenInFull'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import ReplayIcon from '@mui/icons-material/Replay'
import CloseIcon from '@mui/icons-material/Close'
import { useQuery } from '@tanstack/react-query'
import StatusChip from '../../components/StatusChip'
import EmptyState from '../../components/EmptyState'
import { axiosMessage } from '../../common/errors'
import { paths } from '../../routes/paths'
import { getRunFacets } from '../../api/qaRuns'
import {
  cancelRun, getInventory, getRun, getSpecContent, listRuns, runReportUrl, runStreamUrl, startRun,
  type ScriptInventoryItem,
} from '../../api/scriptsBoard'
import TermConsole from '../../components/TermConsole'
import AllSelect from './AllSelect'

const IN_FLIGHT = new Set(['Queued', 'Running'])

const RECONCILE_COLOR: Record<string, 'success' | 'warning' | 'error' | 'default' | 'info'> = {
  linked: 'success',
  no_meta: 'warning',
  orphan_disk: 'info',
  orphan_db: 'error',
}

function fmtDuration(ms: number) {
  if (!ms) return '—'
  if (ms < 1000) return `${ms}ms`
  const s = Math.round(ms / 1000)
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`
}

function fileName(p: string) {
  const parts = p.replace(/\\/g, '/').split('/')
  return parts[parts.length - 1] || p
}

const panelSx = {
  border: 1,
  borderColor: 'divider',
  borderRadius: 1,
  bgcolor: 'background.paper',
  overflow: 'hidden',
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
} as const

export default function ScriptsBoardPage({
  fullscreen = false,
  initialModule,
  initialRunId,
}: {
  fullscreen?: boolean
  initialModule?: string
  initialRunId?: number
} = {}) {
  const [module, setModule] = useState(initialModule ?? '')
  const [level, setLevel] = useState('')
  const [type, setType] = useState('')
  const [bucket, setBucket] = useState('')
  const [headed, setHeaded] = useState(false)
  const [profile, setProfile] = useState('default')
  const [workers, setWorkers] = useState('')
  const [selected, setSelected] = useState<Record<string, boolean>>({})
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [runId, setRunId] = useState<number | null>(initialRunId ?? null)
  const [preview, setPreview] = useState<ScriptInventoryItem | null>(null)

  const facetsQ = useQuery({ queryKey: ['qa-run-facets'], queryFn: getRunFacets })
  const adoBase = (facetsQ.data?.adoBaseUrl ?? '').replace(/\/$/, '')
  function adoTcUrl(id: number) {
    return adoBase ? `${adoBase}/_workitems/edit/${id}` : undefined
  }
  useEffect(() => {
    const def = facetsQ.data?.defaultProfile
    if (def && facetsQ.data?.profiles.includes(def)) setProfile(def)
  }, [facetsQ.data?.defaultProfile]) // eslint-disable-line react-hooks/exhaustive-deps

  const inventoryQ = useQuery({
    queryKey: ['scripts-board-inventory'],
    queryFn: getInventory,
    refetchInterval: 30_000,
  })
  const runsQ = useQuery({
    queryKey: ['scripts-board-runs'],
    queryFn: () => listRuns(0, 50),
    refetchInterval: (q) => {
      const items = q.state.data?.items ?? []
      return items.some(r => IN_FLIGHT.has(String(r.status))) ? 2000 : 10_000
    },
  })
  const runDetailQ = useQuery({
    queryKey: ['scripts-board-run', runId],
    queryFn: () => getRun(runId!),
    enabled: runId != null,
    refetchInterval: (q) => {
      const s = q.state.data?.status
      return s && IN_FLIGHT.has(String(s)) ? 1500 : false
    },
  })

  const items = inventoryQ.data?.items ?? []
  const moduleCounts = useMemo(() => {
    const map = new Map<string, number>()
    for (const i of items) {
      if (i.reconcileState === 'orphan_db' || !i.module) continue
      map.set(i.module, (map.get(i.module) ?? 0) + 1)
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [items])

  const filtered = useMemo(() => {
    return items.filter(i => {
      if (module && i.module !== module) return false
      if (level && i.level !== level) return false
      if (type && i.type !== type) return false
      if (bucket && i.bucket !== bucket) return false
      if (i.reconcileState === 'orphan_db') return false
      return true
    })
  }, [items, module, level, type, bucket])

  const selectedPaths = filtered.filter(i => selected[i.specFilePath]).map(i => i.specFilePath)
  const runs = runsQ.data?.items ?? []
  const activeCount = runs.filter(r => IN_FLIGHT.has(String(r.status))).length
  const boardReady = activeCount === 0

  function toggle(path: string) {
    setSelected(m => ({ ...m, [path]: !m[path] }))
  }

  function selectModule(name: string) {
    setModule(prev => (prev === name ? '' : name))
    setSelected({})
  }

  async function runSelection(paths?: string[], forceModule?: string) {
    const specFilePaths = paths?.length ? paths : selectedPaths
    const mod = forceModule !== undefined ? forceModule : module
    if (specFilePaths.length === 0 && !mod && !level && !type && !bucket) {
      setError('Select at least one spec, or set a filter before running.')
      return
    }
    const workersN = workers.trim() ? Number(workers) : undefined
    setBusy(true); setError(null)
    try {
      const { runId: id } = await startRun({
        module: mod || undefined,
        level: level || undefined,
        type: type || undefined,
        bucket: bucket || undefined,
        specFilePaths: specFilePaths.length ? specFilePaths : undefined,
        headed,
        workers: Number.isFinite(workersN) ? workersN : undefined,
        profile: profile || undefined,
      })
      setRunId(id)
      void runsQ.refetch()
    } catch (e) { setError(axiosMessage(e) || 'Failed to start run') }
    finally { setBusy(false) }
  }

  async function reRun(fromId: number) {
    setBusy(true); setError(null)
    try {
      const detail = await getRun(fromId)
      const { runId: id } = await startRun({
        module: detail.module || undefined,
        specFilePaths: detail.specFilePaths?.length ? detail.specFilePaths : undefined,
        headed: detail.headed,
        workers: detail.workers ?? undefined,
        profile: detail.profile || profile || undefined,
      })
      setRunId(id)
      void runsQ.refetch()
    } catch (e) { setError(axiosMessage(e) || 'Re-run failed') }
    finally { setBusy(false) }
  }

  async function doCancel(id: number) {
    try { await cancelRun(id); void runsQ.refetch(); void runDetailQ.refetch() }
    catch (e) { setError(axiosMessage(e) || 'Cancel failed') }
  }

  function openFullscreen() {
    const qs = new URLSearchParams()
    if (module) qs.set('module', module)
    if (runId != null) qs.set('runId', String(runId))
    const q = qs.toString()
    const url = `${paths.scriptsBoardFullscreen}${q ? `?${q}` : ''}`
    window.open(url, 'scripts-board', 'noopener,noreferrer')
  }

  return (
    <Box sx={{
      display: 'flex', flexDirection: 'column', gap: 1.5,
      height: fullscreen
        ? '100%'
        : { md: 'calc(100vh - 88px)' },
      minHeight: fullscreen ? 0 : 560,
    }}>
      {/* Header */}
      <Stack direction="row" spacing={1.5} useFlexGap
        sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Chip
          size="small"
          label={boardReady ? 'Ready' : `Running · ${activeCount}`}
          color={boardReady ? 'success' : 'warning'}
          variant="outlined"
        />
        <Typography variant="body2" color="text.secondary">
          {filtered.length} / {inventoryQ.data?.total ?? 0} specs
          {runsQ.data ? ` · ${runsQ.data.total} runs` : ''}
        </Typography>
        <Box sx={{ flex: 1 }} />
        {!fullscreen && (
          <Tooltip title="Open in isolated popup (no app chrome)">
            <Button size="small" startIcon={<OpenInFullIcon />} onClick={openFullscreen}>
              Open fullscreen
            </Button>
          </Tooltip>
        )}
        <Tooltip title="Refresh inventory & runs">
          <IconButton size="small" onClick={() => { void inventoryQ.refetch(); void runsQ.refetch() }}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>

      {error && (
        <Alert severity="error" onClose={() => setError(null)} sx={{ py: 0 }}>{error}</Alert>
      )}

      {/* Three-column console */}
      <Box sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', md: '220px minmax(0, 1fr) 260px' },
        gap: 1.5,
        flex: 1,
        minHeight: 0,
      }}>
        {/* Modules */}
        <Box sx={panelSx}>
          <Typography variant="overline" sx={{ px: 1.5, pt: 1, color: 'text.secondary' }}>
            Modules
          </Typography>
          <List dense disablePadding sx={{ overflow: 'auto', flex: 1 }}>
            <ListItemButton selected={!module} onClick={() => { setModule(''); setSelected({}) }}>
              <ListItemText
                primary="All modules"
                secondary={`${items.filter(i => i.reconcileState !== 'orphan_db').length} specs`}
              />
            </ListItemButton>
            {moduleCounts.map(([name, count]) => (
              <ListItemButton
                key={name}
                selected={module === name}
                onClick={() => selectModule(name)}
              >
                <ListItemText primary={name} secondary={`${count}`} />
              </ListItemButton>
            ))}
          </List>
        </Box>

        {/* Specs */}
        <Box sx={panelSx}>
          <Stack direction="row" spacing={1} useFlexGap
            sx={{ p: 1.25, flexWrap: 'wrap', borderBottom: 1, borderColor: 'divider' }}>
            <AllSelect
              label="Level"
              value={level}
              onChange={setLevel}
              minWidth={110}
              options={['Contract', 'Endpoint', 'Workflow', 'Ui'].map(l => ({ value: l, label: l }))}
            />
            <AllSelect
              label="Type"
              value={type}
              onChange={setType}
              minWidth={100}
              options={[
                { value: 'api', label: 'api' },
                { value: 'ui', label: 'ui' },
              ]}
            />
            <AllSelect
              label="Bucket"
              value={bucket}
              onChange={setBucket}
              minWidth={110}
              options={['smoke', 'regression', 'e2e'].map(b => ({ value: b, label: b }))}
            />
          </Stack>
          <Box sx={{ overflow: 'auto', flex: 1 }}>
            {inventoryQ.isLoading ? (
              <Box sx={{ p: 2 }}><CircularProgress size={22} /></Box>
            ) : filtered.length === 0 ? (
              <Box sx={{ p: 2 }}>
                <EmptyState title="No specs" hint="Generate API or UI specs in QA Loop, then refresh." />
              </Box>
            ) : (
              <Table size="small" stickyHeader>
                <TableHead>
                  <TableRow>
                    <TableCell padding="checkbox" />
                    <TableCell>Spec</TableCell>
                    <TableCell>Lvl</TableCell>
                    <TableCell>State</TableCell>
                    <TableCell align="right">ADO / Run</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {filtered.map((i: ScriptInventoryItem) => (
                    <TableRow
                      key={i.specFilePath}
                      hover
                      selected={!!selected[i.specFilePath] || preview?.specFilePath === i.specFilePath}
                      onClick={() => setPreview(i)}
                      sx={{ cursor: 'pointer' }}
                    >
                      <TableCell padding="checkbox" onClick={e => e.stopPropagation()}>
                        <Checkbox size="small" checked={!!selected[i.specFilePath]}
                          onChange={() => toggle(i.specFilePath)} />
                      </TableCell>
                      <TableCell sx={{ maxWidth: 240 }}>
                        <Typography variant="body2" title={i.specFilePath}
                          sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {fileName(i.specFilePath)}
                        </Typography>
                      </TableCell>
                      <TableCell>{i.level || '—'}</TableCell>
                      <TableCell>
                        <Chip size="small" label={i.reconcileState}
                          color={RECONCILE_COLOR[i.reconcileState] ?? 'default'} variant="outlined" />
                      </TableCell>
                      <TableCell align="right" onClick={e => e.stopPropagation()}>
                        {i.adoTestCaseId != null && (
                          <Chip
                            size="small"
                            variant="outlined"
                            label={`TC #${i.adoTestCaseId}`}
                            component={adoTcUrl(i.adoTestCaseId) ? 'a' : 'div'}
                            href={adoTcUrl(i.adoTestCaseId)}
                            target="_blank"
                            rel="noopener"
                            clickable={!!adoTcUrl(i.adoTestCaseId)}
                            sx={{ mr: 0.5 }}
                          />
                        )}
                        <Button size="small" onClick={() => runSelection([i.specFilePath])} disabled={busy}>
                          Run
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Box>
        </Box>

        {/* Launch & intel */}
        <Box sx={panelSx}>
          <Typography variant="overline" sx={{ px: 1.5, pt: 1, color: 'text.secondary' }}>
            Launch
          </Typography>
          <Stack spacing={1.25} sx={{ px: 1.5, pb: 1.5 }}>
            <TextField
              select size="small" label="Profile" value={profile}
              onChange={e => setProfile(e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            >
              {(facetsQ.data?.profiles ?? ['default']).map(p => (
                <MenuItem key={p} value={p}>{p}</MenuItem>
              ))}
            </TextField>
            <TextField
              size="small" label="Workers" value={workers}
              onChange={e => setWorkers(e.target.value.replace(/[^\d]/g, ''))}
              placeholder="default"
              slotProps={{ inputLabel: { shrink: true } }}
            />
            <FormControlLabel
              control={<Checkbox size="small" checked={headed} onChange={(_, v) => setHeaded(v)} />}
              label="Headed browser"
            />
            <Button
              variant="contained"
              startIcon={busy ? <CircularProgress size={14} color="inherit" /> : <PlayArrowIcon />}
              disabled={busy}
              onClick={() => runSelection()}
            >
              Run selection{selectedPaths.length ? ` (${selectedPaths.length})` : ''}
            </Button>
            <Button
              variant="outlined"
              disabled={busy || !module}
              onClick={() => runSelection([], module)}
            >
              Run module{module ? ` · ${module}` : ''}
            </Button>
            {runId != null && runDetailQ.data && IN_FLIGHT.has(String(runDetailQ.data.status)) && (
              <Button
                color="warning"
                startIcon={<StopCircleIcon />}
                onClick={() => doCancel(runId)}
              >
                Cancel run #{runId}
              </Button>
            )}
            {runId != null && runDetailQ.data && !IN_FLIGHT.has(String(runDetailQ.data.status)) && (
              <Stack direction="row" spacing={1}>
                <Button
                  size="small"
                  startIcon={<ReplayIcon />}
                  disabled={busy}
                  onClick={() => reRun(runId)}
                >
                  Re-run
                </Button>
                {runDetailQ.data.reportPath && (
                  <Button
                    size="small"
                    startIcon={<OpenInNewIcon />}
                    component="a"
                    href={runReportUrl(runId)}
                    target="_blank"
                    rel="noopener"
                  >
                    Report
                  </Button>
                )}
              </Stack>
            )}
          </Stack>
        </Box>
      </Box>

      {/* Live terminal */}
      <Box sx={{ ...panelSx, height: { xs: 200, md: 220 }, flexShrink: 0 }}>
        <Stack direction="row" sx={{ alignItems: 'center', px: 1.5, py: 0.75, borderBottom: 1, borderColor: 'divider' }}>
          <Typography variant="overline" color="text.secondary" sx={{ flex: 1 }}>
            Live terminal{runId != null ? ` · run #${runId}` : ''}
          </Typography>
          {runDetailQ.data && (
            <StatusChip value={String(runDetailQ.data.status)} kind="status" />
          )}
        </Stack>
        <Box sx={{ flex: 1, minHeight: 0, p: 0.5 }}>
          {runId != null ? (
            <TermConsole
              key={runId}
              streamUrl={runStreamUrl(runId)}
              running={!!runDetailQ.data && IN_FLIGHT.has(String(runDetailQ.data.status))}
            />
          ) : (
            <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
              Select a run from the rail below (or launch) to stream Playwright output here.
            </Typography>
          )}
        </Box>
      </Box>

      {/* Runs rail */}
      <Box sx={{ ...panelSx, height: 148, flexShrink: 0 }}>
        <Typography variant="overline" sx={{ px: 1.5, pt: 0.75, color: 'text.secondary' }}>
          Runs · {runsQ.data?.total ?? 0}
        </Typography>
        <Box sx={{ overflow: 'auto', flex: 1, px: 1, pb: 1 }}>
          {runs.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ px: 0.5 }}>
              No Scripts Board runs yet.
            </Typography>
          ) : (
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Id</TableCell>
                  <TableCell>Status</TableCell>
                  <TableCell>Module</TableCell>
                  <TableCell>Selection</TableCell>
                  <TableCell>P/F/S</TableCell>
                  <TableCell>Duration</TableCell>
                  <TableCell align="right" />
                </TableRow>
              </TableHead>
              <TableBody>
                {runs.map(r => (
                  <TableRow
                    key={r.id}
                    hover
                    selected={runId === r.id}
                    sx={{ cursor: 'pointer' }}
                    onClick={() => setRunId(r.id)}
                  >
                    <TableCell>#{r.id}</TableCell>
                    <TableCell><StatusChip value={String(r.status)} kind="status" /></TableCell>
                    <TableCell>{r.module || '—'}</TableCell>
                    <TableCell sx={{ maxWidth: 220 }}>
                      <Typography variant="body2" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {r.selectionSummary}
                      </Typography>
                    </TableCell>
                    <TableCell>{r.passed}/{r.failed}/{r.skipped}</TableCell>
                    <TableCell>{fmtDuration(r.durationMs)}</TableCell>
                    <TableCell align="right" onClick={e => e.stopPropagation()}>
                      <Stack direction="row" spacing={0.5} sx={{ justifyContent: 'flex-end' }}>
                        {IN_FLIGHT.has(String(r.status)) ? (
                          <IconButton size="small" color="warning" title="Cancel"
                            onClick={() => doCancel(r.id)}>
                            <StopCircleIcon fontSize="small" />
                          </IconButton>
                        ) : (
                          <IconButton size="small" title="Re-run" disabled={busy}
                            onClick={() => reRun(r.id)}>
                            <ReplayIcon fontSize="small" />
                          </IconButton>
                        )}
                        {r.reportPath && (
                          <IconButton size="small" title="Open report"
                            component="a" href={runReportUrl(r.id)} target="_blank" rel="noopener">
                            <OpenInNewIcon fontSize="small" />
                          </IconButton>
                        )}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Box>
      </Box>

      <SpecPreviewDrawer
        item={preview}
        adoBaseUrl={adoBase}
        onClose={() => setPreview(null)}
        onRun={(path) => { void runSelection([path]); setPreview(null) }}
        busy={busy}
      />
    </Box>
  )
}

function SpecPreviewDrawer({
  item, adoBaseUrl, onClose, onRun, busy,
}: {
  item: ScriptInventoryItem | null
  adoBaseUrl: string
  onClose: () => void
  onRun: (path: string) => void
  busy: boolean
}) {
  const previewQ = useQuery({
    queryKey: ['scripts-board-spec', item?.specFilePath ?? ''],
    queryFn: () => getSpecContent(item!.specFilePath),
    enabled: item != null,
  })

  const tcId = item?.adoTestCaseId ?? item?.meta?.tcId ?? null
  const adoUrl = tcId != null && adoBaseUrl
    ? `${adoBaseUrl}/_workitems/edit/${tcId}`
    : undefined

  return (
    <Drawer
      anchor="right"
      open={item != null}
      onClose={onClose}
      slotProps={{ paper: { sx: { width: { xs: '100%', sm: 480, md: 560 } } } }}
    >
      <Toolbar sx={{ gap: 1 }}>
        <Typography variant="subtitle1" sx={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {item ? fileName(item.specFilePath) : 'Spec'}
        </Typography>
        <IconButton onClick={onClose}><CloseIcon /></IconButton>
      </Toolbar>
      {item && (
        <Stack spacing={1.5} sx={{ px: 2, pb: 2, overflow: 'auto', flex: 1 }}>
          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
            <Chip size="small" label={item.reconcileState}
              color={RECONCILE_COLOR[item.reconcileState] ?? 'default'} variant="outlined" />
            {item.module && <Chip size="small" label={item.module} variant="outlined" />}
            {item.level && <Chip size="small" label={item.level} variant="outlined" />}
            {item.type && <Chip size="small" label={item.type} variant="outlined" />}
            {item.bucket && <Chip size="small" label={item.bucket} variant="outlined" />}
            {tcId != null && (
              <Chip
                size="small"
                color="primary"
                variant="outlined"
                label={`ADO TC #${tcId}`}
                component={adoUrl ? 'a' : 'div'}
                href={adoUrl}
                target="_blank"
                rel="noopener"
                clickable={!!adoUrl}
                icon={<OpenInNewIcon />}
              />
            )}
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>
            {item.specFilePath}
          </Typography>
          {item.meta && (
            <Alert severity="info" sx={{ py: 0 }}>
              @qa-meta · hash {item.meta.hash ?? '—'}
              {item.meta.draftId != null ? ` · draft ${item.meta.draftId}` : ''}
            </Alert>
          )}
          <Button size="small" variant="contained" disabled={busy}
            startIcon={<PlayArrowIcon />}
            onClick={() => onRun(item.specFilePath)}
            sx={{ alignSelf: 'flex-start' }}>
            Run this spec
          </Button>
          <Typography variant="overline" color="text.secondary">Source</Typography>
          {previewQ.isLoading ? <CircularProgress size={18} /> : previewQ.isError ? (
            <Alert severity="warning">{axiosMessage(previewQ.error) || 'Could not load spec body.'}</Alert>
          ) : (
            <Box
              component="pre"
              sx={{
                m: 0, p: 1.5, borderRadius: 1, bgcolor: 'action.hover',
                overflow: 'auto', maxHeight: '55vh', fontSize: 12, fontFamily: 'ui-monospace, monospace',
              }}
            >
              {previewQ.data?.code || '(empty)'}
            </Box>
          )}
        </Stack>
      )}
    </Drawer>
  )
}
