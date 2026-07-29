import { useState, useEffect } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  IconButton, LinearProgress, Stack, Tab, Table, TableBody, TableCell, TableHead, TableRow,
  Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
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
import { useAdoWorkItemUrl } from '../../common/adoLinks'
import DraftDetailDialog from '../DraftDetailDialog'
import VisibilityIcon from '@mui/icons-material/Visibility'

import {
  startScan, listSessionsPage, getSessionDetail, saveAnalysis, generateDrafts, patchDraft,
  approveAllDrafts, pushSession, updateSessionPlan, cancelSession, deleteSession, sessionStreamUrl, listFrontendModules, LEVEL_NAMES,
  type ApiSessionDetail, type ApiTestDraftDto, type ScanStartRequest, type SurfaceRow,
  type FrontendModule,
} from '../../api/qaLoop'
import { getPlans, getSuites, searchWorkItems } from '../../api/authorTests'
import type { TestPlan, TestSuite } from '../../api/authorTests'
import { getAdoRepositories, getAdoBranches, getConfig } from '../../api/settings'
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

  // Load saved defaults from Settings and pre-populate fields on mount.
  useEffect(() => {
    getConfig().then(async cfg => {
      const defRepo   = String(cfg['qaloop.default_repository'] ?? '')
      const defBranch = String(cfg['qaloop.default_branch'] ?? '')
      const defPlanId = cfg['qaloop.default_plan_id'] ? Number(cfg['qaloop.default_plan_id']) : null
      const defSuiteId = cfg['qaloop.default_suite_id'] ? Number(cfg['qaloop.default_suite_id']) : null

      if (defRepo) {
        setRepo(defRepo)
        try {
          const bs = await getAdoBranches(defRepo)
          setBranches(bs)
          if (defBranch) setBranch(defBranch)
          else if (bs.includes('main')) setBranch('main')
        } catch { /* ignore */ }
      }
      if (defPlanId) {
        setPlanId(defPlanId)
        try {
          const [ps, ss] = await Promise.all([getPlans(), getSuites(defPlanId)])
          setPlans(ps)
          setSuites(ss)
          if (defSuiteId) setSuiteId(defSuiteId)
        } catch { /* ignore */ }
      }
    }).catch(() => { /* best-effort */ })
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

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
      const req: ScanStartRequest = {
        module: fmodule.moduleName,               // derived from the frontend module
        frontendModule: fmodule.routePath,
        frontendPath: fmodule.componentPath,      // scan traces its apis/ to the backend (source dirs derived)
        repository: repo || undefined,
        branch: branch || undefined,
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
        <Autocomplete
          options={plans} getOptionLabel={p => `${p.id} — ${p.name}`}
          value={plans.find(p => p.id === planId) ?? null}
          isOptionEqualToValue={(a, b) => a.id === b.id}
          onOpen={loadPlans}
          onChange={(_, v) => { setPlanId(v?.id ?? null); setSuiteId(null); setSuites([]); if (v) loadSuites(v.id) }}
          renderInput={p => <TextField {...p} label="Test Plan (optional)" size="small" />}
        />
        {planId && (
          <Autocomplete
            options={suites} getOptionLabel={s => `${s.id} — ${s.name}`}
            value={suites.find(s => s.id === suiteId) ?? null}
            isOptionEqualToValue={(a, b) => a.id === b.id}
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

function CoverageMatrix({ session, onSaved, readOnly }: { session: ApiSessionDetail; onSaved: () => void; readOnly?: boolean }) {
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
  const [justSaved, setJustSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  // Clears the "Saved" confirmation the moment the selection changes again.
  function toggle(id: string, checked: boolean) {
    setJustSaved(false)
    setSelected(prev => { const s = new Set(prev); if (checked) s.add(id); else s.delete(id); return s })
  }

  async function save() {
    setSaving(true); setSaveError(null); setJustSaved(false)
    try {
      // Full-replace: resend EVERY row with its current Selected flag, not just the checked ones —
      // omitting a row here deletes it from the matrix.
      const updated = rows.map(r => ({ ...r, Selected: selected.has(r.Id) }))
      await saveAnalysis(session.id, module, updated)
      setJustSaved(true)   // visible confirmation — the save is otherwise invisible (selection unchanged)
      onSaved()
    } catch (e) { setSaveError(axiosMessage(e)) }
    finally { setSaving(false) }
  }

  if (rows.length === 0)
    return <EmptyState icon={<QrCode2Icon />} title="No coverage data yet" hint="Wait for the scan to complete." />

  const covered = rows.filter(r => r.Status === 'covered')
  const selCount = selected.size

  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
        <Typography variant="body2">{selCount} of {rows.length} selected{!readOnly && ` → will generate ${selCount}`}</Typography>
        <Box sx={{ flex: 1 }} />
        {!readOnly && covered.length > 0 && (
          <Button size="small" onClick={() => {
            setJustSaved(false)
            setSelected(new Set(rows.filter(r => r.Status !== 'covered').map(r => r.Id)))
          }}>Deselect all covered</Button>
        )}
        {!readOnly && justSaved && <Typography variant="body2" color="success.main">Saved ✓</Typography>}
        {!readOnly && saveError && <Typography variant="body2" color="error.main">{saveError}</Typography>}
        {readOnly
          ? <Typography variant="body2" color="success.main">Saved ✓</Typography>
          : <Button size="small" variant="contained" disabled={saving} onClick={save}
              startIcon={saving ? <CircularProgress size={14} /> : undefined}>Save</Button>}
      </Stack>
      <Stack spacing={0.5}>
        {rows.map(r => (
          <Stack key={r.Id} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Checkbox size="small" checked={selected.has(r.Id)}
              onChange={e => toggle(r.Id, e.target.checked)} disabled={readOnly} />
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

// Strip the machinery prefix (`[<tcId-pending>]`, `[<tcId-pending:N>]`, or the pushed `[123456]`)
// from a title for display. The tag stays in stored titles for the push-time rewrite; UI just hides it.
const stripTitleTag = (title: string): string =>
  title.replace(/^\[(?:<tcId-pending(?::\d+)?>|\d+)\]\s*/, '')

function DraftsByLevel({ session, refetch, onGenerate, readOnly }: { session: ApiSessionDetail; refetch: () => void; onGenerate: () => void; readOnly?: boolean }) {
  const wiUrl = useAdoWorkItemUrl()
  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  // ApiTestDraftDto.level is TestLevel.ToWire() — lowercase ("contract"/"endpoint"/"workflow") — not
  // the capitalized tab labels; compare case-insensitively rather than assuming either casing.
  const levels = ['Contract', 'Endpoint', 'Workflow']
  const [tab, setTab] = useState(0)
  const [openDraftId, setOpenDraftId] = useState<number | null>(null)

  const byLevel = (l: string) => drafts.filter(d => d.level.toLowerCase() === l.toLowerCase())
  const hasApprovable = drafts.some(d => d.status !== 'Approved' && d.status !== 'Rejected' && d.status !== 'Pushed')
  const openDraft = drafts.find(d => d.id === openDraftId) ?? null
  const stepCount = (json: string | null | undefined) => {
    try { const v = JSON.parse(json ?? '[]'); return Array.isArray(v) ? v.length : 0 } catch { return 0 }
  }

  async function doApprove(id: number) {
    try { await patchDraft(id, { status: 'Approved' }); refetch() } catch { /* ignore */ }
  }
  async function doReject(id: number) {
    try { await patchDraft(id, { status: 'Rejected' }); refetch() } catch { /* ignore */ }
  }
  async function doApproveAll() {
    try { await approveAllDrafts(session.id); refetch() } catch { /* ignore */ }
  }
  async function saveOpenDraft(patch: { title?: string; preconditions?: string; steps?: { action: string; expected: string }[] }) {
    if (!openDraft) return
    const body: { title?: string; preconditions?: string; stepsJson?: string } = {}
    if (patch.title !== undefined) {
      // Re-attach the machinery tag stripped for display so push-time rewrite still finds it.
      const tag = openDraft.title.match(/^\[(?:<tcId-pending(?::\d+)?>|\d+)\]/)?.[0]
      body.title = tag ? `${tag} ${patch.title}` : patch.title
    }
    if (patch.preconditions !== undefined) body.preconditions = patch.preconditions
    if (patch.steps !== undefined)         body.stepsJson = JSON.stringify(patch.steps)
    await patchDraft(openDraft.id, body); refetch()
  }

  if (drafts.length === 0) {
    // Mid-generation: drafts arrive when status leaves Drafting/Analyzing.
    if (session.status === 'Drafting' || session.status === 'Analyzing')
      return (
        <Box sx={{ py: 2, textAlign: 'center' }}>
          <Typography variant="body2" sx={{ mb: 1 }}>Generating drafts…</Typography>
          <LinearProgress />
        </Box>
      )
    return (
      <Box sx={{ textAlign: 'center' }}>
        <EmptyState icon={<PushPinIcon />} title="No drafts yet"
          hint="Generate Playwright drafts for the selected surfaces." />
        <Button variant="contained" size="small" onClick={onGenerate} sx={{ mt: 1 }}>Generate drafts</Button>
      </Box>
    )
  }

  return (
    <Box>
      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
        <Typography variant="body2">{drafts.length} draft{drafts.length !== 1 ? 's' : ''}</Typography>
        <Box sx={{ flex: 1 }} />
        {!readOnly && hasApprovable && <Button size="small" variant="outlined" onClick={doApproveAll}>Approve all</Button>}
        {!readOnly && !hasApprovable && <Typography variant="body2" color="success.main">Approved ✓</Typography>}
      </Stack>
      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 1 }}>
        {levels.map((l, i) => <Tab key={l} value={i} label={`${l} (${byLevel(l).length})`} sx={{ textTransform: 'none', minHeight: 36 }} />)}
      </Tabs>
      <Stack spacing={1}>
        {byLevel(levels[tab]).map(d => (
          <Card key={d.id} variant="outlined"
            sx={{ transition: 'border-color .15s, box-shadow .15s', '&:hover': { borderColor: 'primary.main', boxShadow: 1 } }}>
            <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <StatusChip value={d.status} kind="draft" />
                <Typography variant="body2"
                  sx={{ flex: 1, fontWeight: 500, cursor: 'pointer', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                  title={stripTitleTag(d.title)} onClick={() => setOpenDraftId(d.id)}>{stripTitleTag(d.title)}</Typography>
                <Chip size="small" variant="outlined"
                  label={`${stepCount(d.stepsJson)} step${stepCount(d.stepsJson) === 1 ? '' : 's'}`} />
                {d.adoTestCaseId && <Chip size="small" color="success" variant="outlined" label={`TC #${d.adoTestCaseId}`} />}
                {!readOnly && d.status !== 'Pushed' && d.status !== 'Rejected' && (
                  <>
                    {d.status !== 'Approved' && <Tooltip title="Approve"><IconButton size="small" color="success" onClick={() => doApprove(d.id)}><CheckIcon fontSize="small" /></IconButton></Tooltip>}
                    <Tooltip title="Reject"><IconButton size="small" color="error" onClick={() => doReject(d.id)}><CloseIcon fontSize="small" /></IconButton></Tooltip>
                  </>
                )}
                <Button size="small" variant="outlined" startIcon={<VisibilityIcon />}
                  onClick={() => setOpenDraftId(d.id)}>View</Button>
              </Stack>
            </CardContent>
          </Card>
        ))}
      </Stack>

      {openDraft && (
        <DraftDetailDialog
          open={openDraftId !== null}
          onClose={() => setOpenDraftId(null)}
          draft={{ ...openDraft, title: stripTitleTag(openDraft.title), stepsJson: openDraft.stepsJson ?? '' }}
          readOnly={Boolean(readOnly) || openDraft.status === 'Pushed' || openDraft.status === 'Rejected'}
          onSave={saveOpenDraft}
          adoUrl={wiUrl}
        />
      )}
    </Box>
  )
}

// ── Push panel ────────────────────────────────────────────────────────────────

// Push reads Plan/Suite/Story from the session itself (set at scan time) — the endpoint takes no
// body at all, so there is nothing to pick here. Show what will be used and let the user push,
// or point them back at a new scan if the session was never given a Plan/Suite.
function PushPanel({ session, refetch }: { session: ApiSessionDetail; refetch: () => void }) {
  const wiUrl = useAdoWorkItemUrl()
  const [pushing, setPushing]       = useState(false)
  const [error, setError]           = useState('')
  const [plans, setPlans]           = useState<TestPlan[]>([])
  const [suites, setSuites]         = useState<TestSuite[]>([])
  const [localPlanId, setLocalPlanId]   = useState<number | null>(null)
  const [localSuiteId, setLocalSuiteId] = useState<number | null>(null)
  const [savingPlan, setSavingPlan] = useState(false)
  const [planError, setPlanError]   = useState('')

  const [pushTab, setPushTab] = useState(0)

  const drafts   = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const pushed   = drafts.filter(d => d.status === 'Pushed').length
  const approved = drafts.filter(d => d.status === 'Approved').length
  const canPush  = Boolean(session.testPlanId && session.testSuiteId)
  const isDone   = session.status === 'Done'
  const isPushing = session.status === 'Pushing'
  const pushedDrafts = drafts.filter(d => d.status === 'Pushed' && d.adoTestCaseId)
  const PUSH_LEVELS = ['Contract', 'Endpoint', 'Workflow'] as const
  const byPushLevel = (l: string) => pushedDrafts.filter(d => d.level.toLowerCase() === l.toLowerCase())

  async function doPush() {
    setPushing(true); setError('')
    try { await pushSession(session.id); refetch() }
    catch (err) { setError(axiosMessage(err)) }
    finally { setPushing(false) }
  }

  async function doCancel() {
    try { await cancelSession(session.id); refetch() } catch { /* ignore */ }
  }

  async function loadPlans() {
    try { setPlans(await getPlans()) } catch { /* ignore */ }
  }

  async function loadSuites(pid: number) {
    try { setSuites(await getSuites(pid)) } catch { setSuites([]) }
  }

  async function savePlan() {
    if (!localPlanId || !localSuiteId) return
    setSavingPlan(true); setPlanError('')
    try { await updateSessionPlan(session.id, { testPlanId: localPlanId, testSuiteId: localSuiteId }); refetch() }
    catch (e) { setPlanError(axiosMessage(e)) }
    finally { setSavingPlan(false) }
  }

  return (
    <Stack spacing={2}>
      {error && <Alert severity="error">{error}</Alert>}
      <Typography variant="body2">
        {isDone
          ? `${pushed} draft${pushed !== 1 ? 's' : ''} pushed to ADO.`
          : `${approved} approved draft${approved !== 1 ? 's' : ''} will be pushed.`}
      </Typography>

      {canPush ? (
        <Stack direction="row" spacing={1}>
          <Chip size="small" label={`Plan #${session.testPlanId}`} />
          <Chip size="small" label={`Suite #${session.testSuiteId}`} />
          {session.workItemId && <Chip size="small" label={`Story #${session.workItemId}`} />}
        </Stack>
      ) : !isDone && (
        <Stack spacing={1.5}>
          {planError && <Alert severity="error">{planError}</Alert>}
          <Autocomplete
            options={plans} getOptionLabel={p => `${p.id} — ${p.name}`}
            value={plans.find(p => p.id === localPlanId) ?? null}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            onOpen={loadPlans}
            onChange={(_, v) => { setLocalPlanId(v?.id ?? null); setLocalSuiteId(null); setSuites([]); if (v) loadSuites(v.id) }}
            renderInput={p => <TextField {...p} label="Test Plan" size="small" required />}
          />
          <Autocomplete
            options={suites} getOptionLabel={s => `${s.id} — ${s.name}`}
            value={suites.find(s => s.id === localSuiteId) ?? null}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            disabled={!localPlanId}
            onChange={(_, v) => setLocalSuiteId(v?.id ?? null)}
            renderInput={p => <TextField {...p} label="Test Suite" size="small" required
              helperText={!localPlanId ? 'Pick a plan first' : undefined} />}
          />
          <Box>
            <Button size="small" variant="outlined" disabled={!localPlanId || !localSuiteId || savingPlan}
              onClick={savePlan}
              startIcon={savingPlan ? <CircularProgress size={14} /> : undefined}>
              Save & enable push
            </Button>
          </Box>
        </Stack>
      )}

      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        {isDone
          ? <Typography variant="body2" color="success.main">Pushed ✓</Typography>
          : <Button variant="contained" disabled={pushing || isPushing || !canPush || approved === 0} onClick={doPush}
              startIcon={(pushing || isPushing) ? <CircularProgress size={16} /> : <PushPinIcon />}>
              {isPushing ? 'Pushing…' : `Push${approved > 0 ? ` (${approved})` : ''}`}
            </Button>}
        {!['Done', 'Failed', 'Cancelled'].includes(session.status) && (
          <Button color="error" onClick={doCancel}>Cancel scan</Button>
        )}
      </Stack>

      {pushedDrafts.length > 0 && (
        <Card variant="outlined" sx={{ borderColor: 'success.main' }}>
          <CardContent>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
              <CheckIcon color="success" fontSize="small" />
              <Typography variant="subtitle2">{pushedDrafts.length} test case{pushedDrafts.length !== 1 ? 's' : ''} pushed to ADO</Typography>
            </Stack>
            <Tabs value={pushTab} onChange={(_, v) => setPushTab(v)} sx={{ mb: 1 }}>
              {PUSH_LEVELS.map((l, i) => (
                <Tab key={l} value={i} label={`${l} (${byPushLevel(l).length})`} sx={{ textTransform: 'none', minHeight: 36 }} />
              ))}
            </Tabs>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ width: 96 }}>TC ID</TableCell>
                  <TableCell>Title</TableCell>
                  <TableCell sx={{ width: 48 }} />
                </TableRow>
              </TableHead>
              <TableBody>
                {byPushLevel(PUSH_LEVELS[pushTab]).map(d => (
                  <TableRow key={d.id}>
                    <TableCell><strong>#{d.adoTestCaseId}</strong></TableCell>
                    <TableCell>{stripTitleTag(d.title)}</TableCell>
                    <TableCell>
                      <Tooltip title="Open in Azure DevOps">
                        <IconButton size="small" component="a" href={wiUrl(d.adoTestCaseId!)} target="_blank" rel="noreferrer">
                          <OpenInNewIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                ))}
                {byPushLevel(PUSH_LEVELS[pushTab]).length === 0 && (
                  <TableRow>
                    <TableCell colSpan={3} sx={{ textAlign: 'center', color: 'text.secondary', py: 2 }}>
                      No {PUSH_LEVELS[pushTab]} test cases pushed.
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
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

      {tab === 'coverage' && <CoverageMatrix session={s} onSaved={() => { refetch(); setTab('drafts') }} readOnly={!['Scanning', 'AwaitingReview'].includes(s.status)} />}
      {tab === 'drafts' && <DraftsByLevel session={s} refetch={refetch} onGenerate={doGenerate} readOnly={s.status === 'Done'} />}
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
  deleteSession: (id) => deleteSession(id),
  StartForm: QaLoopStartForm,
  DetailBody: QaLoopDetailBody,
}
