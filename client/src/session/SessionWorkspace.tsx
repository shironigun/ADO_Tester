import { useEffect, useState, type ReactElement } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert, Box, Button, Card, CardContent, Chip, Dialog, DialogContent, DialogTitle,
  Drawer, IconButton, Snackbar, Stack, Tab, Table, TableBody, TableCell, TableHead,
  TableRow, Tabs, Tooltip, Typography, useMediaQuery, useTheme,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import CloseIcon from '@mui/icons-material/Close'
import OpenInFullIcon from '@mui/icons-material/OpenInFull'
import PageHeader from '../components/PageHeader'
import StatusChip from '../components/StatusChip'
import EmptyState from '../components/EmptyState'
import LiveActivityIndicator from '../components/LiveActivityIndicator'
import RuleIcon from '@mui/icons-material/Rule'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import { axiosMessage, SessionTimeline, useSessionDetail, useSessionsLive } from './shared'
import { EpicRunDialog, EpicRunsTab } from './epicRuns'
import type { SessionDetail, SessionModuleConfig, SessionSummary } from './types'

const PAGE_SIZE = 17

function fmtDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' }) } catch { return '' }
}

// ── A single session card ────────────────────────────────────────────────────
function SessionCard({ summary, config, onOpenPanel, onOpenFull }: {
  summary: SessionSummary; config: SessionModuleConfig
  onOpenPanel: () => void; onOpenFull: () => void
}) {
  const live = config.inFlightStatuses.includes(summary.status)
  const accent =
    summary.status === 'Failed' ? 'error.main'
    : summary.status === 'Done' ? 'success.main'
    : live ? 'warning.main' : 'primary.main'
  return (
    <Card variant="outlined" onClick={onOpenPanel}
      sx={{ cursor: 'pointer', position: 'relative', overflow: 'hidden', height: '100%',
            borderLeft: '4px solid', borderLeftColor: accent, borderRadius: 2,
            transition: 'transform .15s, box-shadow .15s, border-color .15s',
            '&:hover': { transform: 'translateY(-2px)', boxShadow: 3 } }}>
      <CardContent sx={{ p: 1.75, '&:last-child': { pb: 1.75 } }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.75 }}>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>#{summary.workItemId}</Typography>
          <Box sx={{ flex: 1 }} />
          <Chip size="small" variant="outlined" icon={config.moduleChip.icon as ReactElement} label={config.moduleChip.label} sx={{ '& .MuiChip-label': { fontSize: 10, fontWeight: 600 } }} />
          <Tooltip title="Open full screen">
            <IconButton size="small" onClick={e => { e.stopPropagation(); onOpenFull() }}><OpenInFullIcon sx={{ fontSize: 16 }} /></IconButton>
          </Tooltip>
        </Stack>
        <Typography variant="body2" sx={{ fontWeight: 500, mb: 1, minHeight: 40, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
          {summary.workItemTitle}
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <StatusChip value={summary.status} />
          <Box sx={{ flex: 1 }} />
          <Typography variant="caption" color="text.secondary">{fmtDate(summary.createdAt)}</Typography>
        </Stack>
        {live && <Box sx={{ mt: 1 }}><LiveActivityIndicator active text="Processing…" /></Box>}
      </CardContent>
    </Card>
  )
}

function NewSessionCard({ onClick, config }: { onClick: () => void; config: SessionModuleConfig }) {
  return (
    <Card onClick={onClick}
      sx={{ cursor: 'pointer', height: '100%', minHeight: 132, border: '2px dashed', borderColor: 'divider',
            bgcolor: 'transparent', display: 'flex', alignItems: 'center', justifyContent: 'center',
            transition: 'border-color .15s, background-color .15s',
            '&:hover': { borderColor: 'primary.main', bgcolor: 'action.hover' } }}>
      <Stack sx={{ alignItems: 'center' }} spacing={0.5}>
        <AddIcon color="primary" />
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{config.labels.newCta}</Typography>
      </Stack>
    </Card>
  )
}

// ── Right detail drawer (Detail / Timeline) ──────────────────────────────────
function SessionDetailPanel({ config, sessionId, onClose, onError, onTerminal, onOpenFull }: {
  config: SessionModuleConfig; sessionId: number | null
  onClose: () => void; onError: (e: unknown, f: string) => void
  onTerminal: () => void; onOpenFull: (id: number) => void
}) {
  const theme = useTheme()
  const fullWidth = useMediaQuery(theme.breakpoints.down('sm'))
  const { session, polling, refetch } = useSessionDetail(config, sessionId, onTerminal)
  const [tab, setTab] = useState<'detail' | 'timeline'>('detail')
  useEffect(() => { setTab('detail') }, [sessionId])

  const live = polling || (session != null && config.inFlightStatuses.includes(session.status))
  const Body = config.DetailBody

  return (
    <Drawer anchor="right" open={sessionId != null} onClose={onClose}
      sx={{ zIndex: t => t.zIndex.drawer + 2 }}
      slotProps={{ paper: { sx: { width: fullWidth ? '100%' : 600, maxWidth: '100%' } } }}>
      {session && (
        <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
          <Box sx={{ p: 2, pb: 0 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                #{session.workItemId} — {session.workItemTitle}
              </Typography>
              <Tooltip title="Open full screen"><IconButton size="small" onClick={() => onOpenFull(session.id)}><OpenInFullIcon fontSize="small" /></IconButton></Tooltip>
              <IconButton size="small" onClick={onClose} aria-label="close"><CloseIcon /></IconButton>
            </Stack>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mt: 0.5 }}>
              <StatusChip value={session.status} />
            </Stack>
            <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mt: 1, minHeight: 40 }}>
              <Tab value="detail" label="Detail" sx={{ minHeight: 40, textTransform: 'none' }} />
              <Tab value="timeline" label="Timeline" sx={{ minHeight: 40, textTransform: 'none' }} />
            </Tabs>
          </Box>
          <Box sx={{ flex: 1, overflowY: 'auto', p: 2 }}>
            {tab === 'detail'
              ? <Body session={session} refetch={refetch} onError={onError} />
              : <SessionTimeline config={config} session={session} live={!!live} />}
          </Box>
        </Box>
      )}
    </Drawer>
  )
}

// ── Analytics table ──────────────────────────────────────────────────────────
function AnalyticsTable({ config, summaries }: { config: SessionModuleConfig; summaries: SessionSummary[] }) {
  const [cache, setCache] = useState<Record<number, SessionDetail>>({})
  useEffect(() => {
    let cancelled = false
    const missing = summaries.filter(s => !cache[s.id]).slice(0, 17)
    missing.forEach(async s => {
      try { const d = await config.getDetail(s.id); if (!cancelled) setCache(prev => ({ ...prev, [s.id]: d })) } catch { /* ignore */ }
    })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [summaries, config])

  if (summaries.length === 0) return <EmptyState icon={<RuleIcon />} title="No sessions yet" hint="Start one from the List tab." />

  return (
    <Box sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Title</TableCell>
            <TableCell>Created</TableCell>
            <TableCell>Status</TableCell>
            {config.analyticsColumns.map(c => <TableCell key={c.key} align={c.align ?? 'left'}>{c.label}</TableCell>)}
          </TableRow>
        </TableHead>
        <TableBody>
          {summaries.map(s => {
            const d = cache[s.id]
            return (
              <TableRow key={s.id} hover>
                <TableCell sx={{ maxWidth: 320 }}>
                  <Typography variant="body2" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    #{s.workItemId} — {s.workItemTitle}
                  </Typography>
                </TableCell>
                <TableCell>{fmtDate(s.createdAt)}</TableCell>
                <TableCell><StatusChip value={s.status} /></TableCell>
                {config.analyticsColumns.map(c => <TableCell key={c.key} align={c.align ?? 'left'}>{c.value(d)}</TableCell>)}
              </TableRow>
            )
          })}
        </TableBody>
      </Table>
    </Box>
  )
}

// ── The workspace ────────────────────────────────────────────────────────────
export default function SessionWorkspace({ config }: { config: SessionModuleConfig }) {
  const navigate = useNavigate()
  const [page, setPage] = useState(0)
  const { summaries, total, loading, refresh } = useSessionsLive(config, PAGE_SIZE, page * PAGE_SIZE)
  const [tab, setTab] = useState<'list' | 'epic' | 'analytics'>('list')
  const [panelId, setPanelId] = useState<number | null>(null)
  const [newOpen, setNewOpen] = useState(false)
  const [epicOpen, setEpicOpen] = useState(false)
  const [actionError, setActionError] = useState('')

  const onError = (e: unknown, fallback: string) => setActionError(`${fallback}: ${axiosMessage(e)}`)
  const StartForm = config.StartForm

  const lastPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1)
  const from = total === 0 ? 0 : page * PAGE_SIZE + 1
  const to = Math.min(total, page * PAGE_SIZE + summaries.length)

  return (
    <Box>
      <PageHeader title={config.labels.title} />

      <Stack direction="row" sx={{ alignItems: 'center', borderBottom: 1, borderColor: 'divider', mb: 2 }}>
        <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ flex: 1 }}>
          <Tab value="list" label="List" sx={{ textTransform: 'none', fontWeight: 600 }} />
          <Tab value="epic" label="Epic runs" sx={{ textTransform: 'none', fontWeight: 600 }} />
          <Tab value="analytics" label="Analytics" sx={{ textTransform: 'none', fontWeight: 600 }} />
        </Tabs>
        <Button size="small" variant="outlined" startIcon={<AccountTreeIcon />} onClick={() => setEpicOpen(true)} sx={{ ml: 1, textTransform: 'none' }}>
          Epic run
        </Button>
      </Stack>

      {tab === 'list' && (
        <>
          <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', md: 'repeat(3, 1fr)', lg: 'repeat(4, 1fr)' } }}>
            <NewSessionCard config={config} onClick={() => setNewOpen(true)} />
            {summaries.map(s => (
              <SessionCard key={s.id} summary={s} config={config}
                onOpenPanel={() => setPanelId(s.id)}
                onOpenFull={() => navigate(`${config.route}/${s.id}`)} />
            ))}
          </Box>
          {!loading && total === 0 && (
            <Box sx={{ mt: 3 }}><EmptyState icon={config.moduleChip.icon} title="No sessions yet" hint={`Click "${config.labels.newCta}" to start.`} /></Box>
          )}
          {total > PAGE_SIZE && (
            <Stack direction="row" spacing={2} sx={{ mt: 3, alignItems: 'center', justifyContent: 'flex-end' }}>
              <Typography variant="caption" color="text.secondary">{from}–{to} of {total}</Typography>
              <Button size="small" disabled={page === 0} onClick={() => setPage(p => Math.max(0, p - 1))}>‹ Prev</Button>
              <Button size="small" disabled={page >= lastPage} onClick={() => setPage(p => Math.min(lastPage, p + 1))}>Next ›</Button>
            </Stack>
          )}
        </>
      )}
      {tab === 'epic' && <EpicRunsTab config={config} />}
      {tab === 'analytics' && <AnalyticsTable config={config} summaries={summaries} />}

      <SessionDetailPanel config={config} sessionId={panelId}
        onClose={() => { setPanelId(null); refresh() }}
        onError={onError} onTerminal={refresh}
        onOpenFull={id => { setPanelId(null); navigate(`${config.route}/${id}`) }} />

      <Dialog open={newOpen} onClose={() => setNewOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <Typography variant="h6" component="span" sx={{ fontWeight: 600, flex: 1 }}>{config.labels.newCta}</Typography>
          <IconButton size="small" onClick={() => setNewOpen(false)} aria-label="close"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent>
          <StartForm onClose={() => setNewOpen(false)} onError={onError}
            onStarted={id => { refresh(); setPanelId(id) }} />
        </DialogContent>
      </Dialog>

      <Dialog open={epicOpen} onClose={() => setEpicOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <Typography variant="h6" component="span" sx={{ fontWeight: 600, flex: 1 }}>Epic / Feature run</Typography>
          <IconButton size="small" onClick={() => setEpicOpen(false)} aria-label="close"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent>
          <EpicRunDialog config={config} onClose={() => setEpicOpen(false)} onError={onError}
            onStarted={batchId => { setEpicOpen(false); navigate(`${config.route}/epic/${batchId}`) }} />
        </DialogContent>
      </Dialog>

      <Snackbar open={!!actionError} autoHideDuration={6000} onClose={() => setActionError('')} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="error" onClose={() => setActionError('')} variant="filled">{actionError}</Alert>
      </Snackbar>
    </Box>
  )
}
