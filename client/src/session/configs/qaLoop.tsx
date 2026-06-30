import { useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  IconButton, LinearProgress, Stack, Tab, Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import PushPinIcon from '@mui/icons-material/PushPin'
import QrCode2Icon from '@mui/icons-material/QrCode2'
import type { PipelineStep } from '../../components/AgentPipeline'
import AgentConsole from '../../components/AgentConsole'
import AgentPipeline from '../../components/AgentPipeline'
import EmptyState from '../../components/EmptyState'
import LiveActivityIndicator from '../../components/LiveActivityIndicator'
import StatusChip from '../../components/StatusChip'
import type {
  AnalyticsColumn, DetailBodyProps, SessionDetail, SessionModuleConfig, SessionSummary,
  StartFormProps,
} from '../types'
import { parseEventLog } from '../shared'
import { axiosMessage } from '../../common/errors'

import {
  startScan, listSessionsPage, getSessionDetail, saveAnalysis, generateDrafts, patchDraft,
  approveAllDrafts, pushSession, cancelSession, sessionStreamUrl,
  type ApiSessionDetail, type ApiTestDraftDto, type ScanStartRequest, type SurfaceRow,
} from '../../api/qaLoop'
import { getPlans, getSuites, searchWorkItems } from '../../api/authorTests'
import type { TestPlan, TestSuite } from '../../api/authorTests'
import type { WorkItemOption } from '../types'

// ── Pipeline ──────────────────────────────────────────────────────────────────

const PIPELINE_STEPS: PipelineStep[] = [
  { key: 'scan',   label: 'Scan',   icon: <QrCode2Icon /> },
  { key: 'review', label: 'Review', icon: <CheckIcon /> },
  { key: 'drafts', label: 'Drafts', icon: <PushPinIcon /> },
  { key: 'push',   label: 'Push',   icon: <CheckIcon /> },
]

// eslint-disable-next-line @typescript-eslint/no-unused-vars
function activeStepIndex(status: string, _activity: string | null): number {
  switch (status) {
    case 'Scanning':      return 0
    case 'AwaitingReview': return 1
    case 'Drafting':      return 2
    case 'Drafted':
    case 'Pushing':       return 3
    default:              return 0
  }
}

const ANALYTICS_COLUMNS: AnalyticsColumn[] = [
  { key: 'module', label: 'Module', value: (d: SessionDetail | undefined) => (d as ApiSessionDetail | undefined)?.module ?? '—' },
  { key: 'drafts', label: 'Drafts', align: 'right', value: (d: SessionDetail | undefined) => d?.drafts?.length ?? '—' },
]

// ── Start form ────────────────────────────────────────────────────────────────

export function QaLoopStartForm({ onStarted, onError, onClose }: StartFormProps) {
  const [module, setModule]           = useState('')
  const [repo, setRepo]               = useState('')
  const [branch, setBranch]           = useState('')
  const [planId, setPlanId]           = useState<number | null>(null)
  const [suiteId, setSuiteId]         = useState<number | null>(null)
  const [story, setStory]             = useState<WorkItemOption | null>(null)
  const [storyInput, setStoryInput]   = useState('')
  const [storyOpts, setStoryOpts]     = useState<WorkItemOption[]>([])
  const [plans, setPlans]             = useState<TestPlan[]>([])
  const [suites, setSuites]           = useState<TestSuite[]>([])
  const [loading, setLoading]         = useState(false)

  async function loadPlans() {
    try { setPlans(await getPlans()) } catch { /* ignore */ }
  }
  async function loadSuites(pid: number) {
    try { setSuites(await getSuites(pid)) } catch { setSuites([]) }
  }
  async function searchStories(q: string) {
    if (q.length < 2) return
    try { setStoryOpts(await searchWorkItems(q)) } catch { /* ignore */ }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!module.trim()) return
    setLoading(true)
    try {
      const req: ScanStartRequest = {
        module: module.trim(),
        repository: repo.trim() || undefined,
        branch: branch.trim() || undefined,
        testPlanId: planId ?? undefined,
        testSuiteId: suiteId ?? undefined,
        workItemId: story?.id ?? undefined,
        autoHandoff: true,
      }
      const { sessionId } = await startScan(req)
      onStarted(sessionId)
    } catch (err) {
      onError(err, 'Failed to start scan')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Box component="form" onSubmit={handleSubmit}>
      <Stack spacing={2}>
        <TextField required label="Module" size="small" value={module}
          onChange={e => setModule(e.target.value)} placeholder="e.g. deals" />
        <TextField label="Repository" size="small" value={repo}
          onChange={e => setRepo(e.target.value)} placeholder="leave blank for default" />
        <TextField label="Branch" size="small" value={branch}
          onChange={e => setBranch(e.target.value)} placeholder="leave blank for default" />
        <Autocomplete
          options={plans} getOptionLabel={p => `${p.id} — ${p.name}`}
          onOpen={loadPlans}
          onChange={(_, v) => { setPlanId(v?.id ?? null); setSuiteId(null); if (v) loadSuites(v.id) }}
          renderInput={p => <TextField {...p} label="Test Plan (optional)" size="small" />}
        />
        {planId && (
          <Autocomplete
            options={suites} getOptionLabel={s => `${s.id} — ${s.name}`}
            onChange={(_, v) => setSuiteId(v?.id ?? null)}
            renderInput={p => <TextField {...p} label="Test Suite (optional)" size="small" />}
          />
        )}
        <Autocomplete
          options={storyOpts} getOptionLabel={o => `#${o.id} — ${o.title}`}
          inputValue={storyInput} onInputChange={(_, v) => { setStoryInput(v); searchStories(v) }}
          onChange={(_, v) => setStory(v)} value={story}
          renderInput={p => <TextField {...p} label="Story (optional)" size="small" />}
        />
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          <Button onClick={onClose} disabled={loading}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={loading || !module.trim()}
            startIcon={loading ? <CircularProgress size={16} /> : undefined}>
            Start Scan
          </Button>
        </Stack>
      </Stack>
    </Box>
  )
}

// ── Coverage matrix ───────────────────────────────────────────────────────────

function CoverageMatrix({ session, onSaved }: { session: ApiSessionDetail; onSaved: () => void }) {
  const rows: SurfaceRow[] = (() => {
    try { const p = JSON.parse(session.analysisJson ?? ''); return Array.isArray(p?.rows) ? p.rows : [] }
    catch { return [] }
  })()
  const [selected, setSelected] = useState<Set<string>>(() => new Set(rows.map(r => r.surface + r.route)))
  const [saving, setSaving] = useState(false)

  async function save() {
    setSaving(true)
    try {
      const filtered = rows.filter(r => selected.has(r.surface + r.route))
      await saveAnalysis(session.id, filtered)
      onSaved()
    } catch { /* user sees nothing — non-blocking */ }
    finally { setSaving(false) }
  }

  if (rows.length === 0)
    return <EmptyState icon={<QrCode2Icon />} title="No coverage data yet" hint="Wait for the scan to complete." />

  const covered = rows.filter(r => r.status === 'covered')
  const selCount = rows.filter(r => selected.has(r.surface + r.route)).length

  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
        <Typography variant="body2">{selCount} of {rows.length} selected → will generate {selCount}</Typography>
        <Box sx={{ flex: 1 }} />
        {covered.length > 0 && (
          <Button size="small" onClick={() => {
            const gaps = new Set(rows.filter(r => r.status !== 'covered').map(r => r.surface + r.route))
            setSelected(gaps)
          }}>Deselect all covered</Button>
        )}
        <Button size="small" variant="contained" disabled={saving} onClick={save}
          startIcon={saving ? <CircularProgress size={14} /> : undefined}>Save</Button>
      </Stack>
      <Stack spacing={0.5}>
        {rows.map(r => {
          const key = r.surface + r.route
          return (
            <Stack key={key} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Checkbox size="small" checked={selected.has(key)}
                onChange={e => setSelected(prev => { const s = new Set(prev); if (e.target.checked) s.add(key); else s.delete(key); return s })} />
              <Chip size="small" label={r.level} />
              <Chip size="small" label={r.status === 'covered' ? 'covered' : 'gap'} color={r.status === 'covered' ? 'success' : 'warning'} variant="outlined" />
              <Typography variant="body2" sx={{ fontFamily: 'monospace', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {r.method ? `${r.method} ` : ''}{r.route}
              </Typography>
            </Stack>
          )
        })}
      </Stack>
    </Box>
  )
}

// ── Drafts by level ───────────────────────────────────────────────────────────

function DraftsByLevel({ session, refetch }: { session: ApiSessionDetail; refetch: () => void }) {
  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const levels = ['Contract', 'Endpoint', 'Workflow']
  const [tab, setTab] = useState(0)
  const [editId, setEditId] = useState<number | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [saving, setSaving] = useState(false)

  const byLevel = (l: string) => drafts.filter(d => d.level === l)

  async function doApprove(id: number) {
    try { await patchDraft(session.id, id, { status: 'Approved' }); refetch() } catch { /* ignore */ }
  }
  async function doReject(id: number) {
    try { await patchDraft(session.id, id, { status: 'Rejected' }); refetch() } catch { /* ignore */ }
  }
  async function doSaveTitle(d: ApiTestDraftDto) {
    setSaving(true)
    try { await patchDraft(session.id, d.id, { title: editTitle }); setEditId(null); refetch() }
    catch { /* ignore */ }
    finally { setSaving(false) }
  }
  async function doApproveAll() {
    try { await approveAllDrafts(session.id); refetch() } catch { /* ignore */ }
  }

  if (drafts.length === 0)
    return <EmptyState icon={<PushPinIcon />} title="No drafts yet" hint="Click Generate to create drafts." />

  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
        <Typography variant="body2">{drafts.length} draft{drafts.length !== 1 ? 's' : ''}</Typography>
        <Box sx={{ flex: 1 }} />
        <Button size="small" variant="outlined" onClick={doApproveAll}>Approve all</Button>
      </Stack>
      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 1 }}>
        {levels.map((l, i) => <Tab key={l} value={i} label={`${l} (${byLevel(l).length})`} sx={{ textTransform: 'none', minHeight: 36 }} />)}
      </Tabs>
      <Stack spacing={1}>
        {byLevel(levels[tab]).map(d => (
          <Card key={d.id} variant="outlined">
            <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <StatusChip value={d.status} kind="draft" />
                {editId === d.id ? (
                  <>
                    <TextField size="small" value={editTitle} onChange={e => setEditTitle(e.target.value)}
                      sx={{ flex: 1 }} autoFocus onKeyDown={e => { if (e.key === 'Enter') doSaveTitle(d) }} />
                    <IconButton size="small" disabled={saving} onClick={() => doSaveTitle(d)}>
                      {saving ? <CircularProgress size={14} /> : <CheckIcon fontSize="small" />}
                    </IconButton>
                    <IconButton size="small" onClick={() => setEditId(null)}><CloseIcon fontSize="small" /></IconButton>
                  </>
                ) : (
                  <>
                    <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}
                      onClick={() => { setEditId(d.id); setEditTitle(d.title) }}>{d.title}</Typography>
                    {d.adoTestCaseId && <Chip size="small" color="success" variant="outlined" label={`TC #${d.adoTestCaseId}`} />}
                    {d.status !== 'Pushed' && d.status !== 'Rejected' && (
                      <>
                        {d.status !== 'Approved' && <Tooltip title="Approve"><IconButton size="small" color="success" onClick={() => doApprove(d.id)}><CheckIcon fontSize="small" /></IconButton></Tooltip>}
                        <Tooltip title="Reject"><IconButton size="small" color="error" onClick={() => doReject(d.id)}><CloseIcon fontSize="small" /></IconButton></Tooltip>
                      </>
                    )}
                  </>
                )}
              </Stack>
              {d.specCode && (
                <Box component="pre" sx={{ mt: 1, p: 1, bgcolor: 'action.hover', borderRadius: 1, fontSize: 11, overflowX: 'auto', whiteSpace: 'pre-wrap', maxHeight: 180 }}>
                  {d.specCode.slice(0, 800)}{d.specCode.length > 800 ? '\n…' : ''}
                </Box>
              )}
            </CardContent>
          </Card>
        ))}
      </Stack>
    </Box>
  )
}

// ── Push panel ────────────────────────────────────────────────────────────────

function PushPanel({ session, refetch }: { session: ApiSessionDetail; refetch: () => void }) {
  const [plans, setPlans]   = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])
  const [planId, setPlanId] = useState<number | null>(session.testPlanId ?? null)
  const [suiteId, setSuiteId] = useState<number | null>(session.testSuiteId ?? null)
  const [storyOpts, setStoryOpts] = useState<WorkItemOption[]>([])
  const [story, setStory]   = useState<WorkItemOption | null>(null)
  const [storyInput, setStoryInput] = useState('')
  const [pushing, setPushing] = useState(false)
  const [error, setError]   = useState('')

  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const approved = drafts.filter(d => d.status === 'Approved').length

  async function doPush() {
    if (!planId || !suiteId) { setError('Pick a Test Plan and Suite.'); return }
    setPushing(true); setError('')
    try { await pushSession(session.id, { testPlanId: planId, testSuiteId: suiteId, storyId: story?.id }); refetch() }
    catch (err) { setError(axiosMessage(err)) }
    finally { setPushing(false) }
  }

  async function doCancel() {
    try { await cancelSession(session.id); refetch() } catch { /* ignore */ }
  }

  return (
    <Stack spacing={2}>
      {error && <Alert severity="error">{error}</Alert>}
      <Typography variant="body2">{approved} approved draft{approved !== 1 ? 's' : ''} will be pushed.</Typography>
      <Autocomplete
        options={plans} getOptionLabel={p => `${p.id} — ${p.name}`} value={plans.find(p => p.id === planId) ?? null}
        onOpen={async () => { try { setPlans(await getPlans()) } catch { /* ignore */ } }}
        onChange={(_, v) => { setPlanId(v?.id ?? null); setSuiteId(null); if (v?.id) getSuites(v.id).then(setSuites).catch(() => {}) }}
        renderInput={p => <TextField {...p} label="Test Plan" size="small" required />}
      />
      {planId && (
        <Autocomplete
          options={suites} getOptionLabel={s => `${s.id} — ${s.name}`} value={suites.find(s => s.id === suiteId) ?? null}
          onChange={(_, v) => setSuiteId(v?.id ?? null)}
          renderInput={p => <TextField {...p} label="Test Suite" size="small" required />}
        />
      )}
      <Autocomplete
        options={storyOpts} getOptionLabel={o => `#${o.id} — ${o.title}`} inputValue={storyInput}
        onInputChange={(_, v) => { setStoryInput(v); if (v.length >= 2) searchWorkItems(v).then(setStoryOpts).catch(() => {}) }}
        onChange={(_, v) => setStory(v)} value={story}
        renderInput={p => <TextField {...p} label="Link to Story (optional)" size="small" />}
      />
      <Stack direction="row" spacing={1}>
        <Button variant="contained" disabled={pushing || !planId || !suiteId || approved === 0} onClick={doPush}
          startIcon={pushing ? <CircularProgress size={16} /> : <PushPinIcon />}>
          Push {approved > 0 ? `(${approved})` : ''}
        </Button>
        {!['Done', 'Failed', 'Cancelled'].includes(session.status) && (
          <Button color="error" onClick={doCancel}>Cancel scan</Button>
        )}
      </Stack>
    </Stack>
  )
}

// ── Detail body ───────────────────────────────────────────────────────────────

export function QaLoopDetailBody({ session, refetch, onError, fullScreen }: DetailBodyProps) {
  const s = session as ApiSessionDetail
  const live = !['Done', 'Failed', 'Cancelled'].includes(s.status)
  const stepIdx = activeStepIndex(s.status, s.currentActivity)
  const [tab, setTab] = useState<'detail' | 'coverage' | 'drafts' | 'push'>('detail')

  async function doGenerate() {
    try { await generateDrafts(s.id); refetch() }
    catch (err) { onError(err, 'Failed to generate drafts') }
  }

  return (
    <Box sx={{ maxWidth: fullScreen ? undefined : 800 }}>
      <AgentPipeline steps={PIPELINE_STEPS} activeIndex={stepIdx} inFlight={live} failed={s.status === 'Failed'} currentActivity={s.currentActivity ?? null} />
      {live && <Box sx={{ mb: 2 }}><LiveActivityIndicator active text={s.currentActivity ?? 'Processing…'} /></Box>}

      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 2 }}>
        <Tab value="detail" label="Activity" sx={{ textTransform: 'none', minHeight: 36 }} />
        <Tab value="coverage" label="Coverage" sx={{ textTransform: 'none', minHeight: 36 }} />
        <Tab value="drafts" label={`Drafts (${session.drafts?.length ?? 0})`} sx={{ textTransform: 'none', minHeight: 36 }} />
        <Tab value="push" label="Push" sx={{ textTransform: 'none', minHeight: 36 }} />
      </Tabs>

      {tab === 'detail' && (
        <Box>
          {s.status === 'AwaitingReview' && (
            <Alert severity="info" sx={{ mb: 2 }} action={
              <Button color="inherit" size="small" onClick={doGenerate}>Generate drafts</Button>
            }>Coverage analysis ready. Generate Playwright drafts when ready.</Alert>
          )}
          {s.errorMessage && <Alert severity="error" sx={{ mb: 2 }}>{s.errorMessage}</Alert>}
          {(s.activityEnabled || (s.eventLogJson?.length ?? 0) > 2)
            ? <AgentConsole streamUrl={live ? sessionStreamUrl(s.id) : null} seedEvents={parseEventLog(s.eventLogJson)} />
            : live
              ? <Box sx={{ py: 1 }}><LinearProgress /></Box>
              : null}
        </Box>
      )}

      {tab === 'coverage' && <CoverageMatrix session={s} onSaved={refetch} />}
      {tab === 'drafts' && <DraftsByLevel session={s} refetch={refetch} />}
      {tab === 'push' && <PushPanel session={s} refetch={refetch} />}
    </Box>
  )
}

// ── Config export ─────────────────────────────────────────────────────────────

// eslint-disable-next-line react-refresh/only-export-components
export const qaLoopConfig: SessionModuleConfig = {
  key: 'qa-loop',
  apiBase: '/api/qaloop',
  route: '/qa-loop',
  labels: { title: 'QA Loop', subtitle: 'AI-powered test authoring from source', newCta: 'New Scan' },
  moduleChip: { label: 'QA LOOP', icon: <QrCode2Icon /> },
  inFlightStatuses: ['Scanning', 'AwaitingReview', 'Drafting', 'Pushing'],
  pipelineSteps: PIPELINE_STEPS,
  activeStepIndex,
  analyticsColumns: ANALYTICS_COLUMNS,
  renderIdentity: (s) => {
    const d = s as ApiSessionDetail
    const module = d.module ?? `#${d.id}`
    const draftCount = d.drafts?.length ?? 0
    return `${module} · ${d.status}${draftCount > 0 ? ` · ${draftCount} draft${draftCount !== 1 ? 's' : ''}` : ''}`
  },
  listSessions: (take, skip) => listSessionsPage(take, skip) as Promise<{ total: number; items: SessionSummary[] }>,
  getDetail: (id) => getSessionDetail(id) as Promise<SessionDetail>,
  streamUrl: sessionStreamUrl,
  StartForm: QaLoopStartForm,
  DetailBody: QaLoopDetailBody,
}
