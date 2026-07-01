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
  approveAllDrafts, pushSession, cancelSession, sessionStreamUrl, listFrontendModules, LEVEL_NAMES,
  type ApiSessionDetail, type ApiTestDraftDto, type ScanStartRequest, type SurfaceRow,
  type FrontendModule,
} from '../../api/qaLoop'
import { getPlans, getSuites, searchWorkItems } from '../../api/authorTests'
import type { TestPlan, TestSuite } from '../../api/authorTests'
import { getAdoRepositories, getAdoBranches } from '../../api/settings'
import type { WorkItemOption } from '../types'
import { PROJECT_ID } from '../../config/env'

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
  const [fmodule, setFmodule]         = useState<FrontendModule | null>(null)
  const [fmodules, setFmodules]       = useState<FrontendModule[]>([])
  const [fmLoading, setFmLoading]     = useState(false)
  const [repo, setRepo]               = useState<string | null>(null)
  const [branch, setBranch]           = useState<string | null>(null)
  const [sourceDirs, setSourceDirs]   = useState('')
  const [planId, setPlanId]           = useState<number | null>(null)
  const [suiteId, setSuiteId]         = useState<number | null>(null)
  const [story, setStory]             = useState<WorkItemOption | null>(null)
  const [storyInput, setStoryInput]   = useState('')
  const [storyOpts, setStoryOpts]     = useState<WorkItemOption[]>([])
  const [plans, setPlans]             = useState<TestPlan[]>([])
  const [suites, setSuites]           = useState<TestSuite[]>([])
  const [repos, setRepos]             = useState<string[]>([])
  const [reposLoading, setReposLoading] = useState(false)
  const [branches, setBranches]       = useState<string[]>([])
  const [branchesLoading, setBranchesLoading] = useState(false)
  const [loading, setLoading]         = useState(false)

  async function loadFrontendModules() {
    if (fmodules.length > 0) return
    setFmLoading(true)
    try { setFmodules(await listFrontendModules()) } catch { /* ignore */ }
    finally { setFmLoading(false) }
  }
  async function loadRepos() {
    if (repos.length > 0) return
    setReposLoading(true)
    try { setRepos(await getAdoRepositories()) } catch { /* ignore */ }
    finally { setReposLoading(false) }
  }
  async function loadBranches(r: string) {
    setBranchesLoading(true)
    try {
      const bs = await getAdoBranches(r)
      setBranches(bs)
      if (bs.includes('main')) setBranch('main')   // sensible default when present
    } catch { setBranches([]) }
    finally { setBranchesLoading(false) }
  }
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
    if (!fmodule) return
    setLoading(true)
    try {
      const dirs = sourceDirs.split(/[,\n]/).map(d => d.trim()).filter(Boolean)
      const req: ScanStartRequest = {
        module: fmodule.moduleName,               // derived from the frontend module
        frontendModule: fmodule.routePath,
        frontendPath: fmodule.componentPath,
        repository: repo || undefined,
        branch: branch || undefined,
        sourceDirs: dirs.length > 0 ? dirs : undefined,
        testPlanId: planId ?? undefined,
        testSuiteId: suiteId ?? undefined,
        workItemId: story?.id ?? undefined,
        autoHandoff: true,
      }
      const { sessionId } = await startScan(PROJECT_ID, req)
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
        <Autocomplete
          options={fmodules} value={fmodule} loading={fmLoading}
          getOptionLabel={m => `${m.moduleName} — ${m.routePath}`}
          isOptionEqualToValue={(a, b) => a.componentPath === b.componentPath}
          onOpen={loadFrontendModules}
          onChange={(_, v) => setFmodule(v)}
          renderInput={p => <TextField {...p} required label="Frontend module" size="small"
            helperText={fmLoading ? 'Loading modules…'
              : fmodule ? fmodule.url
              : 'Pick a product module — the scan traces its frontend to the backend API'} />}
        />
        <Autocomplete
          options={repos} value={repo} loading={reposLoading}
          onOpen={loadRepos}
          onChange={(_, v) => { setRepo(v); setBranch(null); setBranches([]); if (v) loadBranches(v) }}
          renderInput={p => <TextField {...p} label="Repository (optional)" size="small"
            helperText={reposLoading ? 'Loading repositories…' : undefined} />}
        />
        <Autocomplete
          options={branches} value={branch} loading={branchesLoading} disabled={!repo}
          onOpen={() => { if (repo) loadBranches(repo) }}
          onChange={(_, v) => setBranch(v)}
          renderInput={p => <TextField {...p} label="Branch (optional)" size="small"
            helperText={!repo ? 'Pick a repository first' : branchesLoading ? 'Loading branches…' : undefined} />}
        />
        <TextField label="Source directories (optional)" size="small" multiline minRows={2}
          value={sourceDirs} onChange={e => setSourceDirs(e.target.value)}
          placeholder={'One per line, e.g.\nLead/src/Target.Crm.Lead/Target.Crm.Lead.Api/Target.Crm.Lead.Api/Deal'}
          helperText="Repo-relative paths to scan when the module isn't a top-level folder. Comma or newline separated." />
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
          <Button type="submit" variant="contained" disabled={loading || !fmodule}
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
  const module = (() => {
    try { return JSON.parse(session.analysisJson ?? '')?.Module ?? session.module ?? '' }
    catch { return session.module ?? '' }
  })()
  const rows: SurfaceRow[] = (() => {
    try { const p = JSON.parse(session.analysisJson ?? ''); return Array.isArray(p?.Rows) ? p.Rows : [] }
    catch { return [] }
  })()
  const [selected, setSelected] = useState<Set<string>>(() => new Set(rows.filter(r => r.Selected).map(r => r.Id)))
  const [saving, setSaving] = useState(false)

  async function save() {
    setSaving(true)
    try {
      // Full-replace: resend EVERY row with its current Selected flag, not just the checked ones —
      // omitting a row here deletes it from the matrix.
      const updated = rows.map(r => ({ ...r, Selected: selected.has(r.Id) }))
      await saveAnalysis(session.id, module, updated)
      onSaved()
    } catch { /* user sees nothing — non-blocking */ }
    finally { setSaving(false) }
  }

  if (rows.length === 0)
    return <EmptyState icon={<QrCode2Icon />} title="No coverage data yet" hint="Wait for the scan to complete." />

  const covered = rows.filter(r => r.Status === 'covered')
  const selCount = selected.size

  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
        <Typography variant="body2">{selCount} of {rows.length} selected → will generate {selCount}</Typography>
        <Box sx={{ flex: 1 }} />
        {covered.length > 0 && (
          <Button size="small" onClick={() => {
            setSelected(new Set(rows.filter(r => r.Status !== 'covered').map(r => r.Id)))
          }}>Deselect all covered</Button>
        )}
        <Button size="small" variant="contained" disabled={saving} onClick={save}
          startIcon={saving ? <CircularProgress size={14} /> : undefined}>Save</Button>
      </Stack>
      <Stack spacing={0.5}>
        {rows.map(r => (
          <Stack key={r.Id} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Checkbox size="small" checked={selected.has(r.Id)}
              onChange={e => setSelected(prev => { const s = new Set(prev); if (e.target.checked) s.add(r.Id); else s.delete(r.Id); return s })} />
            <Chip size="small" label={LEVEL_NAMES[r.Level] ?? r.Level} />
            <Chip size="small" label={r.Status === 'covered' ? 'covered' : 'gap'} color={r.Status === 'covered' ? 'success' : 'warning'} variant="outlined" />
            <Typography variant="body2" sx={{ fontFamily: 'monospace', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {r.Label}
            </Typography>
            {r.LinkedTestCaseId && (
              <Chip size="small" variant="outlined" label={`TC #${r.LinkedTestCaseId}`} />
            )}
          </Stack>
        ))}
      </Stack>
    </Box>
  )
}

// ── Drafts by level ───────────────────────────────────────────────────────────

function DraftsByLevel({ session, refetch }: { session: ApiSessionDetail; refetch: () => void }) {
  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  // ApiTestDraftDto.level is TestLevel.ToWire() — lowercase ("contract"/"endpoint"/"workflow") — not
  // the capitalized tab labels; compare case-insensitively rather than assuming either casing.
  const levels = ['Contract', 'Endpoint', 'Workflow']
  const [tab, setTab] = useState(0)
  const [editId, setEditId] = useState<number | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [saving, setSaving] = useState(false)

  const byLevel = (l: string) => drafts.filter(d => d.level.toLowerCase() === l.toLowerCase())

  async function doApprove(id: number) {
    try { await patchDraft(id, { status: 'Approved' }); refetch() } catch { /* ignore */ }
  }
  async function doReject(id: number) {
    try { await patchDraft(id, { status: 'Rejected' }); refetch() } catch { /* ignore */ }
  }
  async function doSaveTitle(d: ApiTestDraftDto) {
    setSaving(true)
    try { await patchDraft(d.id, { title: editTitle }); setEditId(null); refetch() }
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

// Push reads Plan/Suite/Story from the session itself (set at scan time) — the endpoint takes no
// body at all, so there is nothing to pick here. Show what will be used and let the user push,
// or point them back at a new scan if the session was never given a Plan/Suite.
function PushPanel({ session, refetch }: { session: ApiSessionDetail; refetch: () => void }) {
  const [pushing, setPushing] = useState(false)
  const [error, setError]   = useState('')

  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const approved = drafts.filter(d => d.status === 'Approved').length
  const canPush = Boolean(session.testPlanId && session.testSuiteId)

  async function doPush() {
    setPushing(true); setError('')
    try { await pushSession(session.id); refetch() }
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
      {canPush ? (
        <Stack direction="row" spacing={1}>
          <Chip size="small" label={`Plan #${session.testPlanId}`} />
          <Chip size="small" label={`Suite #${session.testSuiteId}`} />
          {session.workItemId && <Chip size="small" label={`Story #${session.workItemId}`} />}
        </Stack>
      ) : (
        <Alert severity="warning">
          This session has no Test Plan/Suite — set them on the New Scan form to push. (Plan/Suite are
          chosen once at scan time, not per push.)
        </Alert>
      )}
      <Stack direction="row" spacing={1}>
        <Button variant="contained" disabled={pushing || !canPush || approved === 0} onClick={doPush}
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
  listSessions: (take, skip) => listSessionsPage(PROJECT_ID, take, skip) as Promise<{ total: number; items: SessionSummary[] }>,
  getDetail: (id) => getSessionDetail(id) as Promise<SessionDetail>,
  streamUrl: sessionStreamUrl,
  StartForm: QaLoopStartForm,
  DetailBody: QaLoopDetailBody,
}
