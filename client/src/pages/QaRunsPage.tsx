import { useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Collapse, Drawer, IconButton, Stack, Tab, Table, TableBody, TableCell,
  TableHead, TableRow, Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import CloseIcon from '@mui/icons-material/Close'
import BugReportIcon from '@mui/icons-material/BugReport'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { useQuery } from '@tanstack/react-query'
import PageHeader from '../components/PageHeader'
import StatusChip from '../components/StatusChip'
import EmptyState from '../components/EmptyState'
import { axiosMessage } from '../common/errors'
import { searchWorkItems } from '../api/authorTests'
import type { WorkItemOption } from '../session/types'
import {
  startRun, listRunsPage, getRunDetail, triage, fileBugs,
  type TestRunSummary, type TestRunDetail, type TestFailureDto,
} from '../api/qaRuns'

const IN_FLIGHT: string[] = ['Queued', 'Running']

function fmtDuration(ms: number) {
  if (ms < 1000) return `${ms}ms`
  const s = Math.round(ms / 1000)
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`
}
function fmtDate(s: string | null) { return s ? new Date(s).toLocaleString() : '—' }

// ── Failure row ───────────────────────────────────────────────────────────────
function FailureRow({ f, selected, onToggle }: { f: TestFailureDto; selected: boolean; onToggle: () => void }) {
  const isRealBug = f.classification === 'RealBug' && f.bugDraftJson != null
  return (
    <TableRow hover>
      <TableCell padding="checkbox">
        {isRealBug && !f.bugId && <Checkbox size="small" checked={selected} onChange={onToggle} />}
      </TableCell>
      <TableCell sx={{ maxWidth: 300 }}>
        <Typography variant="body2" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {f.testTitle}
        </Typography>
      </TableCell>
      <TableCell>
        <Chip size="small" label={f.classification} color={f.classification === 'RealBug' ? 'error' : 'default'} variant="outlined" />
      </TableCell>
      <TableCell><Chip size="small" label={f.confidence} /></TableCell>
      <TableCell sx={{ maxWidth: 240 }}>
        <Typography variant="body2" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {f.reason}
        </Typography>
      </TableCell>
      <TableCell>
        {f.bugId ? <Chip size="small" color="error" label={`Bug #${f.bugId}`} /> : '—'}
      </TableCell>
    </TableRow>
  )
}

// ── Run drawer ────────────────────────────────────────────────────────────────
function RunDrawer({ run, onClose }: { run: TestRunSummary; onClose: () => void }) {
  const [tab, setTab] = useState<'failures' | 'impact'>('failures')
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())
  const [story, setStory] = useState<WorkItemOption | null>(null)
  const [storyInput, setStoryInput] = useState('')
  const [storyOpts, setStoryOpts] = useState<WorkItemOption[]>([])
  const [triaging, setTriaging] = useState(false)
  const [filing, setFiling] = useState(false)
  const [error, setError] = useState('')
  const [expanded, setExpanded] = useState(false)

  const detailQ = useQuery({
    queryKey: ['run-detail', run.id],
    queryFn: () => getRunDetail(run.id),
    refetchInterval: (q) => q.state.data && IN_FLIGHT.includes(q.state.data.status) ? 3000 : false,
  })
  const detail: TestRunDetail | undefined = detailQ.data

  function toggleId(id: number) {
    setSelectedIds(prev => { const s = new Set(prev); s.has(id) ? s.delete(id) : s.add(id); return s })
  }
  async function doTriage() {
    setTriaging(true); setError('')
    try { await triage(run.id); detailQ.refetch() }
    catch (err) { setError(axiosMessage(err)) }
    finally { setTriaging(false) }
  }
  async function doFileBugs() {
    if (selectedIds.size === 0) return
    setFiling(true); setError('')
    try { await fileBugs(run.id, { failureIds: [...selectedIds], storyId: story?.id }); detailQ.refetch(); setSelectedIds(new Set()) }
    catch (err) { setError(axiosMessage(err)) }
    finally { setFiling(false) }
  }

  const failures = detail?.failures ?? []
  const realBugPending = failures.filter(f => f.classification === 'RealBug' && f.bugDraftJson != null && !f.bugId)
  const allPendingIds = new Set(realBugPending.map(f => f.id))

  const adoRunUrl = run.adoTestRunId ? `https://dev.azure.com/your-org/_testManagement/runs?runId=${run.adoTestRunId}` : null

  return (
    <Drawer anchor="right" open onClose={onClose} slotProps={{ paper: { sx: { width: 640, maxWidth: '100%' } } }}>
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
        <Box sx={{ p: 2, pb: 0 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <StatusChip value={run.status} />
            <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1 }}>{run.module ?? `Run #${run.id}`}</Typography>
            <IconButton size="small" onClick={onClose}><CloseIcon /></IconButton>
          </Stack>
          <Stack direction="row" spacing={2} flexWrap="wrap" sx={{ mb: 1 }}>
            <Typography variant="caption">Profile: {run.profile}</Typography>
            <Typography variant="caption">Passed: {run.passed}</Typography>
            <Typography variant="caption">Failed: {run.failed}</Typography>
            <Typography variant="caption">Duration: {fmtDuration(run.durationMs)}</Typography>
          </Stack>
          <Stack direction="row" spacing={1} sx={{ mb: 1 }}>
            {adoRunUrl && <Button size="small" startIcon={<OpenInNewIcon />} href={adoRunUrl} target="_blank" rel="noopener">ADO Test Run</Button>}
            {run.reportPath && <Button size="small" startIcon={<OpenInNewIcon />} href={run.reportPath} target="_blank" rel="noopener">Report</Button>}
          </Stack>
          <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ borderBottom: 1, borderColor: 'divider' }}>
            <Tab value="failures" label={`Failures (${failures.length})`} sx={{ textTransform: 'none', minHeight: 36 }} />
            <Tab value="impact" label="Impact" sx={{ textTransform: 'none', minHeight: 36 }} />
          </Tabs>
        </Box>
        <Box sx={{ flex: 1, overflowY: 'auto', p: 2 }}>
          {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

          {tab === 'failures' && (
            <Box>
              {failures.length === 0 && !detailQ.isLoading && (
                <EmptyState icon={<BugReportIcon />} title="No failures" hint="All tests passed." />
              )}
              {failures.some(f => f.classification === 'TestIssue' && f.bugDraftJson == null) && (
                <Button size="small" variant="outlined" sx={{ mb: 2 }} disabled={triaging}
                  startIcon={triaging ? <CircularProgress size={14} /> : undefined} onClick={doTriage}>
                  AI Triage ({failures.filter(f => f.bugDraftJson == null).length})
                </Button>
              )}
              {failures.length > 0 && (
                <Box sx={{ overflowX: 'auto' }}>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell padding="checkbox">
                          <Checkbox size="small" indeterminate={selectedIds.size > 0 && selectedIds.size < allPendingIds.size}
                            checked={allPendingIds.size > 0 && selectedIds.size === allPendingIds.size}
                            onChange={e => setSelectedIds(e.target.checked ? new Set(allPendingIds) : new Set())} />
                        </TableCell>
                        <TableCell>Test</TableCell><TableCell>Class</TableCell><TableCell>Conf</TableCell>
                        <TableCell>Reason</TableCell><TableCell>Bug</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {failures.map(f => <FailureRow key={f.id} f={f} selected={selectedIds.has(f.id)} onToggle={() => toggleId(f.id)} />)}
                    </TableBody>
                  </Table>
                </Box>
              )}
              {selectedIds.size > 0 && (
                <Card variant="outlined" sx={{ mt: 2 }}>
                  <CardContent sx={{ py: 1.5, '&:last-child': { pb: 1.5 } }}>
                    <Stack spacing={1.5}>
                      <Typography variant="body2">{selectedIds.size} bug{selectedIds.size !== 1 ? 's' : ''} selected to file</Typography>
                      <Autocomplete
                        options={storyOpts} getOptionLabel={o => `#${o.id} — ${o.title}`}
                        inputValue={storyInput}
                        onInputChange={(_, v) => { setStoryInput(v); if (v.length >= 2) searchWorkItems(v).then(setStoryOpts).catch(() => {}) }}
                        onChange={(_, v) => setStory(v)} value={story}
                        renderInput={p => <TextField {...p} label="Link to Story (optional)" size="small" />}
                      />
                      <Button variant="contained" color="error" disabled={filing}
                        startIcon={filing ? <CircularProgress size={16} /> : <BugReportIcon />} onClick={doFileBugs}>
                        File {selectedIds.size} Bug{selectedIds.size !== 1 ? 's' : ''}
                      </Button>
                    </Stack>
                  </CardContent>
                </Card>
              )}
            </Box>
          )}

          {tab === 'impact' && (
            <Box>
              {detail?.impactAnalysisJson ? (
                <>
                  <Button size="small" endIcon={<ExpandMoreIcon sx={{ transform: expanded ? 'rotate(180deg)' : 'none' }} />}
                    onClick={() => setExpanded(v => !v)}>AI impact reasoning</Button>
                  <Collapse in={expanded}>
                    <Box component="pre" sx={{ p: 1.5, bgcolor: 'action.hover', borderRadius: 1, fontSize: 12, whiteSpace: 'pre-wrap', mt: 1 }}>
                      {(() => { try { return JSON.stringify(JSON.parse(detail.impactAnalysisJson!), null, 2) } catch { return detail.impactAnalysisJson } })()}
                    </Box>
                  </Collapse>
                </>
              ) : (
                <Typography variant="body2" color="text.secondary">No impact analysis for this run.</Typography>
              )}
            </Box>
          )}
        </Box>
      </Box>
    </Drawer>
  )
}

// ── Page ──────────────────────────────────────────────────────────────────────
export default function QaRunsPage() {
  const [module, setModule]   = useState('')
  const [level, setLevel]     = useState('')
  const [tag, setTag]         = useState('')
  const [profile, setProfile] = useState('infinity')
  const [launching, setLaunching] = useState(false)
  const [launchError, setLaunchError] = useState('')
  const [page, setPage]       = useState(0)
  const [selectedRun, setSelectedRun] = useState<TestRunSummary | null>(null)

  const TAKE = 20
  const runsQ = useQuery({
    queryKey: ['qa-runs', page],
    queryFn: () => listRunsPage(TAKE, page * TAKE),
    refetchInterval: (q) => {
      const items = q.state.data?.items ?? []
      return items.some(r => IN_FLIGHT.includes(r.status)) ? 3000 : false
    },
  })
  const runs = runsQ.data?.items ?? []
  const total = runsQ.data?.total ?? 0

  async function doLaunch() {
    setLaunching(true); setLaunchError('')
    try {
      await startRun({ moduleFilter: module || undefined, levelFilter: level || undefined, tagFilter: tag || undefined, profile })
      runsQ.refetch()
    } catch (err) { setLaunchError(axiosMessage(err)) }
    finally { setLaunching(false) }
  }

  return (
    <Box>
      <PageHeader title="QA Runs" subtitle="Launch and review Playwright test runs" />
      <Box sx={{ px: 3 }}>
        {/* Launcher */}
        <Card variant="outlined" sx={{ mb: 3 }}>
          <CardContent>
            <Typography variant="subtitle2" sx={{ mb: 2 }}>New Run</Typography>
            {launchError && <Alert severity="error" sx={{ mb: 2 }}>{launchError}</Alert>}
            <Stack direction="row" spacing={2} flexWrap="wrap">
              <TextField label="Module filter" size="small" value={module} onChange={e => setModule(e.target.value)} sx={{ minWidth: 160 }} />
              <TextField label="Level filter" size="small" value={level} onChange={e => setLevel(e.target.value)} placeholder="Contract|Endpoint|Workflow" sx={{ minWidth: 160 }} />
              <TextField label="Tag filter" size="small" value={tag} onChange={e => setTag(e.target.value)} sx={{ minWidth: 120 }} />
              <TextField label="Profile" size="small" value={profile} onChange={e => setProfile(e.target.value)} sx={{ minWidth: 100 }} />
              <Button variant="contained" startIcon={launching ? <CircularProgress size={16} /> : <PlayArrowIcon />}
                disabled={launching} onClick={doLaunch}>Run</Button>
            </Stack>
          </CardContent>
        </Card>

        {/* History */}
        {runs.length === 0 && !runsQ.isLoading
          ? <EmptyState icon={<PlayArrowIcon />} title="No runs yet" hint="Launch a run above." />
          : (
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Module</TableCell><TableCell>Profile</TableCell><TableCell>Status</TableCell>
                  <TableCell align="right">Pass</TableCell><TableCell align="right">Fail</TableCell>
                  <TableCell>Duration</TableCell><TableCell>Started</TableCell><TableCell />
                </TableRow>
              </TableHead>
              <TableBody>
                {runs.map(r => (
                  <TableRow key={r.id} hover sx={{ cursor: 'pointer' }} onClick={() => setSelectedRun(r)}>
                    <TableCell>{r.module ?? `#${r.id}`}</TableCell>
                    <TableCell>{r.profile}</TableCell>
                    <TableCell><StatusChip value={r.status} /></TableCell>
                    <TableCell align="right">{r.passed}</TableCell>
                    <TableCell align="right">{r.failed > 0 ? <Chip size="small" label={r.failed} color="error" /> : r.failed}</TableCell>
                    <TableCell>{fmtDuration(r.durationMs)}</TableCell>
                    <TableCell>{fmtDate(r.startedAt)}</TableCell>
                    <TableCell>
                      {r.failed > 0 && <Tooltip title="View failures"><BugReportIcon fontSize="small" color="error" /></Tooltip>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {total > TAKE && (
              <Stack direction="row" spacing={1} sx={{ mt: 1, justifyContent: 'center' }}>
                <Button size="small" disabled={page === 0} onClick={() => setPage(p => p - 1)}>Previous</Button>
                <Typography variant="body2" sx={{ alignSelf: 'center' }}>{page + 1} / {Math.ceil(total / TAKE)}</Typography>
                <Button size="small" disabled={(page + 1) * TAKE >= total} onClick={() => setPage(p => p + 1)}>Next</Button>
              </Stack>
            )}
          </Box>
        )}
      </Box>
      {selectedRun && <RunDrawer run={selectedRun} onClose={() => setSelectedRun(null)} />}
    </Box>
  )
}
