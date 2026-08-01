import { useState, useEffect } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  FormControlLabel, IconButton, LinearProgress, Stack, Switch, Tab, Table, TableBody,
  TableCell, TableHead, TableRow, Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import PushPinIcon from '@mui/icons-material/PushPin'
import QrCode2Icon from '@mui/icons-material/QrCode2'
import RefreshIcon from '@mui/icons-material/Refresh'
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
  rerunAnalyze, refineDrafts, authorUiCases, generateUiScripts,
  patchUiSuite, rerunUiAnalysis, runUiScripts, explainFailure,
  type ApiSessionDetail, type ApiTestDraftDto, type ScanStartRequest, type SurfaceRow,
  type FrontendModule, type ApiDraftFeedback,
} from '../../api/qaLoop'
import { getPlans, getSuites, searchWorkItems } from '../../api/authorTests'
import type { TestPlan, TestSuite } from '../../api/authorTests'
import { getAdoRepositories, getAdoBranches, getConfig } from '../../api/settings'
import type { WorkItemOption } from '../types'
import { PROJECT_ID } from '../../config/env'

// ── Pipeline ──────────────────────────────────────────────────────────────────

const PIPELINE_STEPS: PipelineStep[] = [
  { key: 'retrieve-kb', label: 'Retrieve KB', icon: <QrCode2Icon /> },
  { key: 'review',      label: 'Coverage',    icon: <CheckIcon /> },
  { key: 'drafts',      label: 'Drafts',      icon: <PushPinIcon /> },
  { key: 'push',        label: 'Push',        icon: <CheckIcon /> },
]

// eslint-disable-next-line @typescript-eslint/no-unused-vars
function activeStepIndex(status: string, _activity: string | null): number {
  switch (status) {
    case 'RetrievingKb':
    case 'Scanning':       return 0
    case 'Analyzing':
    case 'AwaitingReview': return 1
    case 'Drafting':       return 2
    case 'Drafted':
    case 'Pushing':
    case 'ApiDone':
    case 'Done':           return 3
    default:               return 0
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
  const [additionalContext, setAdditionalContext] = useState('')
  const [sourceDirs, setSourceDirs]       = useState('')
  const [hasUiPhase, setHasUiPhase]   = useState(false)

  // Load saved defaults from Settings and pre-populate fields on mount.
  useEffect(() => {
    getConfig().then(async cfg => {
      // Per-QA-Loop defaults override the shared Azure DevOps Settings. When the former are unset,
      // scan forms should inherit the repository + default branch already configured for ADO.
      const defRepo   = String(cfg['qaloop.default_repository'] || cfg['ado.repository'] || '')
      const defBranch = String(cfg['qaloop.default_branch'] || cfg['ado.default_branch'] || '')
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
    try { setFmodules(await listFrontendModules(repo ?? undefined, branch ?? undefined)) } catch { /* ignore */ }
    finally { setFmLoading(false) }
  }
  async function rediscoverModules() {
    setFmLoading(true)
    try { setFmodules(await listFrontendModules(repo ?? undefined, branch ?? undefined, true)) } catch { /* ignore */ }
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
        additionalContext: additionalContext.trim() || undefined,
        sourceDirs: sourceDirs.split(/[\n;,]/).map(dir => dir.trim()).filter(Boolean),
        hasUiPhase: hasUiPhase || undefined,
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
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
          <Autocomplete sx={{ flex: 1 }}
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
          <Tooltip title="Rediscover modules — re-scan the repo (uses AI when the route file isn't found)">
            <span>
              <IconButton onClick={rediscoverModules} disabled={fmLoading} sx={{ mt: 0.5 }} aria-label="Rediscover modules">
                {fmLoading ? <CircularProgress size={18} /> : <RefreshIcon fontSize="small" />}
              </IconButton>
            </span>
          </Tooltip>
        </Stack>
        <Autocomplete
          options={repos} value={repo} loading={reposLoading}
          onOpen={loadRepos}
          onChange={(_, v) => {
            setRepo(v)
            setBranch(null)
            setBranches([])
            setFmodule(null)
            setFmodules([])
            if (v) loadBranches(v)
          }}
          renderInput={p => <TextField {...p} label="Repository (optional)" size="small"
            helperText={reposLoading ? 'Loading repositories…' : undefined} />}
        />
        <Autocomplete
          options={branches} value={branch} loading={branchesLoading} disabled={!repo}
          onOpen={() => { if (repo) loadBranches(repo) }}
          onChange={(_, v) => {
            setBranch(v)
            setFmodule(null)
            setFmodules([])
          }}
          renderInput={p => <TextField {...p} label="Branch (optional)" size="small"
            helperText={!repo ? 'Pick a repository first' : branchesLoading ? 'Loading branches…' : undefined} />}
        />
        <TextField
          label="Source directories override (optional)"
          size="small"
          fullWidth
          multiline
          minRows={2}
          value={sourceDirs}
          onChange={e => setSourceDirs(e.target.value)}
          helperText="One repo-relative directory per line. Leave blank for smart branch discovery."
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
        <TextField
          label="Additional context (optional)" size="small" multiline minRows={2}
          value={additionalContext} onChange={e => setAdditionalContext(e.target.value)}
          helperText="What to focus on, what to skip, or any scope notes for the analyzer"
        />
        <FormControlLabel
          control={<Switch checked={hasUiPhase} onChange={e => setHasUiPhase(e.target.checked)} size="small" />}
          label={<Typography variant="body2">Include UI phase (Playwright)</Typography>}
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
  const levels = ['Contract', 'Endpoint', 'Workflow']
  const [tab, setTab] = useState(0)
  const [openDraftId, setOpenDraftId] = useState<number | null>(null)
  const [perFeedback, setPerFeedback] = useState<Record<number, string>>({})
  const [perRemove, setPerRemove]     = useState<Record<number, boolean>>({})
  const [overallFeedback, setOverallFeedback] = useState(session.lastFeedback ?? '')
  const [refining, setRefining] = useState(false)

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
  async function doRefine() {
    const perDraft: ApiDraftFeedback[] = drafts
      .filter(d => d.status !== 'Approved' && d.status !== 'Pushed')
      .map(d => ({ draftId: d.id, feedback: perFeedback[d.id] || undefined, remove: perRemove[d.id] ?? false }))
      .filter(f => f.feedback || f.remove)
    if (perDraft.length === 0 && !overallFeedback.trim()) return
    setRefining(true)
    try {
      await refineDrafts(session.id, { overallFeedback: overallFeedback.trim() || undefined, perDraft })
      setPerFeedback({}); setPerRemove({})
      refetch()
    } catch { /* ignore */ }
    finally { setRefining(false) }
  }
  async function saveOpenDraft(patch: { title?: string; preconditions?: string; steps?: { action: string; expected: string }[] }) {
    if (!openDraft) return
    const body: { title?: string; preconditions?: string; stepsJson?: string } = {}
    if (patch.title !== undefined) {
      const tag = openDraft.title.match(/^\[(?:<tcId-pending(?::\d+)?>|\d+)\]/)?.[0]
      body.title = tag ? `${tag} ${patch.title}` : patch.title
    }
    if (patch.preconditions !== undefined) body.preconditions = patch.preconditions
    if (patch.steps !== undefined)         body.stepsJson = JSON.stringify(patch.steps)
    await patchDraft(openDraft.id, body); refetch()
  }

  const mutableStatuses = ['Draft', 'Rejected']
  const hasMutableDrafts = drafts.some(d => mutableStatuses.includes(d.status))

  if (drafts.length === 0) {
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
        {byLevel(levels[tab]).map(d => {
          const locked = d.status === 'Approved' || d.status === 'Pushed'
          return (
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
                {!readOnly && !locked && (
                  <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'center' }}>
                    <TextField size="small" placeholder="Per-test feedback…" variant="outlined"
                      sx={{ flex: 1 }} value={perFeedback[d.id] ?? ''}
                      onChange={e => setPerFeedback(prev => ({ ...prev, [d.id]: e.target.value }))} />
                    <Tooltip title="Flag for removal">
                      <Checkbox size="small" checked={perRemove[d.id] ?? false}
                        onChange={e => setPerRemove(prev => ({ ...prev, [d.id]: e.target.checked }))}
                        color="error" />
                    </Tooltip>
                    <Typography variant="caption" color="text.secondary">Remove</Typography>
                  </Stack>
                )}
              </CardContent>
            </Card>
          )
        })}
      </Stack>

      {!readOnly && hasMutableDrafts && (
        <Box sx={{ mt: 2 }}>
          <TextField label="Overall feedback (optional)" size="small" multiline minRows={2} fullWidth
            value={overallFeedback} onChange={e => setOverallFeedback(e.target.value)}
            helperText="Applied to all refined drafts alongside any per-test notes"
            disabled={refining}
          />
          <Button variant="contained" size="small" sx={{ mt: 1 }}
            onClick={doRefine}
            disabled={refining || (Object.values(perFeedback).every(v => !v) && Object.values(perRemove).every(v => !v) && !overallFeedback.trim())}>
            {refining ? 'Regenerating…' : 'Regenerate with feedback'}
          </Button>
        </Box>
      )}

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
  const isDone   = session.status === 'Done' || session.status === 'ApiDone'
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

// ── UI Phase panel ────────────────────────────────────────────────────────────

const BUCKET_COLORS: Record<string, 'success' | 'warning' | 'error' | 'info'> = {
  smoke: 'success', regression: 'warning', e2e: 'info',
}

interface UiAnalystOutput {
  features?: { id?: string; name?: string; description?: string }[]
  flows?: { name?: string; trigger?: string; steps?: string[]; breaks?: string[] }[]
  screens?: { name?: string; route?: string; components?: string[] }[]
  openQuestions?: string[]
  mustTest?: string[]
  mustNotTest?: string[]
  authPermissionsSummary?: string
  dmsVariantsSummary?: string
}

function parseUiAnalysis(json: string | null | undefined): UiAnalystOutput | null {
  if (!json) return null
  try { return JSON.parse(json) as UiAnalystOutput } catch { return null }
}

function ExplainFailureCard({
  sessionId, draft, onError,
}: {
  sessionId: number
  draft: ApiTestDraftDto
  refetch: () => void
  onError: (e: unknown, msg: string) => void
}) {
  const [explanation, setExplanation] = useState<string | null>(null)
  const [explaining, setExplaining] = useState(false)

  async function doExplain() {
    setExplaining(true)
    try {
      const res = await explainFailure(sessionId, draft.id)
      setExplanation(res.explanation)
    } catch (e) { onError(e, 'Failed to explain failure') }
    finally { setExplaining(false) }
  }

  return (
    <Stack spacing={1} sx={{ mt: 1, p: 1.5, bgcolor: 'action.hover', borderRadius: 1 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Typography variant="caption" color="error.main" sx={{ flex: 1 }}>
          {draft.errorMessage}
        </Typography>
        <Button size="small" variant="outlined" onClick={doExplain} disabled={explaining}
          startIcon={explaining ? <CircularProgress size={12} /> : undefined}>
          {explaining ? 'Explaining…' : explanation ? 'Re-explain' : 'Explain'}
        </Button>
      </Stack>
      {explanation && (
        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{explanation}</Typography>
      )}
      <Typography variant="caption" color="text.secondary">
        Failed UI specs are auto-healed and re-run (up to 3 attempts) when you click Run Scripts.
      </Typography>
      {draft.adoTestCaseId != null && (
        <Tooltip title={`File a bug linked to TC #${draft.adoTestCaseId} (after push)`}>
          <span>
            <Button size="small" variant="outlined" color="error" disabled>
              Draft Bug
            </Button>
          </span>
        </Tooltip>
      )}
    </Stack>
  )
}

function UiSuitePicker({
  session, refetch, onError,
}: {
  session: ApiSessionDetail
  refetch: () => void
  onError: (e: unknown, msg: string) => void
}) {
  const [suites, setSuites] = useState<TestSuite[]>([])
  const [saving, setSaving] = useState(false)
  const planId = session.testPlanId ?? null

  async function loadSuites() {
    if (!planId) return
    try { setSuites(await getSuites(planId)) } catch { setSuites([]) }
  }

  async function onPick(suiteId: number | null) {
    setSaving(true)
    try { await patchUiSuite(session.id, suiteId); refetch() }
    catch (e) { onError(e, 'Failed to update UI suite') }
    finally { setSaving(false) }
  }

  if (!planId) {
    return <Alert severity="info">Set a Test Plan on the Push tab before choosing a UI suite.</Alert>
  }

  return (
    <Autocomplete
      options={suites}
      getOptionLabel={s => `${s.id} — ${s.name}`}
      value={suites.find(s => s.id === session.uiTestSuiteId) ?? null}
      isOptionEqualToValue={(a, b) => a.id === b.id}
      onOpen={loadSuites}
      disabled={saving}
      onChange={(_, v) => onPick(v?.id ?? null)}
      renderInput={p => (
        <TextField {...p} label="UI Test Suite (same plan)" size="small"
          helperText={session.uiTestSuiteId
            ? `Suite #${session.uiTestSuiteId}`
            : `Falls back to API suite #${session.testSuiteId ?? '—'}`} />
      )}
    />
  )
}

function UiAnalysisCard({ analysis }: { analysis: UiAnalystOutput }) {
  return (
    <Card variant="outlined">
      <CardContent>
        <Typography variant="subtitle2" gutterBottom>UI Analysis</Typography>
        {analysis.authPermissionsSummary && (
          <Typography variant="body2" sx={{ mb: 1 }}><b>Auth:</b> {analysis.authPermissionsSummary}</Typography>
        )}
        {analysis.dmsVariantsSummary && (
          <Typography variant="body2" sx={{ mb: 1 }}><b>DMS:</b> {analysis.dmsVariantsSummary}</Typography>
        )}
        {analysis.features && analysis.features.length > 0 && (
          <Box sx={{ mb: 1 }}>
            <Typography variant="caption" color="text.secondary">Features</Typography>
            <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5, mt: 0.5 }}>
              {analysis.features.map((f, i) => (
                <Chip key={f.id ?? i} size="small" label={f.name ?? f.id ?? `feature-${i}`} />
              ))}
            </Stack>
          </Box>
        )}
        {analysis.flows && analysis.flows.length > 0 && (
          <Box sx={{ mb: 1 }}>
            <Typography variant="caption" color="text.secondary">Flows</Typography>
            {analysis.flows.map((f, i) => (
              <Typography key={i} variant="body2">• {f.name}{f.trigger ? ` — ${f.trigger}` : ''}</Typography>
            ))}
          </Box>
        )}
        {analysis.screens && analysis.screens.length > 0 && (
          <Box sx={{ mb: 1 }}>
            <Typography variant="caption" color="text.secondary">Screens</Typography>
            {analysis.screens.map((s, i) => (
              <Typography key={i} variant="body2">• {s.name}{s.route ? ` (${s.route})` : ''}</Typography>
            ))}
          </Box>
        )}
        {analysis.mustTest && analysis.mustTest.length > 0 && (
          <Box sx={{ mb: 1 }}>
            <Typography variant="caption" color="text.secondary">Must test</Typography>
            {analysis.mustTest.map((m, i) => <Typography key={i} variant="body2">• {m}</Typography>)}
          </Box>
        )}
        {analysis.mustNotTest && analysis.mustNotTest.length > 0 && (
          <Box>
            <Typography variant="caption" color="text.secondary">Must not test</Typography>
            {analysis.mustNotTest.map((m, i) => <Typography key={i} variant="body2">• {m}</Typography>)}
          </Box>
        )}
        {analysis.openQuestions && analysis.openQuestions.length > 0 && (
          <Alert severity="warning" sx={{ mt: 1 }}>
            {analysis.openQuestions.map((q, i) => <div key={i}>{q}</div>)}
          </Alert>
        )}
      </CardContent>
    </Card>
  )
}

function UiPhasePanel({ session, refetch, onError }: { session: ApiSessionDetail; refetch: () => void; onError: (e: unknown, msg: string) => void }) {
  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const uiCases   = drafts.filter(d => d.draftKind === 'ui_case')
  const uiScripts = drafts.filter(d => d.draftKind === 'ui_script')

  const [authoring, setAuthoring]     = useState(false)
  const [generating, setGenerating]   = useState(false)
  const [running, setRunning]         = useState(false)
  const [rerunning, setRerunning]     = useState(false)
  const [uiFeedback, setUiFeedback]   = useState('')
  const [openSpecId, setOpenSpecId]   = useState<number | null>(null)
  const [explainDraftId, setExplainDraftId] = useState<number | null>(null)

  const uiStatus = session.uiStatus ?? null
  const analysis = parseUiAnalysis(session.uiAnalysisJson)
  const pushedUiCount = uiScripts.filter(d => d.status === 'Pushed').length

  async function doAuthorUiCases() {
    setAuthoring(true)
    try { await authorUiCases(session.id); refetch() }
    catch (e) { onError(e, 'Failed to author UI cases') }
    finally { setAuthoring(false) }
  }

  async function doRerunUiAnalysis() {
    if (!uiFeedback.trim()) return
    setRerunning(true)
    try { await rerunUiAnalysis(session.id, uiFeedback.trim()); refetch() }
    catch (e) { onError(e, 'Failed to rerun UI analysis') }
    finally { setRerunning(false) }
  }

  async function doGenerateUiScripts() {
    setGenerating(true)
    try { await generateUiScripts(session.id); refetch() }
    catch (e) { onError(e, 'Failed to generate UI scripts') }
    finally { setGenerating(false) }
  }

  async function doRunUiScripts() {
    setRunning(true)
    try { await runUiScripts(session.id); refetch() }
    catch (e) { onError(e, 'Failed to run UI scripts') }
    finally { setRunning(false) }
  }

  async function doApproveCase(id: number) {
    try { await patchDraft(id, { status: 'Approved' }); refetch() } catch { /* ignore */ }
  }

  async function doRejectCase(id: number) {
    try { await patchDraft(id, { status: 'Rejected' }); refetch() } catch { /* ignore */ }
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Typography variant="subtitle2">UI Phase</Typography>
        {uiStatus && <Chip size="small" label={uiStatus} variant="outlined" />}
        {session.frsSlug && <Chip size="small" label={`KB: ${session.frsSlug}`} color="info" variant="outlined" />}
      </Stack>

      {/* Suite picker — always visible in UI Phase tab */}
      <UiSuitePicker session={session} refetch={refetch} onError={onError} />

      {/* AnalyzingUi */}
      {uiStatus === 'AnalyzingUi' && (
        <Box>
          <Typography variant="body2" sx={{ mb: 1 }}>Running UI Analysis…</Typography>
          <LinearProgress />
        </Box>
      )}

      {/* AwaitingUiAnalysisReview */}
      {uiStatus === 'AwaitingUiAnalysisReview' && (
        <Stack spacing={2}>
          {analysis
            ? <UiAnalysisCard analysis={analysis} />
            : <Alert severity="warning">UI analysis JSON is missing or invalid.</Alert>}
          <TextField
            label="Refine UI analysis" size="small" multiline minRows={2} fullWidth
            value={uiFeedback} onChange={e => setUiFeedback(e.target.value)}
            helperText="Describe gaps or corrections — the UI Analyst will improve the existing analysis"
            disabled={rerunning}
          />
          <Stack direction="row" spacing={1}>
            <Button variant="outlined" size="small" onClick={doRerunUiAnalysis}
              disabled={rerunning || !uiFeedback.trim()}
              startIcon={rerunning ? <CircularProgress size={14} /> : undefined}>
              {rerunning ? 'Refining…' : 'Rerun UI Analysis'}
            </Button>
            <Button variant="contained" size="small" onClick={doAuthorUiCases} disabled={authoring}
              startIcon={authoring ? <CircularProgress size={14} /> : undefined}>
              {authoring ? 'Authoring…' : 'Author Cases'}
            </Button>
          </Stack>
        </Stack>
      )}

      {/* Authoring */}
      {uiStatus === 'Authoring' && (
        <Box>
          <Typography variant="body2" sx={{ mb: 1 }}>Authoring test cases…</Typography>
          <LinearProgress />
        </Box>
      )}

      {/* Legacy Slab 2: null status with Author button was moved — keep entry for old sessions */}
      {uiStatus === null && session.hasUiPhase && !session.uiAnalysisJson && (
        <Button variant="outlined" size="small" onClick={doAuthorUiCases} disabled={authoring}
          startIcon={authoring ? <CircularProgress size={14} /> : undefined}>
          Author UI Cases (legacy)
        </Button>
      )}

      {/* AwaitingReview — UI cases */}
      {uiStatus === 'AwaitingReview' && (
        <Stack spacing={1}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Typography variant="body2">
              {uiCases.length} UI case{uiCases.length !== 1 ? 's' : ''} to review
            </Typography>
            <Box sx={{ flex: 1 }} />
            <Button size="small" variant="outlined" onClick={doGenerateUiScripts}
              disabled={generating || !uiCases.some(d => d.status === 'Approved')}
              startIcon={generating ? <CircularProgress size={14} /> : undefined}>
              {generating ? 'Generating scripts…' : 'Generate Scripts'}
            </Button>
          </Stack>
          {uiCases.map(d => (
            <Card key={d.id} variant="outlined">
              <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <StatusChip value={d.status} kind="draft" />
                  {d.bucket && (
                    <Chip size="small" label={d.bucket} color={BUCKET_COLORS[d.bucket] ?? 'default'} />
                  )}
                  <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {d.title}
                  </Typography>
                  {d.status !== 'Approved' && (
                    <Tooltip title="Approve for script generation">
                      <IconButton size="small" color="success" onClick={() => doApproveCase(d.id)}>
                        <CheckIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  )}
                  {d.status !== 'Rejected' && (
                    <Tooltip title="Reject">
                      <IconButton size="small" color="error" onClick={() => doRejectCase(d.id)}>
                        <CloseIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  )}
                </Stack>
              </CardContent>
            </Card>
          ))}
        </Stack>
      )}

      {/* Drafting */}
      {uiStatus === 'Drafting' && (
        <Box>
          <Typography variant="body2" sx={{ mb: 1 }}>Generating Playwright scripts…</Typography>
          <LinearProgress />
          {uiCases.filter(d => d.status === 'Approved').map(d => (
            <Typography key={d.id} variant="caption" sx={{ display: 'block' }}>
              {d.errorMessage ? `✗ ${d.title}: ${d.errorMessage}` : `… ${d.title}`}
            </Typography>
          ))}
        </Box>
      )}

      {/* Drafted / RunningUiScripts — scripts + run */}
      {(uiStatus === 'Drafted' || uiStatus === 'RunningUiScripts') && (
        <Stack spacing={1}>
          {uiStatus === 'RunningUiScripts' && (
            <Box>
              <Typography variant="body2" sx={{ mb: 1 }}>Running UI scripts…</Typography>
              <LinearProgress />
            </Box>
          )}
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Typography variant="body2">
              {uiScripts.length} UI script{uiScripts.length !== 1 ? 's' : ''}
            </Typography>
            <Box sx={{ flex: 1 }} />
            {uiStatus === 'Drafted' && (
              <Button size="small" variant="contained" onClick={doRunUiScripts}
                disabled={running || !uiScripts.some(d => d.status === 'Approved')}
                startIcon={running ? <CircularProgress size={14} /> : undefined}>
                {running ? 'Starting…' : 'Run Scripts'}
              </Button>
            )}
          </Stack>
          {uiScripts.map(d => (
            <Card key={d.id} variant="outlined">
              <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <StatusChip value={d.status} kind="draft" />
                  {d.bucket && <Chip size="small" label={d.bucket} color={BUCKET_COLORS[d.bucket] ?? 'default'} />}
                  <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {d.title}
                  </Typography>
                  {d.status !== 'Approved' && d.status !== 'Pushed' && (
                    <Tooltip title="Approve for run / push">
                      <IconButton size="small" color="success" onClick={() => doApproveCase(d.id)}>
                        <CheckIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  )}
                  {d.specCode && (
                    <Button size="small" variant="outlined" startIcon={<VisibilityIcon />}
                      onClick={() => setOpenSpecId(openSpecId === d.id ? null : d.id)}>
                      Spec
                    </Button>
                  )}
                  {d.errorMessage && (
                    <Button size="small" color="error" variant="outlined"
                      onClick={() => setExplainDraftId(explainDraftId === d.id ? null : d.id)}>
                      {explainDraftId === d.id ? 'Hide' : 'Explain'}
                    </Button>
                  )}
                </Stack>
                {d.specFilePath && (
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                    {d.specFilePath}
                  </Typography>
                )}
                {openSpecId === d.id && d.specCode && (
                  <Box component="pre" sx={{ mt: 1, p: 1, bgcolor: 'background.default',
                    borderRadius: 1, overflow: 'auto', fontSize: '0.75rem', fontFamily: 'monospace',
                    maxHeight: 320, border: '1px solid', borderColor: 'divider' }}>
                    {d.specCode}
                  </Box>
                )}
                {explainDraftId === d.id && d.errorMessage && (
                  <ExplainFailureCard sessionId={session.id} draft={d} refetch={refetch} onError={onError} />
                )}
              </CardContent>
            </Card>
          ))}
        </Stack>
      )}

      {/* Pushing */}
      {uiStatus === 'Pushing' && (
        <Box>
          <Typography variant="body2" sx={{ mb: 1 }}>Pushing UI tests to ADO…</Typography>
          <LinearProgress />
        </Box>
      )}

      {/* Done */}
      {uiStatus === 'Done' && (
        <Alert severity="success">
          All done — {pushedUiCount} UI test{pushedUiCount !== 1 ? 's' : ''} pushed.
        </Alert>
      )}

      {/* Failed / Cancelled */}
      {(uiStatus === 'Failed' || uiStatus === 'Cancelled') && (
        <Alert severity={uiStatus === 'Failed' ? 'error' : 'warning'}>
          UI phase {uiStatus.toLowerCase()}{session.errorMessage ? `: ${session.errorMessage}` : ''}
        </Alert>
      )}
    </Stack>
  )
}

// ── Detail body ───────────────────────────────────────────────────────────────

export function QaLoopDetailBody({ session, refetch, onError, fullScreen }: DetailBodyProps) {
  const s = session as ApiSessionDetail
  const live = !['Done', 'Failed', 'Cancelled'].includes(s.status)
  const stepIdx = activeStepIndex(s.status, s.currentActivity)
  const [tab, setTab] = useState<'detail' | 'coverage' | 'drafts' | 'ui' | 'push'>('detail')
  const [generateCtx, setGenerateCtx] = useState(s.generateContext ?? '')
  const [refineAnalysisFeedback, setRefineAnalysisFeedback] = useState(s.lastFeedback ?? '')
  const [refiningAnalysis, setRefiningAnalysis] = useState(false)
  const [generating, setGenerating] = useState(false)

  async function doGenerate() {
    setGenerating(true)
    try { await generateDrafts(s.id, generateCtx.trim() || undefined); refetch() }
    catch (err) { onError(err, 'Failed to generate drafts') }
    finally { setGenerating(false) }
  }

  async function doRerunAnalyze() {
    if (!refineAnalysisFeedback.trim()) return
    setRefiningAnalysis(true)
    try { await rerunAnalyze(s.id, refineAnalysisFeedback.trim()); refetch() }
    catch (err) { onError(err, 'Failed to rerun analysis') }
    finally { setRefiningAnalysis(false) }
  }

  return (
    <Box sx={{ maxWidth: fullScreen ? undefined : 800 }}>
      <AgentPipeline steps={PIPELINE_STEPS} activeIndex={stepIdx} inFlight={live} failed={s.status === 'Failed'} currentActivity={s.currentActivity ?? null} />
      {live && <Box sx={{ mb: 2 }}><LiveActivityIndicator active text={s.currentActivity ?? 'Processing…'} /></Box>}

      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 2 }}>
        <Tab value="detail" label="Activity" sx={{ textTransform: 'none', minHeight: 36 }} />
        <Tab value="coverage" label="Coverage" sx={{ textTransform: 'none', minHeight: 36 }} />
        <Tab value="drafts" label={`Drafts (${session.drafts?.length ?? 0})`} sx={{ textTransform: 'none', minHeight: 36 }} />
        {s.hasUiPhase && <Tab value="ui" label="UI Phase" sx={{ textTransform: 'none', minHeight: 36 }} />}
        <Tab value="push" label="Push" sx={{ textTransform: 'none', minHeight: 36 }} />
      </Tabs>

      {tab === 'detail' && (
        <Box>
          {s.status === 'AwaitingReview' && (
            <Stack spacing={2} sx={{ mb: 2 }}>
              <Alert severity="info" action={
                <Button color="inherit" size="small" onClick={doGenerate} disabled={generating}>
                  {generating ? 'Generating…' : 'Generate drafts'}
                </Button>
              }>Coverage analysis ready. Generate Playwright drafts when ready.</Alert>
              <TextField
                label="Context for generators (optional)" size="small" multiline minRows={2}
                value={generateCtx} onChange={e => setGenerateCtx(e.target.value)}
                helperText="Scope notes passed to all three generator agents"
              />
              <Box>
                <TextField
                  label="Refine analysis with feedback" size="small" multiline minRows={2} fullWidth
                  value={refineAnalysisFeedback} onChange={e => setRefineAnalysisFeedback(e.target.value)}
                  helperText="Describe gaps or corrections — the Analyzer will improve the existing coverage matrix"
                  disabled={refiningAnalysis}
                />
                <Button variant="outlined" size="small" sx={{ mt: 1 }}
                  onClick={doRerunAnalyze} disabled={refiningAnalysis || !refineAnalysisFeedback.trim()}>
                  {refiningAnalysis ? 'Refining…' : 'Refine analysis'}
                </Button>
              </Box>
            </Stack>
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
      {tab === 'ui' && <UiPhasePanel session={s} refetch={refetch} onError={onError} />}
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
  inFlightStatuses: ['RetrievingKb', 'Scanning', 'Analyzing', 'AwaitingReview', 'Drafting', 'Pushing', 'ApiDone'],
  getUiStatus: (d) => (d as ApiSessionDetail).uiStatus ?? undefined,
  uiInFlightStatuses: ['AnalyzingUi', 'RunningUiScripts'],
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
