import { useEffect, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Collapse, Dialog, DialogActions, DialogContent, DialogTitle, Drawer, IconButton, LinearProgress,
  MenuItem, Stack, Tab, Table, TableBody, TableCell,
  TableHead, TableRow, Tabs, TextField, Toolbar, Tooltip, Typography,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import CloseIcon from '@mui/icons-material/Close'
import BugReportIcon from '@mui/icons-material/BugReport'
import DownloadIcon from '@mui/icons-material/Download'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import OpenInFullIcon from '@mui/icons-material/OpenInFull'
import CloseFullscreenIcon from '@mui/icons-material/CloseFullscreen'
import HourglassEmptyIcon from '@mui/icons-material/HourglassEmpty'
import AgentConsole from '../components/AgentConsole'
import { useQuery } from '@tanstack/react-query'
import PageHeader from '../components/PageHeader'
import StatusChip from '../components/StatusChip'
import EmptyState from '../components/EmptyState'
import { axiosMessage } from '../common/errors'
import { searchWorkItems } from '../api/authorTests'
import type { WorkItemOption } from '../session/types'
import type { ConsoleEvent } from '../hooks/useEventStream'
import {
  startRun, listRunsPage, getRunDetail, triage, fileBugs, runStreamUrl, getRunFacets, getRunImpact,
  type TestRunSummary, type TestRunDetail, type TestFailureDto, type RunImpactTest,
} from '../api/qaRuns'
import { PROJECT_ID } from '../config/env'

const IN_FLIGHT: string[] = ['Queued', 'Running']

function parseEventLog(json: string | null | undefined): ConsoleEvent[] {
  if (!json) return []
  try {
    const arr = JSON.parse(json)
    return Array.isArray(arr) ? (arr as ConsoleEvent[]) : []
  } catch {
    return []
  }
}

function fmtDuration(ms: number) {
  if (ms < 1000) return `${ms}ms`
  const s = Math.round(ms / 1000)
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`
}
function fmtDate(s: string | null) { return s ? new Date(s).toLocaleString() : '—' }

// Strip the "[chromium] › file:line › describe › " scaffolding + the [<tcId-pending>] placeholder.
function cleanTitle(t: string): string {
  const parts = t.split('\u203a')
  let last = (parts[parts.length - 1] ?? t).trim()
  last = last.replace(/\[<tcId-pending(?::\d+)?>\]/g, '').replace(/^\[\d+\]\s*/, '').trim()
  return last || t.trim()
}

interface BugDraft {
  Title: string; Severity: string; Preconditions?: string
  ReproSteps?: string[]; Expected?: string; Observations?: string[]
  FoundBy?: string; AutomatedTest?: string; AdoTestCaseId?: number | null
}
function parseBugDraft(json?: string | null): BugDraft | null {
  if (!json) return null
  try { return JSON.parse(json) as BugDraft } catch { return null }
}

// ── Failure row (expandable — shows the full failure reason) ────────────────────
function FailureRow({ f }: { f: TestFailureDto }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <TableRow hover sx={{ '& > td': { borderBottom: open ? 'unset' : undefined } }}>
        <TableCell sx={{ maxWidth: 360 }}>
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
            <IconButton size="small" onClick={() => setOpen(o => !o)}>
              <ExpandMoreIcon sx={{ fontSize: 18, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform .15s' }} />
            </IconButton>
            <Typography variant="body2" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {cleanTitle(f.testTitle)}
            </Typography>
          </Stack>
        </TableCell>
        <TableCell><Chip size="small" label={f.classification} color={f.classification === 'RealBug' ? 'error' : 'default'} variant="outlined" /></TableCell>
        <TableCell>{f.adoTestCaseId ? <Chip size="small" variant="outlined" label={`TC #${f.adoTestCaseId}`} /> : '—'}</TableCell>
        <TableCell>{f.bugId ? <Chip size="small" color="error" label={`Bug #${f.bugId}`} /> : '—'}</TableCell>
      </TableRow>
      <TableRow>
        <TableCell colSpan={4} sx={{ py: 0, border: 0 }}>
          <Collapse in={open} timeout="auto" unmountOnExit>
            <Box sx={{ p: 1.5, my: 1, bgcolor: 'action.hover', borderRadius: 1 }}>
              <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600 }}>Failure reason</Typography>
              <Box component="pre" sx={{ m: 0, mt: 0.5, fontFamily: 'ui-monospace, monospace', fontSize: 12, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
                {f.reason || '(no reason captured)'}
              </Box>
            </Box>
          </Collapse>
        </TableCell>
      </TableRow>
    </>
  )
}

// ── Draft bug card (rendered from the deterministic BugDraftJson) ───────────────
function DraftBugCard({ f, selected, onToggle }: { f: TestFailureDto; selected: boolean; onToggle: () => void }) {
  const draft = parseBugDraft(f.bugDraftJson)
  if (!draft) return null
  const filed = f.bugId != null
  const sevColor = draft.Severity === 'High' || draft.Severity === 'Critical' ? 'error'
    : draft.Severity === 'Low' ? 'default' : 'warning'
  return (
    <Card variant="outlined" sx={{ borderLeft: '4px solid', borderLeftColor: filed ? 'success.main' : 'error.main' }}>
      <CardContent sx={{ py: 1.5, '&:last-child': { pb: 1.5 } }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
          {!filed && <Checkbox size="small" sx={{ p: 0, mt: 0.25 }} checked={selected} onChange={onToggle} />}
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', mb: 0.5 }}>
              <Chip size="small" color={sevColor} label={draft.Severity} />
              {f.adoTestCaseId && <Chip size="small" variant="outlined" label={`TC #${f.adoTestCaseId}`} />}
              {filed && <Chip size="small" color="success" label={`Filed · Bug #${f.bugId}`} />}
            </Stack>
            <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.75 }}>{draft.Title}</Typography>
            {draft.Preconditions && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
                <b>Preconditions:</b> {draft.Preconditions}
              </Typography>
            )}
            {draft.ReproSteps && draft.ReproSteps.length > 0 && (
              <Box sx={{ mb: 0.5 }}>
                <Typography variant="caption" sx={{ fontWeight: 600 }}>Steps to reproduce</Typography>
                <Box component="ol" sx={{ m: 0, pl: 2.5 }}>
                  {draft.ReproSteps.map((s, i) => <li key={i}><Typography variant="caption">{s}</Typography></li>)}
                </Box>
              </Box>
            )}
            {draft.Expected && (
              <Typography variant="caption" sx={{ display: 'block', mb: 0.5 }}><b>Expected Result:</b> {draft.Expected}</Typography>
            )}
            {draft.Observations && draft.Observations.length > 0 && (
              <Box sx={{ mb: 0.5 }}>
                <Typography variant="caption" sx={{ fontWeight: 600 }}>Actual Result</Typography>
                <Box component="pre" sx={{ m: 0, mt: 0.25, fontFamily: 'ui-monospace, monospace', fontSize: 11.5, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: 'error.main' }}>
                  {draft.Observations.join('\n')}
                </Box>
              </Box>
            )}
            {(draft.FoundBy || draft.AutomatedTest) && (
              <Box sx={{ mt: 1, pt: 1, borderTop: t => `1px dashed ${t.palette.divider}` }}>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                  <b>How this bug was found:</b> {draft.FoundBy ?? 'Automated QA Loop run.'}
                </Typography>
                {draft.AutomatedTest && (
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                    <b>Automated test:</b> {draft.AutomatedTest}
                  </Typography>
                )}
              </Box>
            )}
          </Box>
        </Stack>
      </CardContent>
    </Card>
  )
}

// ── Impact tab ────────────────────────────────────────────────────────────────
const LEVEL_LABELS: Record<string, string> = {
  Contract: 'Contract Tests', Endpoint: 'Endpoint Tests', Workflow: 'Workflow Tests', Ui: 'UI Tests',
}
const CONF_COLOR: Record<string, 'success' | 'warning' | 'default'> = {
  high: 'success', medium: 'warning', low: 'default',
}

function ImpactLevelTabs({ byLevel }: { byLevel: Map<string, RunImpactTest[]> }) {
  const levels = [...byLevel.keys()]
  const [selected, setSelected] = useState<string | null>(null)
  const active = selected && byLevel.has(selected) ? selected : levels[0]
  const tests = byLevel.get(active) ?? []
  return (
    <>
      <Tabs value={active} onChange={(_, v) => setSelected(v)}
        sx={{ borderBottom: 1, borderColor: 'divider', minHeight: 36, mb: 1 }}>
        {levels.map(l => (
          <Tab key={l} value={l} sx={{ textTransform: 'none', minHeight: 36 }}
            label={`${LEVEL_LABELS[l] ?? l} (${byLevel.get(l)!.length})`} />
        ))}
      </Tabs>
      <Table size="small">
        <TableBody>
          {tests.map(t => (
            <TableRow key={t.id} hover>
              <TableCell sx={{ py: 0.5 }}>{cleanTitle(t.title)}</TableCell>
              <TableCell sx={{ py: 0.5, width: 110 }}>
                {t.adoTestCaseId ? <Chip size="small" variant="outlined" label={`TC #${t.adoTestCaseId}`} /> : '—'}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </>
  )
}

function ImpactView({ runId, adoBaseUrl }: { runId: number; adoBaseUrl?: string }) {
  const impactQ = useQuery({ queryKey: ['run-impact', runId], queryFn: () => getRunImpact(runId) })
  const impact = impactQ.data

  if (impactQ.isLoading) return <LinearProgress />
  if (impactQ.isError) return <Alert severity="error">{axiosMessage(impactQ.error)}</Alert>
  if (!impact?.hasImpact) {
    return (
      <Typography variant="body2" color="text.secondary">
        No impact analysis for this run — it was launched manually against the whole module,
        so every pushed test was in scope.
      </Typography>
    )
  }

  const trigger = impact.trigger
  const buildUrl = adoBaseUrl && trigger?.buildId
    ? `${adoBaseUrl}/_build/results?buildId=${trigger.buildId}` : null
  const byLevel = new Map<string, RunImpactTest[]>()
  for (const t of impact.tests ?? []) {
    const list = byLevel.get(t.level) ?? []
    list.push(t)
    byLevel.set(t.level, list)
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Chip size="small" color={CONF_COLOR[impact.confidence ?? 'low'] ?? 'default'}
          label={`Confidence: ${impact.confidence ?? 'low'}`} />
        <Chip size="small" variant="outlined" label={`${impact.impactedCount ?? 0} impacted test(s)`} />
        {impact.fallback && (
          <Tooltip title="AI impact analysis failed — the whole module was selected as a safe fallback">
            <Chip size="small" color="warning" label="Fallback (module-wide)" />
          </Tooltip>
        )}
        {trigger?.source === 'pipeline-watcher' && trigger.buildId && (
          buildUrl
            ? <Chip size="small" variant="outlined" clickable component="a" href={buildUrl} target="_blank"
                icon={<OpenInNewIcon sx={{ fontSize: 14 }} />} label={`Build #${trigger.buildId}`} />
            : <Chip size="small" variant="outlined" label={`Build #${trigger.buildId}`} />
        )}
        {trigger?.commitSha && (
          <Chip size="small" variant="outlined" label={`commit ${trigger.commitSha.slice(0, 8)}`} />
        )}
      </Stack>

      <Alert severity="info" icon={false} sx={{ '& .MuiAlert-message': { width: '100%' } }}>
        <Typography variant="caption" sx={{ fontWeight: 700, display: 'block', mb: 0.25 }}>
          Why these tests were selected
        </Typography>
        <Typography variant="body2">{impact.reasoning || '—'}</Typography>
        {impact.skippedReason && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
            Skipped: {impact.skippedReason}
          </Typography>
        )}
      </Alert>

      <Box>
        <Typography variant="body2" sx={{ fontWeight: 700, mb: 1 }}>Impacted tests by level</Typography>
        {byLevel.size === 0 && (
          <Typography variant="body2" color="text.secondary">
            No tests impacted — the run was cancelled before execution.
          </Typography>
        )}
        {byLevel.size > 0 && <ImpactLevelTabs byLevel={byLevel} />}
      </Box>
    </Stack>
  )
}

// ── Run drawer ────────────────────────────────────────────────────────────────
function RunDrawer({ run, adoBaseUrl, onClose }: { run: TestRunSummary; adoBaseUrl?: string; onClose: () => void }) {
  const [tab, setTab] = useState<'console' | 'failures' | 'draftbugs' | 'impact'>('console')
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())
  const [story, setStory] = useState<WorkItemOption | null>(null)
  const [storyInput, setStoryInput] = useState('')
  const [storyOpts, setStoryOpts] = useState<WorkItemOption[]>([])
  const [triaging, setTriaging] = useState(false)
  const [filing, setFiling] = useState(false)
  const [error, setError] = useState('')
  const [fullscreen, setFullscreen] = useState(false)
  const [fileDialogOpen, setFileDialogOpen] = useState(false)

  const detailQ = useQuery({
    queryKey: ['run-detail', run.id],
    queryFn: () => getRunDetail(run.id),
    refetchInterval: (q) => q.state.data && IN_FLIGHT.includes(q.state.data.status) ? 3000 : false,
  })
  const detail: TestRunDetail | undefined = detailQ.data

  function toggleId(id: number) {
    setSelectedIds(prev => { const s = new Set(prev); if (s.has(id)) s.delete(id); else s.add(id); return s })
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
  // Every failure now carries a deterministic BugDraftJson, so all un-filed failures are fileable.
  const draftBugs = failures.filter(f => f.bugDraftJson != null)
  const pendingDrafts = draftBugs.filter(f => !f.bugId)
  const allPendingIds = new Set(pendingDrafts.map(f => f.id))

  // detail is polled every 3s while in-flight — use it as the live source of truth for all
  // status-dependent rendering. The `run` prop is a stale list-row snapshot from when the row
  // was clicked; it never gets updated, so adoTestRunId, status, and counts lag behind.
  const liveStatus = detail?.status ?? run.status
  const liveAdoTestRunId = detail?.adoTestRunId ?? run.adoTestRunId
  const liveErrorMessage = detail?.errorMessage ?? run.errorMessage
  const livePassed = detail?.passed ?? run.passed
  const liveFailed = detail?.failed ?? run.failed
  const liveSkipped = detail?.skipped ?? run.skipped
  const liveDuration = detail?.durationMs ?? run.durationMs
  const isInFlight = IN_FLIGHT.includes(liveStatus)

  const adoRunUrl = liveAdoTestRunId && adoBaseUrl
    ? `${adoBaseUrl}/_testManagement/runs?runId=${liveAdoTestRunId}`
    : null

  return (
    <Drawer anchor="right" open onClose={onClose} slotProps={{ paper: { sx: { width: fullscreen ? '100%' : 640, maxWidth: '100%' } } }}>
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
        <Toolbar />
        <Box sx={{ p: 2, pb: 0 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <StatusChip value={liveStatus} />
            <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1 }}>{run.module ?? `Run #${run.id}`}</Typography>
            <Tooltip title={fullscreen ? 'Exit full screen' : 'Full screen'}>
              <IconButton size="small" onClick={() => setFullscreen(v => !v)}>
                {fullscreen ? <CloseFullscreenIcon fontSize="small" /> : <OpenInFullIcon fontSize="small" />}
              </IconButton>
            </Tooltip>
            <IconButton size="small" onClick={onClose}><CloseIcon /></IconButton>
          </Stack>
          <Stack direction="row" spacing={2} sx={{ mb: 1, flexWrap: 'wrap' }}>
            <Typography variant="caption">Profile: {run.profile}</Typography>
            <Typography variant="caption">Passed: {livePassed}</Typography>
            <Typography variant="caption">Failed: {liveFailed}</Typography>
            {liveSkipped > 0 && <Typography variant="caption">Skipped: {liveSkipped}</Typography>}
            <Typography variant="caption">Duration: {fmtDuration(liveDuration)}</Typography>
          </Stack>
          <Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: 'wrap' }}>
            {adoRunUrl && <Button size="small" startIcon={<OpenInNewIcon />} href={adoRunUrl} target="_blank" rel="noopener">ADO Test Run</Button>}
            {/* Gate on liveStatus (from fresh detail poll) — not the stale run prop — so the
                buttons appear as soon as the run finishes without waiting for the list to refresh. */}
            {!isInFlight && !detailQ.isLoading && (
              detail?.reportPath
                ? <>
                    <Button size="small" startIcon={<OpenInNewIcon />} component="a" href={`/api/qaloop/runs/${run.id}/report/`} target="_blank" rel="noopener">Report ↗</Button>
                    <Button size="small" startIcon={<DownloadIcon />} component="a" href={`/api/qaloop/runs/${run.id}/report?dl=true`} download={`qa-run-${run.id}-report.zip`}>Download</Button>
                  </>
                : liveStatus !== 'Error' && liveStatus !== 'Cancelled'
                  && <Typography variant="caption" color="text.disabled" sx={{ alignSelf: 'center' }}>report unavailable</Typography>
            )}
          </Stack>
          <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ borderBottom: 1, borderColor: 'divider' }}>
            <Tab value="console" label="Console" sx={{ textTransform: 'none', minHeight: 36 }} />
            <Tab value="draftbugs" label={`Draft Bugs (${draftBugs.length})`} sx={{ textTransform: 'none', minHeight: 36 }} />
            <Tab value="failures" label={`Failures (${failures.length})`} sx={{ textTransform: 'none', minHeight: 36 }} />
            <Tab value="impact" label="Impact" sx={{ textTransform: 'none', minHeight: 36 }} />
          </Tabs>
        </Box>
        <Box sx={{ flex: 1, overflowY: tab === 'console' ? 'hidden' : 'auto', p: 2, display: 'flex', flexDirection: 'column' }}>
          {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

          {tab === 'failures' && (
            <Box>
              {failures.length === 0 && !detailQ.isLoading && (
                isInFlight
                  ? <EmptyState icon={<HourglassEmptyIcon />} title="Run in progress"
                      hint="Failures will appear here once the run finishes." />
                  : liveStatus === 'Error' || liveStatus === 'Cancelled'
                    ? <EmptyState icon={<BugReportIcon />} title={`Run ${liveStatus.toLowerCase()}`}
                        hint={liveErrorMessage ?? 'No test results were recorded.'} />
                    : livePassed === 0 && liveFailed === 0
                      ? <EmptyState icon={<BugReportIcon />} title="No tests ran"
                          hint="No tests matched this run's filters — nothing was executed." />
                      : <EmptyState icon={<BugReportIcon />} title="No failures" hint="All tests passed." />
              )}
              {failures.length > 0 && (
                <Box sx={{ overflowX: 'auto' }}>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Test</TableCell><TableCell>Class</TableCell>
                        <TableCell>Test Case</TableCell><TableCell>Bug</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {failures.map(f => <FailureRow key={f.id} f={f} />)}
                    </TableBody>
                  </Table>
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                    Click a row to expand its full failure reason. File bugs from the <b>Draft Bugs</b> tab.
                  </Typography>
                </Box>
              )}
            </Box>
          )}

          {tab === 'draftbugs' && (
            <Box>
              {draftBugs.length === 0 && !detailQ.isLoading && (
                isInFlight
                  ? <EmptyState icon={<HourglassEmptyIcon />} title="Run in progress"
                      hint="Draft bugs are generated from failed tests once the run finishes." />
                  : <EmptyState icon={<BugReportIcon />} title="No draft bugs"
                      hint={liveFailed === 0 ? 'All tests passed — nothing to file.' : 'No failures were captured for this run.'} />
              )}
              {draftBugs.length > 0 && (
                <>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.5, flexWrap: 'wrap' }}>
                    <Typography variant="caption" color="text.secondary">
                      {draftBugs.length} draft{draftBugs.length !== 1 ? 's' : ''} from failed tests · {pendingDrafts.length} un-filed
                    </Typography>
                    <Box sx={{ flex: 1 }} />
                    {pendingDrafts.length > 0 && (
                      <Button size="small" onClick={() => setSelectedIds(selectedIds.size === allPendingIds.size ? new Set() : new Set(allPendingIds))}>
                        {selectedIds.size === allPendingIds.size ? 'Clear' : 'Select all'}
                      </Button>
                    )}
                    <Button size="small" variant="outlined" disabled={triaging}
                      startIcon={triaging ? <CircularProgress size={14} /> : undefined} onClick={doTriage}
                      title="Re-classify these drafts with AI (real bug vs test issue) and refine the wording">
                      AI enrich
                    </Button>
                    <Button size="small" variant="contained" color="error" disabled={selectedIds.size === 0}
                      startIcon={<BugReportIcon />} onClick={() => setFileDialogOpen(true)}
                      title={selectedIds.size === 0 ? 'Select drafts first' : 'File the selected drafts as ADO Bugs'}>
                      File to ADO{selectedIds.size > 0 ? ` (${selectedIds.size})` : ''}
                    </Button>
                  </Stack>
                  <Stack spacing={1.5}>
                    {draftBugs.map(f => (
                      <DraftBugCard key={f.id} f={f} selected={selectedIds.has(f.id)} onToggle={() => toggleId(f.id)} />
                    ))}
                  </Stack>
                  <Dialog open={fileDialogOpen} onClose={() => !filing && setFileDialogOpen(false)} fullWidth maxWidth="sm">
                    <DialogTitle>File {selectedIds.size} bug{selectedIds.size !== 1 ? 's' : ''} to ADO</DialogTitle>
                    <DialogContent>
                      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                        The bugs are created as <b>children</b> of the ticket you pick and linked to their failing test cases.
                        Leave the ticket empty to file them standalone.
                      </Typography>
                      <Autocomplete
                        options={storyOpts} getOptionLabel={o => `#${o.id} — ${o.title}`}
                        inputValue={storyInput}
                        onInputChange={(_, v) => { setStoryInput(v); if (v.length >= 2) searchWorkItems(v).then(setStoryOpts).catch(() => {}) }}
                        onChange={(_, v) => setStory(v)} value={story}
                        renderInput={p => <TextField {...p} label="Parent ticket (search by id or title)" size="small" autoFocus />}
                      />
                    </DialogContent>
                    <DialogActions>
                      <Button disabled={filing} onClick={() => setFileDialogOpen(false)}>Cancel</Button>
                      <Button variant="contained" color="error" disabled={filing}
                        startIcon={filing ? <CircularProgress size={16} /> : <BugReportIcon />}
                        onClick={async () => { await doFileBugs(); setFileDialogOpen(false) }}>
                        File {selectedIds.size} Bug{selectedIds.size !== 1 ? 's' : ''}
                      </Button>
                    </DialogActions>
                  </Dialog>
                </>
              )}
            </Box>
          )}

          {tab === 'console' && (() => {
            // Phase/system/result events are always published to the bus regardless of
            // activityEnabled. Once finished, replay from eventLogJson persisted at completion.
            const seed = parseEventLog(detail?.eventLogJson)
            const streamUrl = isInFlight ? runStreamUrl(run.id) : null
            if (!isInFlight && seed.length === 0) {
              return detailQ.isLoading
                ? <Box sx={{ py: 2 }}><LinearProgress /></Box>
                : <EmptyState icon={<HourglassEmptyIcon />} title="No console output"
                    hint="This run predates console logging. Re-run to capture output." />
            }
            // key forces remount (re-seed) when the run transitions from in-flight to finished.
            return <AgentConsole key={`${run.id}-${isInFlight ? 'live' : 'done'}`}
              streamUrl={streamUrl} seedEvents={seed} title="Run console" fillHeight />
          })()}

          {tab === 'impact' && <ImpactView runId={run.id} adoBaseUrl={adoBaseUrl} />}
        </Box>
      </Box>
    </Drawer>
  )
}

// ── Page ──────────────────────────────────────────────────────────────────────
export default function QaRunsPage() {
  const [module, setModule]   = useState('')
  const [level, setLevel]     = useState('')      // '' == All
  const [tag, setTag]         = useState('')      // '' == All
  const [profile, setProfile] = useState('infinity')
  const [launching, setLaunching] = useState(false)
  const [launchError, setLaunchError] = useState('')
  const [page, setPage]       = useState(0)
  const [selectedRun, setSelectedRun] = useState<TestRunSummary | null>(null)

  const facetsQ = useQuery({ queryKey: ['qa-run-facets'], queryFn: getRunFacets })
  const facets = facetsQ.data
  // Seed profile from AUTOMATION_PROFILE env once facets arrive; user can still override.
  useEffect(() => {
    if (facets?.defaultProfile && facets.profiles.includes(facets.defaultProfile))
      setProfile(facets.defaultProfile)
  }, [facets?.defaultProfile])  // eslint-disable-line react-hooks/exhaustive-deps

  const TAKE = 20
  const runsQ = useQuery({
    queryKey: ['qa-runs', page],
    queryFn: () => listRunsPage(PROJECT_ID, TAKE, page * TAKE),
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
      <PageHeader title="QA Runs" />
      <Box sx={{ px: 3 }}>
        {/* Launcher */}
        <Card variant="outlined" sx={{ mb: 3 }}>
          <CardContent>
            <Typography variant="subtitle2" sx={{ mb: 2 }}>New Run</Typography>
            {launchError && <Alert severity="error" sx={{ mb: 2 }}>{launchError}</Alert>}
            <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
              <TextField select label="Module" size="small" value={module}
                onChange={e => setModule(e.target.value)} sx={{ minWidth: 200 }}
                disabled={facetsQ.isLoading}
                helperText={facets && facets.modules.length === 0 ? 'No modules with pushed drafts' : ' '}>
                {(facets?.modules ?? []).map(m => <MenuItem key={m} value={m}>{m}</MenuItem>)}
              </TextField>
              <TextField select label="Level" size="small" value={level}
                onChange={e => setLevel(e.target.value)} sx={{ minWidth: 160 }}>
                <MenuItem value="">All levels</MenuItem>
                {(facets?.levels ?? ['contract', 'endpoint', 'workflow']).map(l =>
                  <MenuItem key={l} value={l} sx={{ textTransform: 'capitalize' }}>{l}</MenuItem>)}
              </TextField>
              <Autocomplete
                freeSolo size="small" options={facets?.tags ?? []} value={tag}
                onChange={(_, v) => setTag(v ?? '')} onInputChange={(_, v) => setTag(v)}
                sx={{ minWidth: 160 }}
                renderInput={p => <TextField {...p} label="Tag" placeholder="All tags" />}
              />
              <TextField select label="Profile" size="small" value={profile}
                onChange={e => setProfile(e.target.value)} sx={{ minWidth: 140 }}>
                {(facets?.profiles ?? ['ideal', 'infinity', 'aspen', 'quantum']).map(p =>
                  <MenuItem key={p} value={p} sx={{ textTransform: 'capitalize' }}>{p}</MenuItem>)}
              </TextField>
              <Button variant="contained" startIcon={launching ? <CircularProgress size={16} /> : <PlayArrowIcon />}
                disabled={launching || !module} onClick={doLaunch} sx={{ height: 40, px: 3 }}>Run</Button>
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
                  <TableCell>Run</TableCell><TableCell>Module</TableCell><TableCell>Profile</TableCell><TableCell>Trigger</TableCell><TableCell>Status</TableCell>
                  <TableCell align="right">Pass</TableCell><TableCell align="right">Fail</TableCell>
                  <TableCell>Duration</TableCell><TableCell>Started</TableCell><TableCell />
                </TableRow>
              </TableHead>
              <TableBody>
                {runs.map(r => (
                  <TableRow key={r.id} hover sx={{ cursor: 'pointer' }} onClick={() => setSelectedRun(r)}>
                    <TableCell>#{r.id}</TableCell>
                    <TableCell>{r.module ?? `#${r.id}`}</TableCell>
                    <TableCell>{r.profile}</TableCell>
                    <TableCell>
                      <Chip size="small" variant="outlined"
                        label={r.triggerSource === 'manual' ? 'Manual' : 'Automated'}
                        color={r.triggerSource === 'manual' ? 'default' : 'info'} />
                    </TableCell>
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
      {selectedRun && <RunDrawer run={selectedRun} adoBaseUrl={facets?.adoBaseUrl} onClose={() => setSelectedRun(null)} />}
    </Box>
  )
}
