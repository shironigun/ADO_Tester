/**
 * Epic/Feature runs (batches): pick an Epic/Feature (expand to its child stories) and/or
 * multi-select individual work items, fan out to one session per story, and watch a story-wise
 * roll-up. Reuses the per-story session pipeline + detail unchanged — this is just the grouping UI.
 * Config-driven, so Author Tests and Find Bugs share it.
 */
import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from 'react-router-dom'
import {
  Alert, AppBar, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Container, IconButton, Snackbar, Stack, TextField, ToggleButton, ToggleButtonGroup, Toolbar,
  Tooltip, Typography,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import CloseIcon from '@mui/icons-material/Close'
import StopIcon from '@mui/icons-material/Stop'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import StatTile from '../components/StatTile'
import StatusChip from '../components/StatusChip'
import EmptyState from '../components/EmptyState'
import { ThinkingShimmer } from '../components/LoadingSkeleton'
import { axiosMessage } from '../common/errors'
import { PROJECT_ID } from '../config/env'
import { searchWorkItems } from '../api/authorTests'
import {
  expandChildren, startBatch, listBatches, getBatchDetail, cancelBatch,
  type BatchChild,
} from '../api/batches'
import type { SessionModuleConfig, WorkItemOption } from './types'

const unitOf = (config: SessionModuleConfig) => (config.key === 'find-bugs' ? 'bug' : 'test')

// ── Start dialog ─────────────────────────────────────────────────────────────
export function EpicRunDialog({ config, onClose, onStarted, onError }: {
  config: SessionModuleConfig
  onClose: () => void
  onStarted: (batchId: number) => void
  onError: (e: unknown, fallback: string) => void
}) {
  const [wiQuery, setWiQuery] = useState('')
  const [wiOptions, setWiOptions] = useState<WorkItemOption[]>([])
  const [items, setItems] = useState<BatchChild[]>([])        // candidate work items (added)
  const [checked, setChecked] = useState<Set<number>>(new Set())
  const [parentId, setParentId] = useState<number | null>(null)
  const [mode, setMode] = useState<'auto' | 'manual'>('auto')
  const [expanding, setExpanding] = useState(false)
  const [starting, setStarting] = useState(false)
  const [note, setNote] = useState('')

  useEffect(() => {
    if (wiQuery.length < 2) { setWiOptions([]); return }
    const t = setTimeout(() => {
      searchWorkItems(wiQuery).then(setWiOptions).catch(() => setWiOptions([]))
    }, 400)
    return () => clearTimeout(t)
  }, [wiQuery])

  const addItems = (next: BatchChild[]) => setItems(prev => {
    const seen = new Set(prev.map(i => i.id))
    const fresh = next.filter(n => !seen.has(n.id))
    // Only auto-check the genuinely new items — re-picking an already-added Epic must NOT re-check
    // items the user deliberately unchecked.
    if (fresh.length) setChecked(c => { const s = new Set(c); fresh.forEach(n => s.add(n.id)); return s })
    return fresh.length ? [...prev, ...fresh] : prev
  })

  async function pick(o: WorkItemOption | null) {
    if (!o) return
    setNote('')
    // Always try to expand: an Epic/Feature (or any parent that has children) pulls in its child
    // stories; a leaf work item has no children and is added on its own. (ADO types vary — some
    // "epics" are typed User Story — so we don't gate on the work-item type.)
    setExpanding(true)
    try {
      const data = await expandChildren(o.id, true)
      if (data.children.length > 0) {
        if (parentId == null) setParentId(o.id)
        addItems(data.children)
        setNote(data.truncated
          ? `Added ${data.children.length} children of #${o.id} (more were truncated).`
          : `Added ${data.children.length} child item(s) of #${o.id}.`)
      } else {
        addItems([{ id: o.id, title: o.title, workItemType: o.workItemType }])
      }
    } catch (e) { onError(e, 'Add failed') } finally { setExpanding(false) }
  }

  const toggle = (id: number) => setChecked(c => { const s = new Set(c); s.has(id) ? s.delete(id) : s.add(id); return s })
  const remove = (id: number) => {
    setItems(prev => prev.filter(i => i.id !== id))
    setChecked(c => { const s = new Set(c); s.delete(id); return s })
  }
  const selectedIds = items.filter(i => checked.has(i.id)).map(i => i.id)

  async function start() {
    if (selectedIds.length === 0) return
    setStarting(true)
    try {
      const data = await startBatch(config.key, PROJECT_ID, {
        parentWorkItemId: parentId, workItemIds: selectedIds,
        ...(config.key === 'find-bugs' ? { mode } : {}),
      })
      if (!data.sessionIds || data.sessionIds.length === 0) {
        // Every child failed to start (e.g. ADO unreachable) — don't navigate to an empty roll-up.
        onError(new Error('No stories could be started — Azure DevOps may be unreachable. Nothing was queued.'),
          'Start epic run failed')
        return
      }
      onClose(); onStarted(data.batchId)
    } catch (e) { onError(e, 'Start epic run failed') } finally { setStarting(false) }
  }

  return (
    <Stack spacing={2} sx={{ pt: 1 }}>
      <Autocomplete options={wiOptions} filterOptions={x => x} value={null} blurOnSelect clearOnBlur
        getOptionLabel={o => `#${o.id} — ${o.title}`} isOptionEqualToValue={(a, b) => a.id === b.id}
        renderOption={(props, o) => (
          <li {...props} key={o.id}>
            <Chip size="small" variant="outlined" label={o.workItemType || '?'} sx={{ mr: 1, height: 18 }} />
            #{o.id} — {o.title}
          </li>
        )}
        onInputChange={(_, v) => setWiQuery(v)} onChange={(_, v) => pick(v)}
        renderInput={p => <TextField {...p} label="Add a ticket to the run" size="small"
          helperText="Pick an Epic/Feature/parent to pull in its child tickets, or add an individual one." />}
        noOptionsText={wiQuery.length < 2 ? 'Type 2+ chars' : 'No results'} />

      {expanding && <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}><CircularProgress size={16} /><Typography variant="caption">Expanding…</Typography></Stack>}
      {note && <Alert severity="info" sx={{ py: 0 }}>{note}</Alert>}

      {items.length > 0 && (
        <Box>
          <Stack direction="row" sx={{ alignItems: 'center', mb: 0.5 }}>
            <Typography variant="overline" color="text.secondary" sx={{ flex: 1 }}>
              {selectedIds.length} of {items.length} selected
            </Typography>
            <Button size="small" onClick={() => setChecked(new Set(items.map(i => i.id)))}>All</Button>
            <Button size="small" onClick={() => setChecked(new Set())}>None</Button>
          </Stack>
          <Stack spacing={0.5} sx={{ maxHeight: 280, overflowY: 'auto', border: 1, borderColor: 'divider', borderRadius: 1, p: 0.5 }}>
            {items.map(i => (
              <Stack key={i.id} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Checkbox size="small" sx={{ p: 0.25 }} checked={checked.has(i.id)} onChange={() => toggle(i.id)} />
                <Chip size="small" variant="outlined" label={i.workItemType || '?'} sx={{ height: 18, flexShrink: 0 }} />
                <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                  title={i.title}>#{i.id} — {i.title}</Typography>
                <IconButton size="small" onClick={() => remove(i.id)} aria-label="remove"><CloseIcon fontSize="small" /></IconButton>
              </Stack>
            ))}
          </Stack>
        </Box>
      )}

      {config.key === 'find-bugs' && (
        <Box>
          <Typography variant="body2" sx={{ mb: 0.5, fontWeight: 500 }}>Run mode</Typography>
          <ToggleButtonGroup fullWidth size="small" exclusive value={mode} onChange={(_, v) => { if (v) setMode(v) }}>
            <ToggleButton value="auto"><Stack sx={{ alignItems: 'flex-start', py: 0.25 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>Automatic</Typography>
              <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>Analyst → Code Verify → Bug Writer per story</Typography>
            </Stack></ToggleButton>
            <ToggleButton value="manual"><Stack sx={{ alignItems: 'flex-start', py: 0.25 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>Manual</Typography>
              <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>Pause each story to verify by hand</Typography>
            </Stack></ToggleButton>
          </ToggleButtonGroup>
        </Box>
      )}

      <Button variant="contained" onClick={start} disabled={starting || selectedIds.length === 0}>
        {starting ? <CircularProgress size={18} sx={{ color: 'white' }} />
          : `Run ${config.key === 'find-bugs' ? 'Find Bugs' : 'Author Tests'} on ${selectedIds.length} ticket${selectedIds.length === 1 ? '' : 's'}`}
      </Button>
    </Stack>
  )
}

// ── Epic runs list (a workspace tab) ─────────────────────────────────────────
const RUNNING = (s: string) => s === 'Running'
// A child is "in flight" only while doing automatic work — AwaitingActual/AwaitingTester are
// parked-for-manual states, not running, so they must not count toward the running tally.
const IN_FLIGHT = (s: string) => !['Done', 'Drafted', 'Failed', 'Cancelled', 'AwaitingActual', 'AwaitingTester'].includes(s)
// Derived-status → MUI Chip color. AwaitingInput (all automatic work done, parked for manual
// verification) is its own state so a parked Manual batch never reads as a green "Completed".
const statusColor = (s: string): 'warning' | 'info' | 'error' | 'success' | 'default' =>
  ({ Running: 'warning', AwaitingInput: 'info', CompletedWithFailures: 'error', Completed: 'success' } as const)[s] ?? 'default'
const borderColor = (s: string): string =>
  ({ Running: 'warning.main', AwaitingInput: 'info.main', CompletedWithFailures: 'error.main', Completed: 'success.main' } as Record<string, string>)[s] ?? 'divider'

export function EpicRunsTab({ config }: { config: SessionModuleConfig }) {
  const navigate = useNavigate()

  // Batches for this module, kept live via React Query (F2). Polls every 3s only while one is
  // Running. The endpoint returns all modules' batches; we filter to this module client-side.
  const batchesQuery = useQuery({
    queryKey: ['batches', config.key],
    queryFn: () => listBatches(PROJECT_ID, 100),
    refetchInterval: (q) =>
      (q.state.data?.items ?? []).some(b => b.module === config.key && RUNNING(b.derivedStatus))
        ? 3000 : false,
  })
  const batches = (batchesQuery.data?.items ?? []).filter(b => b.module === config.key)
  const loaded = !batchesQuery.isLoading

  if (loaded && batches.length === 0) {
    return <EmptyState icon={<AccountTreeIcon />} title="No epic runs yet"
      hint={`Click "Epic run" on the List tab to test a whole Epic/Feature at once.`} />
  }
  return (
    <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', md: 'repeat(3, 1fr)' } }}>
      {batches.map(b => (
        <Card key={b.id} variant="outlined" onClick={() => navigate(`${config.route}/epic/${b.id}`)}
          sx={{ cursor: 'pointer', borderLeft: '4px solid', borderLeftColor: borderColor(b.derivedStatus),
                transition: 'transform .15s, box-shadow .15s', '&:hover': { transform: 'translateY(-2px)', boxShadow: 3 } }}>
          <CardContent sx={{ p: 1.75, '&:last-child': { pb: 1.75 } }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
              <AccountTreeIcon fontSize="small" color="action" />
              <Typography variant="body2" sx={{ fontWeight: 600, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={b.title}>{b.title}</Typography>
            </Stack>
            <Typography variant="caption" color="text.secondary">
              {b.childCount} stories · {b.doneCount} done{b.failedCount ? ` · ${b.failedCount} failed` : ''}{b.runningCount ? ` · ${b.runningCount} running` : ''}
            </Typography>
            <Box sx={{ mt: 1 }}><Chip size="small" label={b.derivedStatus} color={statusColor(b.derivedStatus)} variant="outlined" /></Box>
          </CardContent>
        </Card>
      ))}
    </Box>
  )
}

// ── Roll-up full-screen view ─────────────────────────────────────────────────
export default function EpicRunDetailFullScreen({ config }: { config: SessionModuleConfig }) {
  const { batchId } = useParams()
  const navigate = useNavigate()
  const bid = batchId ? Number(batchId) : null
  const [actionError, setActionError] = useState('')
  const unit = unitOf(config)

  // Roll-up detail, kept live via React Query (F2). Polls every 2.5s only while Running.
  const detailQuery = useQuery({
    queryKey: ['batch-detail', bid],
    queryFn: () => getBatchDetail(bid as number),
    enabled: bid != null,
    refetchInterval: (q) => (q.state.data?.derivedStatus === 'Running' ? 2500 : false),
  })
  const detail = detailQuery.data ?? null

  const cancel = async () => {
    if (bid == null) return
    try { await cancelBatch(bid); void detailQuery.refetch() }
    catch (e) { setActionError(`Cancel failed: ${axiosMessage(e)}`) }
  }

  // A parked Manual batch settles at "AwaitingInput": cancellable (Fix B makes it terminate parked
  // children) but no longer polled, since nothing advances without per-story manual action.
  const cancellable = detail != null && ['Running', 'AwaitingInput'].includes(detail.derivedStatus)

  const children = detail?.children ?? []
  const total = children.length
  const done = children.filter(c => c.status === 'Done' || c.status === 'Drafted').length
  const failed = children.filter(c => c.status === 'Failed').length
  const awaiting = children.filter(c => c.status === 'AwaitingActual' || c.status === 'AwaitingTester').length
  const runningCount = children.filter(c => IN_FLIGHT(c.status)).length
  const produced = children.reduce((acc, c) => acc + (c.count || 0), 0)

  const tiles = [
    { label: 'Stories', value: total, color: 'primary.main' },
    { label: 'Done', value: done, color: 'success.main' },
    { label: 'Running', value: runningCount, color: 'warning.main' },
    ...(awaiting > 0 ? [{ label: 'Awaiting', value: awaiting, color: 'info.main' }] : []),
    { label: 'Failed', value: failed, color: 'error.main' },
    { label: unit === 'bug' ? 'Bugs' : 'Tests', value: produced, color: 'info.main' },
  ]

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="sticky" color="default" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider' }}>
        <Toolbar>
          <Button startIcon={<ArrowBackIcon />} onClick={() => navigate(config.route)} sx={{ textTransform: 'none', mr: 2 }}>Back</Button>
          <AccountTreeIcon fontSize="small" color="action" sx={{ mr: 1 }} />
          <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {detail?.title ?? 'Epic run'}
          </Typography>
          {detail && <Chip size="small" sx={{ mr: 1 }} label={detail.derivedStatus}
            color={statusColor(detail.derivedStatus)} variant="outlined" />}
          {cancellable && <Button color="warning" size="small" startIcon={<StopIcon />} onClick={cancel}>Cancel all</Button>}
        </Toolbar>
      </AppBar>

      <Container maxWidth="lg" sx={{ py: 3 }}>
        {!detail ? (
          <ThinkingShimmer label="Loading epic run…" />
        ) : (
          <>
            <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(104px, 1fr))', gap: 1.5, mb: 3 }}>
              {tiles.map(t => <StatTile key={t.label} icon={<AccountTreeIcon />} label={t.label} value={t.value} color={t.color} />)}
            </Box>
            <Typography variant="overline" color="text.secondary">Stories ({total})</Typography>
            <Stack spacing={1} sx={{ mt: 0.5 }}>
              {children.map(c => (
                <Card key={c.sessionId} variant="outlined">
                  <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
                    <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5 }}>
                      <StatusChip value={c.status} />
                      <Typography variant="body2" sx={{ fontWeight: 500, flex: 1, minWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                        title={c.workItemTitle}>#{c.workItemId} — {c.workItemTitle}</Typography>
                      {c.count > 0 && <Chip size="small" variant="outlined" label={`${c.count} ${unit}${c.count === 1 ? '' : 's'}`} />}
                      <Tooltip title="Open this story">
                        <Button size="small" variant="outlined" endIcon={<OpenInNewIcon />}
                          onClick={() => navigate(`${config.route}/${c.sessionId}`)}>Open</Button>
                      </Tooltip>
                    </Stack>
                    {c.errorMessage && <Typography variant="caption" color="error.main" sx={{ display: 'block', mt: 0.5 }}>{c.errorMessage}</Typography>}
                  </CardContent>
                </Card>
              ))}
            </Stack>
          </>
        )}
      </Container>

      <Snackbar open={!!actionError} autoHideDuration={6000} onClose={() => setActionError('')} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="error" onClose={() => setActionError('')} variant="filled">{actionError}</Alert>
      </Snackbar>
    </Box>
  )
}
