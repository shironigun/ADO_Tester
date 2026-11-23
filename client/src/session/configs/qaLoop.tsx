import { useState, useEffect, useRef, type ReactNode } from 'react'
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
import StopIcon from '@mui/icons-material/Stop'
import type { PipelineStep } from '../../components/AgentPipeline'
import AgentConsole from '../../components/AgentConsole'
import AgentPipeline from '../../components/AgentPipeline'
import EmptyState from '../../components/EmptyState'
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
  approveAllDrafts, pushSession, getAutomationGitStatus, syncAutomationGitFromBase, updateSessionPlan, cancelSession, deleteSession, sessionStreamUrl, listFrontendModules, listAutomationUnits, LEVEL_NAMES,
  rerunAnalyze, refineDrafts, authorUiCases, generateUiScripts,
  patchUiSuite, rerunUiAnalysis, saveUiAnalysis, runUiScripts, explainFailure, healSpecs,
  type ApiSessionDetail, type ApiTestDraftDto, type ScanStartRequest, type SurfaceRow,
  type FrontendModule, type AutomationUnitChoice, type ApiDraftFeedback, type ContextAttachment,   type AutomationGitStatus,
  type AutomationGitSyncResult,
} from '../../api/qaLoop'
import UiAnalysisPanel, { parseUiAnalysis, type UiAnalystOutput } from '../../components/UiAnalysisPanel'
import UiCaseReviewPanel from '../UiCaseReviewPanel'
import { getPlans, getSuites, searchWorkItems } from '../../api/authorTests'
import type { TestPlan, TestSuite } from '../../api/authorTests'
import { getAdoRepositories, getAdoBranches, getConfig } from '../../api/settings'
import type { WorkItemOption } from '../types'
import { PROJECT_ID } from '../../config/env'

// ── Pipeline ──────────────────────────────────────────────────────────────────

const PIPELINE_STEPS_API: PipelineStep[] = [
  { key: 'retrieve-kb', label: 'Retrieve KB', icon: <QrCode2Icon /> },
  { key: 'plan-intake', label: 'Plan Intake', icon: <CheckIcon /> },
  { key: 'api-analyst', label: 'API Analyst', icon: <CheckIcon /> },
  { key: 'api-tests', label: 'API Tests', icon: <PushPinIcon /> },
  { key: 'push-api', label: 'Push (API)', icon: <CheckIcon /> },
]

const PIPELINE_STEPS_UI: PipelineStep[] = [
  { key: 'ui-analyst', label: 'UI Analyst', icon: <CheckIcon /> },
  { key: 'ui-tester', label: 'UI Tester', icon: <PushPinIcon /> },
  { key: 'push-ui', label: 'Push (UI)', icon: <CheckIcon /> },
]

function pipelineStepsFor(hasUiPhase: boolean): PipelineStep[] {
  return hasUiPhase ? [...PIPELINE_STEPS_API, ...PIPELINE_STEPS_UI] : PIPELINE_STEPS_API
}

/** Progress index for the rail (highest unlocked). Uses API status + uiStatus. */
function activeStepIndex(status: string, _activity: string | null, uiStatus?: string | null, hasUiPhase?: boolean): number {
  const uiOff = !hasUiPhase
  switch (status) {
    case 'RetrievingKb':
      return 0
    case 'ImportingPlan':
      return 1
    case 'Scanning':
    case 'Analyzing':
    case 'AwaitingReview':
      return 2
    case 'Drafting':
    case 'Drafted':
      return 3
    case 'Pushing':
      return 4
    case 'ApiDone': {
      if (uiOff) return 4
      switch (uiStatus) {
        case 'AnalyzingUi':
        case 'AwaitingUiAnalysisReview':
          return 5
        case 'Authoring':
        case 'AwaitingReview':
        case 'Drafting':
        case 'Drafted':
        case 'RunningUiScripts':
          return 6
        case 'Pushing':
        case 'Done':
          return 7
        default:
          return 5
      }
    }
    case 'Done':
      return uiOff ? 4 : 7
    case 'Failed':
    case 'Cancelled':
      return 0
    default:
      return 0
  }
}

// Back-compat for SessionModuleConfig (signature without ui flags — detail body uses full form).
function activeStepIndexCompat(status: string, activity: string | null): number {
  return activeStepIndex(status, activity, null, false)
}

const ANALYTICS_COLUMNS: AnalyticsColumn[] = [
  { key: 'module', label: 'Module', value: (d: SessionDetail | undefined) => (d as ApiSessionDetail | undefined)?.module ?? '—' },
  { key: 'drafts', label: 'Drafts', align: 'right', value: (d: SessionDetail | undefined) => d?.drafts?.length ?? '—' },
]

const MAX_CONTEXT_ATTACHMENTS = 50
const MAX_CONTEXT_ATTACH_BYTES = 2 * 1024 * 1024

function attachmentBytes(items: ContextAttachment[]): number {
  return items.reduce((n, a) => n + new TextEncoder().encode(a.content).length, 0)
}

// ── Start form ────────────────────────────────────────────────────────────────

export function QaLoopStartForm({ onStarted, onError, onClose }: StartFormProps) {
  const [unit, setUnit]               = useState<AutomationUnitChoice | null>(null)
  const [units, setUnits]             = useState<AutomationUnitChoice[]>([])
  const [unitsLoading, setUnitsLoading] = useState(false)
  const [fmodule, setFmodule]         = useState<FrontendModule | null>(null)
  const [fmodules, setFmodules]       = useState<FrontendModule[]>([])
  const [fmLoading, setFmLoading]     = useState(false)
  const [useRoutePicker, setUseRoutePicker] = useState(false)
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
  const [contextAttachments, setContextAttachments] = useState<ContextAttachment[]>([])
  const [attachError, setAttachError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const folderInputRef = useRef<HTMLInputElement>(null)
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

  async function loadAutomationUnits() {
    if (units.length > 0) return
    setUnitsLoading(true)
    try { setUnits(await listAutomationUnits()) } catch { /* ignore */ }
    finally { setUnitsLoading(false) }
  }
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
  function applyUnit(u: AutomationUnitChoice | null) {
    setUnit(u)
    if (!u) return
    setSourceDirs(u.sourceDirs.join('\n'))
    setFmodule(null)
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

  async function readMdFiles(files: FileList | File[]): Promise<ContextAttachment[]> {
    const mdFiles = Array.from(files).filter(f =>
      f.name.toLowerCase().endsWith('.md') || f.type === 'text/markdown')
    const out: ContextAttachment[] = []
    for (const f of mdFiles) {
      const content = await f.text()
      const relativePath = (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name
      out.push({ relativePath, content })
    }
    return out
  }

  function mergeAttachments(existing: ContextAttachment[], incoming: ContextAttachment[]): ContextAttachment[] {
    const byPath = new Map(existing.map(a => [a.relativePath, a]))
    for (const a of incoming) byPath.set(a.relativePath, a)
    return Array.from(byPath.values())
  }

  async function handleAttachFiles(e: React.ChangeEvent<HTMLInputElement>) {
    setAttachError(null)
    const files = e.target.files
    if (!files?.length) return
    try {
      const incoming = await readMdFiles(files)
      const merged = mergeAttachments(contextAttachments, incoming)
      if (merged.length > MAX_CONTEXT_ATTACHMENTS) {
        setAttachError(`Maximum ${MAX_CONTEXT_ATTACHMENTS} markdown files.`)
        return
      }
      if (attachmentBytes(merged) > MAX_CONTEXT_ATTACH_BYTES) {
        setAttachError('Total attachment size exceeds 2MB.')
        return
      }
      setContextAttachments(merged)
    } catch {
      setAttachError('Failed to read one or more files.')
    } finally {
      e.target.value = ''
    }
  }

  function removeAttachment(path: string) {
    setAttachError(null)
    setContextAttachments(prev => prev.filter(a => a.relativePath !== path))
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!planId) return
    if (!useRoutePicker && !unit) return
    if (useRoutePicker && !fmodule) return
    if (contextAttachments.length > MAX_CONTEXT_ATTACHMENTS) {
      setAttachError(`Maximum ${MAX_CONTEXT_ATTACHMENTS} markdown files.`)
      return
    }
    if (attachmentBytes(contextAttachments) > MAX_CONTEXT_ATTACH_BYTES) {
      setAttachError('Total attachment size exceeds 2MB.')
      return
    }
    setLoading(true)
    try {
      const dirs = sourceDirs.split(/[\n;,]/).map(dir => dir.trim()).filter(Boolean)
      const req: ScanStartRequest = useRoutePicker && fmodule
        ? {
            module: fmodule.moduleName,
            frontendModule: fmodule.routePath,
            frontendPath: fmodule.componentPath,
            repository: repo || undefined,
            branch: branch || undefined,
            testPlanId: planId,
            testSuiteId: suiteId ?? undefined,
            workItemId: story?.id ?? undefined,
            autoHandoff: false,
            additionalContext: additionalContext.trim() || undefined,
            contextAttachments: contextAttachments.length > 0 ? contextAttachments : undefined,
            sourceDirs: dirs,
            hasUiPhase: hasUiPhase || undefined,
          }
        : {
            module: unit!.slug,
            frontendModule: unit!.routePath,
            frontendPath: unit!.frontendPath,
            repository: repo || undefined,
            branch: branch || undefined,
            testPlanId: planId,
            testSuiteId: suiteId ?? undefined,
            workItemId: story?.id ?? undefined,
            autoHandoff: false,
            additionalContext: additionalContext.trim() || undefined,
            contextAttachments: contextAttachments.length > 0 ? contextAttachments : undefined,
            sourceDirs: dirs.length > 0 ? dirs : [...unit!.sourceDirs],
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
        <FormControlLabel
          control={<Switch checked={useRoutePicker} onChange={e => {
            setUseRoutePicker(e.target.checked)
            if (!e.target.checked) setFmodule(null)
            else setUnit(null)
          }} size="small" />}
          label={<Typography variant="body2">Use legacy side-menu route picker</Typography>}
        />
        {!useRoutePicker ? (
          <Autocomplete
            options={units} value={unit} loading={unitsLoading}
            getOptionLabel={u => u.label}
            groupBy={u => u.parentShell ? `Nested under ${u.parentShell}` : 'Top-level modules'}
            isOptionEqualToValue={(a, b) => a.slug === b.slug}
            onOpen={loadAutomationUnits}
            onChange={(_, v) => applyUnit(v)}
            renderInput={p => <TextField {...p} required label="Automation unit" size="small"
              helperText={unitsLoading ? 'Loading units…'
                : unit
                  ? `${unit.slug} · ${unit.routePath}`
                  : 'Includes nested units (e.g. CPQ+ under Deals). Data Review / Feedback deferred.'} />}
          />
        ) : (
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
        )}
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
          renderInput={p => <TextField {...p} label="Test Plan" size="small" required
            error={!planId}
            helperText={!planId ? 'Required — complete ADO Test Plan for Plan Intake' : undefined} />}
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
        <Box>
          <Typography variant="body2" sx={{ mb: 0.5 }}>Context markdown (optional)</Typography>
          <Stack direction="row" spacing={1} sx={{ mb: 1 }}>
            <Button size="small" variant="outlined" onClick={() => fileInputRef.current?.click()}>
              Attach .md files
            </Button>
            <Button size="small" variant="outlined" onClick={() => folderInputRef.current?.click()}>
              Attach folder
            </Button>
            <Typography variant="caption" color="text.secondary" sx={{ alignSelf: 'center' }}>
              {contextAttachments.length} file(s) · max 50 / 2MB total
            </Typography>
          </Stack>
          <input ref={fileInputRef} type="file" accept=".md,text/markdown" multiple hidden
            onChange={handleAttachFiles} />
          <input ref={folderInputRef} type="file" accept=".md,text/markdown" multiple hidden
            {...({ webkitdirectory: '', directory: '' } as React.InputHTMLAttributes<HTMLInputElement>)}
            onChange={handleAttachFiles} />
          {attachError && <Alert severity="error" sx={{ mb: 1 }}>{attachError}</Alert>}
          {contextAttachments.length > 0 && (
            <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
              {contextAttachments.map(a => (
                <Chip key={a.relativePath} label={a.relativePath} size="small"
                  onDelete={() => removeAttachment(a.relativePath)} />
              ))}
            </Stack>
          )}
        </Box>
        <FormControlLabel
          control={<Switch checked={hasUiPhase} onChange={e => setHasUiPhase(e.target.checked)} size="small" />}
          label={<Typography variant="body2">Include UI phase (Playwright)</Typography>}
        />
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          <Button onClick={onClose} disabled={loading}>Cancel</Button>
          <Button type="submit" variant="contained"
            disabled={loading || !planId || (useRoutePicker ? !fmodule : !unit)}
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
function PushPanel({ session, refetch, lane = 'api' }: { session: ApiSessionDetail; refetch: () => void; lane?: 'api' | 'ui' }) {
  const wiUrl = useAdoWorkItemUrl()
  const [pushing, setPushing]       = useState(false)
  const [error, setError]           = useState('')
  const [plans, setPlans]           = useState<TestPlan[]>([])
  const [suites, setSuites]         = useState<TestSuite[]>([])
  const [localPlanId, setLocalPlanId]   = useState<number | null>(null)
  const [localSuiteId, setLocalSuiteId] = useState<number | null>(null)
  const [savingPlan, setSavingPlan] = useState(false)
  const [planError, setPlanError]   = useState('')
  const [gitStatus, setGitStatus]   = useState<AutomationGitStatus | null>(null)
  const [syncingGit, setSyncingGit] = useState(false)
  const [syncMsg, setSyncMsg]       = useState('')

  const [pushTab, setPushTab] = useState(0)

  const drafts   = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const isApi = (d: ApiTestDraftDto) => !d.draftKind || d.draftKind === 'api_case'
  const isUi = (d: ApiTestDraftDto) => d.draftKind === 'ui_case' || d.draftKind === 'ui_script'
  const laneDrafts = drafts.filter(d => lane === 'ui' ? isUi(d) : isApi(d))
  const pushed   = laneDrafts.filter(d => d.status === 'Pushed').length
  const approved = laneDrafts.filter(d => d.status === 'Approved').length
  const failedPush = laneDrafts.filter(d => d.status === 'Approved' && d.errorMessage).length
  const canPush  = Boolean(session.testPlanId && session.testSuiteId)
  const sessionFullyDone = session.status === 'Done'
  const apiLaneDone = session.status === 'ApiDone' || session.status === 'Done'
  const uiLaneDone = session.uiStatus === 'Done' || (sessionFullyDone && lane === 'ui')
  const laneComplete = lane === 'ui' ? uiLaneDone && approved === 0 : (apiLaneDone && approved === 0 && pushed > 0)
  const isPushing = session.status === 'Pushing' || session.uiStatus === 'Pushing'
  const pushedDrafts = laneDrafts.filter(d => d.status === 'Pushed' && d.adoTestCaseId)
  const PUSH_LEVELS = lane === 'ui' ? ['Ui'] as const : (['Contract', 'Endpoint', 'Workflow'] as const)
  const byPushLevel = (l: string) => pushedDrafts.filter(d => d.level.toLowerCase() === l.toLowerCase())

  async function refreshGitStatus() {
    try { setGitStatus(await getAutomationGitStatus()) }
    catch { /* ignore — endpoint optional when backend old */ }
  }

  useEffect(() => { void refreshGitStatus() }, [session.status, session.uiStatus, pushed])

  async function doSyncFromBase() {
    setSyncingGit(true); setSyncMsg('')
    try {
      const result = await syncAutomationGitFromBase()
      setGitStatus(result.status)
      setSyncMsg(result.message)
    } catch (err: unknown) {
      const data = (err as { response?: { data?: AutomationGitSyncResult } })?.response?.data
      if (data?.status) setGitStatus(data.status)
      setSyncMsg(data?.message ?? axiosMessage(err))
    } finally { setSyncingGit(false) }
  }

  async function doPush() {
    setPushing(true); setError('')
    try {
      await pushSession(session.id, lane)
      refetch()
      await refreshGitStatus()
    }
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

  const dirtyCount = gitStatus?.changedUnderAutomation?.length ?? 0
  const aheadCount = gitStatus?.commitsAheadOfBase?.length ?? 0
  const baseLabel = gitStatus?.baseBranch ?? 'qa'
  const needsPrReminder = Boolean(gitStatus?.prDraftReminder)
  const canShowPushButton = canPush && approved > 0 && !laneComplete && !(lane === 'ui' && session.status !== 'ApiDone' && session.status !== 'Done')

  return (
    <Stack spacing={2}>
      {error && <Alert severity="error">{error}</Alert>}
      <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
        Push ({lane === 'ui' ? 'UI' : 'API'})
      </Typography>
      <Typography variant="body2">
        {laneComplete
          ? `${pushed} ${lane.toUpperCase()} draft${pushed !== 1 ? 's' : ''} pushed to ADO.`
          : `${approved} approved ${lane.toUpperCase()} draft${approved !== 1 ? 's' : ''} ready to push.`
            + (failedPush > 0 ? ` (${failedPush} previous attempt failed — retry remaining.)` : '')}
      </Typography>

      {gitStatus?.productMode && (
        <Stack spacing={1.5}>
          <Alert
            severity={dirtyCount > 0 || aheadCount > 0 ? 'warning' : 'info'}
            action={
              <Stack direction="row" spacing={0.5}>
                <Button color="inherit" size="small" disabled={syncingGit}
                  onClick={() => void doSyncFromBase()}
                  startIcon={syncingGit ? <CircularProgress size={12} color="inherit" /> : undefined}>
                  {syncingGit ? 'Syncing…' : `Sync from ${baseLabel}`}
                </Button>
                <Button color="inherit" size="small" onClick={() => void refreshGitStatus()}>
                  Refresh
                </Button>
              </Stack>
            }
          >
            <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
              Lane 1 — Repo (automation/)
              {gitStatus.currentBranch ? ` · ${gitStatus.currentBranch}` : ''}
            </Typography>
            <Typography variant="body2" sx={{ mb: 1 }}>
              {gitStatus.hint}
            </Typography>
            {dirtyCount > 0 && (
              <Box component="ul" sx={{ m: 0, pl: 2, maxHeight: 140, overflow: 'auto' }}>
                {gitStatus.changedUnderAutomation.slice(0, 20).map(p => (
                  <li key={p}><Typography variant="caption" component="span">{p}</Typography></li>
                ))}
                {dirtyCount > 20 && (
                  <li><Typography variant="caption">…and {dirtyCount - 20} more</Typography></li>
                )}
              </Box>
            )}
            {aheadCount > 0 && (
              <Box sx={{ mt: dirtyCount > 0 ? 1 : 0 }}>
                <Typography variant="caption" sx={{ fontWeight: 600, display: 'block' }}>
                  Commits ahead of origin/{baseLabel} (automation/):
                </Typography>
                <Box component="ul" sx={{ m: 0, pl: 2, maxHeight: 100, overflow: 'auto' }}>
                  {gitStatus.commitsAheadOfBase!.slice(0, 10).map(c => (
                    <li key={c}><Typography variant="caption" component="span">{c}</Typography></li>
                  ))}
                </Box>
              </Box>
            )}
            {dirtyCount === 0 && aheadCount === 0 && (
              <Typography variant="caption" color="text.secondary">
                Working tree clean under automation/ — nothing waiting for a PR.
              </Typography>
            )}
            {gitStatus.automationPipelineUrl && (
              <Box sx={{ mt: 1 }}>
                <Button
                  size="small"
                  component="a"
                  href={gitStatus.automationPipelineUrl}
                  target="_blank"
                  rel="noreferrer"
                  startIcon={<OpenInNewIcon fontSize="small" />}
                >
                  Open automation pipeline
                </Button>
              </Box>
            )}
          </Alert>

          {needsPrReminder && (
            <Alert severity="warning">
              <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
                Draft reminder — go create a PR
              </Typography>
              <Typography variant="body2">
                {gitStatus.prDraftReminder}
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                ADO_Tester will not open the PR. Commit/push your branch, then open
                {' '}<strong>{gitStatus.currentBranch ?? 'this branch'} → {baseLabel}</strong>
                {' '}for someone else to approve.
              </Typography>
            </Alert>
          )}

          {syncMsg && (
            <Alert severity="info" onClose={() => setSyncMsg('')}>{syncMsg}</Alert>
          )}
        </Stack>
      )}

      <Typography variant="subtitle2">Lane 2 — Azure DevOps</Typography>
      {canPush ? (
        <Stack direction="row" spacing={1}>
          <Chip size="small" label={`Plan #${session.testPlanId}`} />
          <Chip size="small" label={`Suite #${session.testSuiteId}`} />
          {session.workItemId && <Chip size="small" label={`Story #${session.workItemId}`} />}
        </Stack>
      ) : !laneComplete && (
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
        {laneComplete && approved === 0
          ? <Typography variant="body2" color="success.main">Pushed ✓</Typography>
          : <Button variant="contained" disabled={pushing || isPushing || !canShowPushButton} onClick={doPush}
              startIcon={(pushing || isPushing) ? <CircularProgress size={16} /> : <PushPinIcon />}>
              {isPushing ? 'Pushing…' : `Push ${lane.toUpperCase()}${approved > 0 ? ` (${approved})` : ''}`}
            </Button>}
        {!['Done', 'Failed', 'Cancelled'].includes(session.status) && (
          <Button color="warning" size="small" startIcon={<StopIcon />} onClick={doCancel}>Cancel</Button>
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

function ExplainFailureCard({
  sessionId, draft, refetch, onError,
}: {
  sessionId: number
  draft: ApiTestDraftDto
  refetch: () => void
  onError: (e: unknown, msg: string) => void
}) {
  const [explanation, setExplanation] = useState<string | null>(null)
  const [explaining, setExplaining] = useState(false)
  const [healing, setHealing] = useState(false)
  const [healCtx, setHealCtx] = useState('')

  async function doExplain() {
    setExplaining(true)
    try {
      const res = await explainFailure(sessionId, draft.id)
      setExplanation(res.explanation)
    } catch (e) { onError(e, 'Failed to explain failure') }
    finally { setExplaining(false) }
  }

  async function doHeal() {
    setHealing(true)
    try {
      await healSpecs(sessionId, [draft.id], healCtx.trim() || undefined)
      refetch()
    } catch (e) { onError(e, 'Heal failed') }
    finally { setHealing(false) }
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
      <TextField
        label="Heal context (optional)" size="small" multiline minRows={2} fullWidth
        value={healCtx} onChange={e => setHealCtx(e.target.value)}
        helperText="Hints for the healer after you review the explanation"
      />
      <Stack direction="row" spacing={1}>
        <Button size="small" variant="contained" onClick={doHeal} disabled={healing}
          startIcon={healing ? <CircularProgress size={12} /> : undefined}>
          {healing ? 'Healing…' : 'Heal'}
        </Button>
        <Button
          size="small" variant="outlined" color="error"
          component="a"
          href={`/find-bugs?from=qa-loop&draftId=${draft.id}${draft.adoTestCaseId ? `&tcId=${draft.adoTestCaseId}` : ''}`}
        >
          Draft Bug
        </Button>
      </Stack>
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

function UiPhasePanel({ session, refetch, onError }: { session: ApiSessionDetail; refetch: () => void; onError: (e: unknown, msg: string) => void }) {
  const drafts = (session.drafts ?? []) as unknown as ApiTestDraftDto[]
  const uiCases   = drafts.filter(d => d.draftKind === 'ui_case')
  const uiScripts = drafts.filter(d => d.draftKind === 'ui_script')

  const [authoring, setAuthoring]     = useState(false)
  const [generating, setGenerating]   = useState(false)
  const [running, setRunning]         = useState(false)
  const [openSpecId, setOpenSpecId]   = useState<number | null>(null)
  const [explainDraftId, setExplainDraftId] = useState<number | null>(null)

  const uiStatus = session.uiStatus ?? null
  const hasUiAnalysis = !!parseUiAnalysis(session.uiAnalysisJson)
  const pushedUiCount = uiScripts.filter(d => d.status === 'Pushed').length

  async function doAuthorUiCases() {
    setAuthoring(true)
    try { await authorUiCases(session.id); refetch() }
    catch (e) { onError(e, 'Failed to author UI cases') }
    finally { setAuthoring(false) }
  }

  async function doSaveUiAnalysis(analysis: UiAnalystOutput) {
    try {
      await saveUiAnalysis(session.id, analysis as unknown as Record<string, unknown>)
      refetch()
    } catch (e) { onError(e, 'Failed to save UI analysis') }
  }

  async function doRefineUiSection(sectionLabel: string, feedback: string) {
    try {
      // L14: refine cancels in-flight UI script generation (registry Cancel on re-Launch).
      if (uiStatus === 'Drafting') await cancelSession(session.id)
      await rerunUiAnalysis(session.id, `## Feedback for ${sectionLabel}\n${feedback.trim()}`)
      refetch()
    } catch (e) { onError(e, 'Failed to refine UI analysis') }
  }

  async function doGenerateUiScripts() {
    setGenerating(true)
    try {
      await generateUiScripts(session.id) // 202 — background job; Activity rail shows progress
      refetch()
    }
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
          <Box>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              Analyst done — review (and edit) the findings, then author cases
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Open each tab, fix anything off, or send feedback on that tab. Then author cases.
            </Typography>
          </Box>
          {hasUiAnalysis
            ? (
              <UiAnalysisPanel
                uiAnalysisJson={session.uiAnalysisJson}
                onSave={doSaveUiAnalysis}
                onRefineSection={doRefineUiSection}
              />
            )
            : <Alert severity="warning">Analyst findings are missing or invalid.</Alert>}
          <Button variant="contained" size="small" onClick={doAuthorUiCases} disabled={authoring}
            startIcon={authoring ? <CircularProgress size={14} /> : undefined}
            sx={{ alignSelf: 'flex-start' }}>
            {authoring ? 'Authoring…' : 'Author Cases'}
          </Button>
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

      {/* AwaitingReview — Tester-depth UI case review (independent of Author Tests) */}
      {uiStatus === 'AwaitingReview' && (
        <UiCaseReviewPanel
          sessionId={session.id}
          drafts={uiCases}
          onError={onError}
          refetch={refetch}
          onGenerateScripts={doGenerateUiScripts}
          generating={generating}
        />
      )}

      {/* Drafting — cards with Automated / Remaining (never … title list) */}
      {uiStatus === 'Drafting' && (
        <Stack spacing={1.5}>
          {(() => {
            const approved = uiCases.filter(d => d.status === 'Approved')
            const automated = uiScripts.length
            const remaining = Math.max(0, approved.length - automated)
            return (
              <>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <Typography variant="body2" sx={{ fontWeight: 600 }}>Generating Playwright scripts…</Typography>
                  <Chip size="small" color="info" label={`Automated ${automated} / Remaining ${remaining}`} />
                </Stack>
                <LinearProgress />
                {approved.map(d => {
                  const script = uiScripts.find(s =>
                    s.title === d.title
                    || (s.title && d.title && s.title.includes(stripTitleTag(d.title))))
                  const failed = Boolean(d.errorMessage) || Boolean(script?.errorMessage)
                  const statusLabel = failed ? 'Failed' : script ? 'Automated' : 'Pending'
                  return (
                    <Card key={d.id} variant="outlined" sx={{ borderColor: failed ? 'error.main' : script ? 'success.main' : 'divider' }}>
                      <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
                        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                          <Chip
                            size="small"
                            label={statusLabel}
                            color={failed ? 'error' : script ? 'success' : 'default'}
                            variant={script && !failed ? 'filled' : 'outlined'}
                          />
                          {d.bucket && <Chip size="small" label={d.bucket} color={BUCKET_COLORS[d.bucket] ?? 'default'} />}
                          <Typography variant="body2" sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                            {stripTitleTag(d.title)}
                          </Typography>
                        </Stack>
                        {(d.errorMessage || script?.errorMessage) && (
                          <Typography variant="caption" color="error" sx={{ display: 'block', mt: 0.5 }}>
                            {d.errorMessage || script?.errorMessage}
                          </Typography>
                        )}
                      </CardContent>
                    </Card>
                  )
                })}
              </>
            )
          })()}
        </Stack>
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
  const steps = pipelineStepsFor(!!s.hasUiPhase)
  const terminalApi = ['Done', 'Failed', 'Cancelled'].includes(s.status)
  const uiLive = !!s.uiStatus && !['Done', 'Failed', 'Cancelled'].includes(s.uiStatus)
  const canCancel = !terminalApi || uiLive
  const live = canCancel
  const stepIdx = activeStepIndex(s.status, s.currentActivity, s.uiStatus, s.hasUiPhase)
  const [selectedStep, setSelectedStep] = useState(Math.min(stepIdx, steps.length - 1))
  const [generateCtx, setGenerateCtx] = useState(s.generateContext ?? '')
  const [refineAnalysisFeedback, setRefineAnalysisFeedback] = useState(s.lastFeedback ?? '')
  const [refiningAnalysis, setRefiningAnalysis] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [cancelling, setCancelling] = useState(false)

  useEffect(() => {
    setSelectedStep(prev => (prev > stepIdx ? stepIdx : Math.max(prev, Math.min(stepIdx, steps.length - 1))))
  }, [stepIdx, steps.length])

  async function doCancel() {
    setCancelling(true)
    try { await cancelSession(s.id); refetch() }
    catch (err) { onError(err, 'Cancel failed') }
    finally { setCancelling(false) }
  }

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

  const stepKey = steps[selectedStep]?.key ?? 'api-analyst'

  const planMeta = (() => {
    try { return s.planImportMetaJson ? JSON.parse(s.planImportMetaJson) as Record<string, unknown> : null }
    catch { return null }
  })()

  const reviewActions = s.status === 'AwaitingReview' && stepKey === 'api-analyst' && (
    <Stack spacing={2} sx={{ mb: 2 }}>
      <Alert severity="info" action={
        <Button color="inherit" size="small" onClick={doGenerate} disabled={generating}>
          {generating ? 'Generating…' : 'Generate API drafts'}
        </Button>
      }>API analysis ready. Review coverage, then generate Contract / Endpoint / Workflow drafts.</Alert>
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
  )

  const activityRail = (
    <Box
      sx={{
        flex: fullScreen ? '1 1 38%' : '1 1 auto',
        minWidth: fullScreen ? 320 : undefined,
        display: 'flex',
        flexDirection: 'column',
        minHeight: fullScreen ? 'calc(100vh - 220px)' : 280,
        borderLeft: fullScreen ? 1 : 0,
        borderTop: fullScreen ? 0 : 1,
        borderColor: 'divider',
        pl: fullScreen ? 2 : 0,
        pt: fullScreen ? 0 : 2,
        mt: fullScreen ? 0 : 1,
      }}
    >
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Activity</Typography>
      {s.errorMessage && <Alert severity="error" sx={{ mb: 1 }}>{s.errorMessage}</Alert>}
      {(s.activityEnabled || (s.eventLogJson?.length ?? 0) > 2)
        ? <AgentConsole
            streamUrl={live ? sessionStreamUrl(s.id) : null}
            seedEvents={parseEventLog(s.eventLogJson)}
            fillHeight={Boolean(fullScreen)}
            title="Live activity"
          />
        : live
          ? <Box sx={{ py: 1 }}><LinearProgress /></Box>
          : <Typography variant="body2" color="text.secondary">No activity yet.</Typography>}
    </Box>
  )

  let stepBody: ReactNode = null
  if (stepKey === 'retrieve-kb') {
    stepBody = (
      <Stack spacing={1.5}>
        <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Retrieve KB</Typography>
        <Chip size="small" label={s.frsSlug ? `KB: ${s.frsSlug}` : 'KB: —'} color={s.frsSlug ? 'success' : 'default'} />
        {s.additionalContext && (
          <Alert severity="info"><Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{s.additionalContext}</Typography></Alert>
        )}
        {['RetrievingKb'].includes(s.status) && <LinearProgress />}
        <Typography variant="body2" color="text.secondary">
          FRS module brief, atlas, and RULES are loaded for this automation unit. Code scan runs under API Analyst (not a separate step).
        </Typography>
      </Stack>
    )
  } else if (stepKey === 'plan-intake') {
    stepBody = (
      <Stack spacing={1.5}>
        <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Plan Intake</Typography>
        {s.status === 'ImportingPlan' && <LinearProgress />}
        <Typography variant="body2">Test Plan #{s.testPlanId ?? '—'} · Suite #{s.testSuiteId ?? '—'}</Typography>
        {planMeta ? (
          <Alert severity="success">
            Imported thin={(planMeta.ThinImproved ?? planMeta.thinImproved) as number ?? '—'}
            {' · '}rich={(planMeta.RichLinked ?? planMeta.richLinked) as number ?? '—'}
            {' · '}gaps={(planMeta.GapsCreated ?? planMeta.gapsCreated) as number ?? '—'}
            {' · '}unit={(planMeta.SelectedUnit ?? planMeta.selectedUnit) as string ?? s.module}
          </Alert>
        ) : (
          <Typography variant="body2" color="text.secondary">
            Suite→unit map and thin/rich/gap import run after KB. Ambiguous suites are skipped until confirmed (see Activity).
          </Typography>
        )}
      </Stack>
    )
  } else if (stepKey === 'api-analyst') {
    stepBody = (
      <Stack spacing={1}>
        {reviewActions}
        <CoverageMatrix session={s} onSaved={() => { refetch(); setSelectedStep(3) }}
          readOnly={!['Scanning', 'Analyzing', 'AwaitingReview'].includes(s.status)} />
      </Stack>
    )
  } else if (stepKey === 'api-tests') {
    stepBody = (
      <DraftsByLevel session={s} refetch={refetch} onGenerate={doGenerate}
        readOnly={s.status === 'Done' || s.status === 'ApiDone' || s.status === 'Pushing'} />
    )
  } else if (stepKey === 'push-api') {
    stepBody = <PushPanel session={s} refetch={refetch} lane="api" />
  } else if (stepKey === 'ui-analyst' || stepKey === 'ui-tester') {
    stepBody = <UiPhasePanel session={s} refetch={refetch} onError={onError} />
  } else if (stepKey === 'push-ui') {
    stepBody = <PushPanel session={s} refetch={refetch} lane="ui" />
  }

  return (
    <Box sx={{
      width: '100%',
      maxWidth: fullScreen ? '100%' : 1100,
      height: fullScreen ? '100%' : undefined,
      display: 'flex',
      flexDirection: 'column',
    }}>
      <AgentPipeline
        steps={steps}
        activeIndex={stepIdx}
        inFlight={live}
        failed={s.status === 'Failed' || s.status === 'Cancelled'}
        currentActivity={s.currentActivity ?? null}
        selectedIndex={selectedStep}
        onSelect={(i) => {
          if (i > stepIdx) return
          setSelectedStep(i)
        }}
      />

      {canCancel && (
        <Stack direction="row" sx={{ mb: 1, justifyContent: 'flex-end' }}>
          <Button color="warning" size="small" startIcon={<StopIcon />}
            onClick={doCancel} disabled={cancelling}>
            {cancelling ? 'Cancelling…' : 'Cancel'}
          </Button>
        </Stack>
      )}

      {s.status === 'Cancelled' && (
        <Alert severity="warning" sx={{ mb: 2 }}>Session cancelled by user.</Alert>
      )}

      <Box
        sx={{
          display: 'flex',
          flexDirection: fullScreen ? 'row' : 'column',
          alignItems: 'stretch',
          gap: fullScreen ? 0 : 0,
          flex: 1,
          minHeight: 0,
          mt: 1,
          width: '100%',
        }}
      >
        <Box sx={{ flex: fullScreen ? '1 1 62%' : 1, minWidth: 0, pr: fullScreen ? 2 : 0 }}>
          {stepBody}
        </Box>

        {activityRail}
      </Box>
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
  inFlightStatuses: ['RetrievingKb', 'ImportingPlan', 'Scanning', 'Analyzing', 'AwaitingReview', 'Drafting', 'Pushing', 'ApiDone'],
  getUiStatus: (d) => (d as ApiSessionDetail).uiStatus ?? undefined,
  uiInFlightStatuses: ['AnalyzingUi', 'RunningUiScripts'],
  pipelineSteps: PIPELINE_STEPS_API,
  activeStepIndex: activeStepIndexCompat,
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
