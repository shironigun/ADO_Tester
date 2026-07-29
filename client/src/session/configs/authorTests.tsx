import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Dialog, DialogActions, DialogContent, DialogTitle, Divider, FormControl,
  FormControlLabel, IconButton, InputLabel, Link, MenuItem, Select, Stack, Switch,
  Table, TableBody, TableCell, TableHead, TableRow, TextField, ToggleButton,
  ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import EditIcon from '@mui/icons-material/Edit'
import SaveIcon from '@mui/icons-material/Save'
import DeleteIcon from '@mui/icons-material/Delete'
import AddIcon from '@mui/icons-material/Add'
import DownloadIcon from '@mui/icons-material/Download'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import VisibilityIcon from '@mui/icons-material/Visibility'
import PushPinIcon from '@mui/icons-material/PushPin'
import StopIcon from '@mui/icons-material/Stop'
import RuleIcon from '@mui/icons-material/Rule'
import SearchIcon from '@mui/icons-material/Search'
import MenuBookIcon from '@mui/icons-material/MenuBook'
import PsychologyIcon from '@mui/icons-material/Psychology'
import TravelExploreIcon from '@mui/icons-material/TravelExplore'
import AgentPipeline, { type PipelineStep } from '../../components/AgentPipeline'
import AgentConsole from '../../components/AgentConsole'
import AnalysisPanel from '../../components/AnalysisPanel'
import AnalystProgress from '../../components/AnalystProgress'
import StatTile from '../../components/StatTile'
import StatusChip from '../../components/StatusChip'
import TesterProgress from '../../components/TesterProgress'
import type {
  AnalyticsColumn, DetailBodyProps, SessionDetail, SessionModuleConfig,
  StartFormProps, WorkItemOption,
} from '../types'
import { parseEventLog } from '../shared'
import { useEventStream } from '../../hooks/useEventStream'
import { axiosMessage } from '../../common/errors'
import { useAdoWorkItemUrl } from '../../common/adoLinks'
import { PROJECT_ID } from '../../config/env'
import {
  getPlans, getSuites, searchWorkItems, startSession, listSessionsPage, getSessionDetail,
  patchDraft, approveAllDrafts, runTester as apiRunTester, cancelSession, pushSession, pushDraft, saveAnalysis,
  rerunAnalyst as apiRerunAnalyst, refineTester as apiRefineTester,
  rescanPlan, getScanStatus, saveCodeBehavior, type CodeBehaviorItem,
  sessionStreamUrl, type TestPlan, type TestSuite, type TestFeedbackItem,
} from '../../api/authorTests'
import { getConfig } from '../../api/settings'

// ADO work-item deep-links are built from the configured org/project via useAdoWorkItemUrl().

interface Step { action: string; expected: string }
interface Draft {
  id: number; title: string; preconditions: string; stepsJson: string
  status: 'Draft' | 'Approved' | 'Pushed' | 'Rejected'; sortOrder: number
  adoTestCaseId: number | null
}
interface DraftPatch { title?: string; preconditions?: string; steps?: Step[] }

const PIPELINE_STEPS: PipelineStep[] = [
  { key: 'fetch',    label: 'Fetch ticket',  icon: <SearchIcon /> },
  { key: 'retrieve', label: 'Retrieve KB',   icon: <MenuBookIcon /> },
  { key: 'discover', label: 'Code behavior', icon: <TravelExploreIcon /> },
  { key: 'analyst',  label: 'Analyst',       icon: <PsychologyIcon /> },
  { key: 'tester',   label: 'Tester',        icon: <RuleIcon /> },
]

function activeStepIndex(status: string, activity: string | null): number {
  const a = (activity ?? '').toLowerCase()
  if (status === 'Done' || status === 'Drafted') return PIPELINE_STEPS.length
  if (status === 'Pushing') return 4 // push fires from the Tester phase — keep that circle lit
  if (status === 'AwaitingTester') return 4
  if (status === 'Failed' || status === 'Cancelled') {
    // currentActivity is overwritten with a generic terminal string on failure, so these substring
    // checks rarely match — reachedIdx derived from durable session data is the authoritative fallback.
    if (a.includes('tester'))   return 4
    if (a.includes('analyst'))  return 3
    if (a.includes('discover') || a.includes('triage')) return 2
    if (a.includes('retriev'))  return 1
    if (a.includes('fetch'))    return 0
    return 0
  }
  if (status === 'Drafting' || a.includes('tester')) return 4
  if (a.includes('analyst'))   return 3
  if (a.includes('discover') || a.includes('triage')) return 2
  if (a.includes('retriev'))   return 1
  if (a.includes('fetch') || a.includes('work item')) return 0
  return 0
}

// ── Draft card: compact row + detail/edit dialog ─────────────────────────────
function TestDraftCard({ draft, onApprove, onReject, onPush, onSave, canPush,
  feedback, onFeedbackChange, markedRemove, onToggleRemove }: {
  draft: Draft; onApprove: () => void; onReject: () => void; onPush: () => Promise<void>; canPush: boolean
  onSave: (patch: DraftPatch) => Promise<void> | void
  feedback?: string; onFeedbackChange?: (v: string) => void
  markedRemove?: boolean; onToggleRemove?: (v: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  const [saving, setSaving] = useState(false)
  const [pushing, setPushing] = useState(false)
  const doPush = async () => { setPushing(true); try { await onPush() } finally { setPushing(false) } }
  const initialSteps = (): Step[] => { try { return JSON.parse(draft.stepsJson) } catch { return [] } }
  const [title, setTitle] = useState(draft.title)
  const [steps, setSteps] = useState<Step[]>(initialSteps())

  useEffect(() => {
    if (!editing) { setTitle(draft.title); setSteps(initialSteps()) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft.id, draft.title, draft.stepsJson])

  const isPushed = draft.status === 'Pushed'
  const isEditable = !isPushed && draft.status !== 'Rejected'

  const updateStep = (i: number, f: 'action' | 'expected', v: string) =>
    setSteps(c => c.map((s, idx) => idx === i ? { ...s, [f]: v } : s))
  const addStep = () => setSteps(c => [...c, { action: '', expected: '' }])
  const removeStep = (i: number) => setSteps(c => c.filter((_, idx) => idx !== i))
  const moveStep = (i: number, dir: -1 | 1) => setSteps(c => {
    const j = i + dir; if (j < 0 || j >= c.length) return c
    const n = [...c]; [n[i], n[j]] = [n[j], n[i]]; return n
  })

  async function commitEdits() {
    setSaving(true)
    try { await onSave({ title, steps }); setEditing(false) } finally { setSaving(false) }
  }
  function cancelEdits() { setTitle(draft.title); setSteps(initialSteps()); setEditing(false) }
  const openView = () => { setEditing(false); setOpen(true) }
  const openEdit = () => { setEditing(true); setOpen(true) }
  const closeDialog = () => { if (editing) cancelEdits(); setOpen(false) }

  return (
    <>
      <Card variant="outlined" sx={{ transition: 'border-color .15s, box-shadow .15s', '&:hover': { borderColor: 'primary.main', boxShadow: 1 } }}>
        <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <StatusChip value={draft.status} kind="draft" />
            <Typography variant="body2" onClick={openView}
              sx={{ flex: 1, fontWeight: 500, cursor: 'pointer', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              title={draft.title}>{draft.title}</Typography>
            <Chip size="small" variant="outlined" label={`${steps.length} step${steps.length === 1 ? '' : 's'}`} />
            {draft.adoTestCaseId && <Chip size="small" color="success" variant="outlined" label={`TC #${draft.adoTestCaseId}`} />}
            {isEditable && (
              <>
                {draft.status === 'Approved' ? (
                  <Tooltip title={canPush ? 'Push this test case to ADO' : 'Choose a Test Plan + Suite below first'}>
                    <span>
                      <Button size="small" variant="outlined" color="success" disabled={!canPush || pushing}
                        startIcon={pushing ? <CircularProgress size={14} /> : <PushPinIcon />} onClick={doPush}>
                        Push
                      </Button>
                    </span>
                  </Tooltip>
                ) : (
                  <Tooltip title="Approve"><IconButton size="small" color="success" onClick={onApprove}><CheckIcon fontSize="small" /></IconButton></Tooltip>
                )}
                <Tooltip title="Remove"><IconButton size="small" color="error" onClick={onReject}><CloseIcon fontSize="small" /></IconButton></Tooltip>
              </>
            )}
            <Button size="small" variant="outlined" startIcon={<VisibilityIcon />} onClick={openView}>View</Button>
          </Stack>
          {isEditable && onFeedbackChange && (
            <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'flex-start' }}>
              <TextField size="small" fullWidth multiline placeholder="Feedback for this test (used on Regenerate)"
                value={feedback ?? ''} onChange={e => onFeedbackChange(e.target.value)}
                disabled={markedRemove} />
              <Tooltip title="Mark this test for removal on Regenerate">
                <FormControlLabel sx={{ m: 0, whiteSpace: 'nowrap' }}
                  control={<Checkbox size="small" checked={!!markedRemove} onChange={(_, v) => onToggleRemove?.(v)} />}
                  label={<Typography variant="caption">Remove</Typography>} />
              </Tooltip>
            </Stack>
          )}
        </CardContent>
      </Card>

      <Dialog open={open} onClose={closeDialog} maxWidth="md" fullWidth scroll="paper">
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
          <StatusChip value={draft.status} kind="draft" />
          <Box sx={{ flex: 1, minWidth: 0 }}>
            {editing
              ? <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)} placeholder="[Module] - [Feature] - Verify that ..." />
              : <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>{draft.title}</Typography>}
          </Box>
          <IconButton size="small" onClick={closeDialog} aria-label="close"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead><TableRow>
                <TableCell sx={{ width: 32 }}>#</TableCell><TableCell>Action</TableCell>
                <TableCell>Expected Result</TableCell>{editing && <TableCell sx={{ width: 96 }} />}
              </TableRow></TableHead>
              <TableBody>
                {steps.map((s, i) => (
                  <TableRow key={i}>
                    <TableCell sx={{ verticalAlign: 'top' }}>{i + 1}</TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top', whiteSpace: 'pre-wrap' }}>
                      {editing ? <TextField fullWidth size="small" multiline value={s.action} onChange={e => updateStep(i, 'action', e.target.value)} /> : s.action}
                    </TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top', whiteSpace: 'pre-wrap' }}>
                      {editing ? <TextField fullWidth size="small" multiline value={s.expected} onChange={e => updateStep(i, 'expected', e.target.value)} /> : s.expected}
                    </TableCell>
                    {editing && (
                      <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>
                        <Tooltip title="Move up"><span><IconButton size="small" disabled={i === 0} onClick={() => moveStep(i, -1)}><ExpandLessIcon fontSize="small" /></IconButton></span></Tooltip>
                        <Tooltip title="Move down"><span><IconButton size="small" disabled={i === steps.length - 1} onClick={() => moveStep(i, 1)}><ExpandMoreIcon fontSize="small" /></IconButton></span></Tooltip>
                        <Tooltip title="Delete step"><IconButton size="small" color="error" onClick={() => removeStep(i)}><DeleteIcon fontSize="small" /></IconButton></Tooltip>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {editing && <Button size="small" startIcon={<AddIcon />} onClick={addStep} sx={{ mt: 1 }}>Add step</Button>}
          </Box>
          {draft.adoTestCaseId && (
            <Typography variant="caption" color="success.main" sx={{ mt: 1.5, display: 'block' }}>ADO Test Case #{draft.adoTestCaseId}</Typography>
          )}
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 1.5 }}>
          {editing ? (
            <>
              <Button onClick={cancelEdits} disabled={saving}>Cancel</Button>
              <Button variant="contained" disabled={saving} startIcon={saving ? <CircularProgress size={16} /> : <SaveIcon />} onClick={commitEdits}>Save</Button>
            </>
          ) : (
            <>
              {isEditable && <Button startIcon={<EditIcon />} onClick={openEdit}>Edit</Button>}
              {isEditable && <Button color="error" startIcon={<CloseIcon />} onClick={() => { onReject(); setOpen(false) }}>Remove</Button>}
              {isEditable && (draft.status === 'Approved' ? (
                <Tooltip title={canPush ? '' : 'Choose a Test Plan + Suite first'}>
                  <span>
                    <Button color="success" variant="contained" disabled={!canPush || pushing}
                      startIcon={pushing ? <CircularProgress size={16} /> : <PushPinIcon />}
                      onClick={async () => { await doPush(); setOpen(false) }}>Push to ADO</Button>
                  </span>
                </Tooltip>
              ) : (
                <Button color="success" variant="contained" startIcon={<CheckIcon />}
                  onClick={() => { onApprove(); setOpen(false) }}>Approve</Button>
              ))}
              <Button onClick={() => setOpen(false)}>Close</Button>
            </>
          )}
        </DialogActions>
      </Dialog>
    </>
  )
}

// ── Post-push results card ───────────────────────────────────────────────────
function TestPushResults({ session, context }: {
  session: SessionDetail; context: { plan: TestPlan; suite: TestSuite } | undefined
}) {
  const wiUrl = useAdoWorkItemUrl()
  const drafts = session.drafts as unknown as Draft[]
  const pushed = drafts.filter(d => d.status === 'Pushed' && d.adoTestCaseId)
  if (pushed.length === 0) return null
  if (!['Done', 'Cancelled', 'Failed'].includes(session.status)) return null

  function downloadCsv() {
    const header = ['SessionId','StoryId','StoryTitle','TestCaseId','TestCaseTitle','PlanId','PlanName','SuiteId','SuiteName','AdoUrl']
    const esc = (v: unknown) => { const s = String(v ?? ''); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s }
    const rows = pushed.map(d => ([
      session.id, session.workItemId, session.workItemTitle, d.adoTestCaseId, d.title,
      context?.plan.id ?? '', context?.plan.name ?? '', context?.suite.id ?? '', context?.suite.name ?? '', wiUrl(d.adoTestCaseId!),
    ] as unknown[]).map(esc).join(','))
    const csv = [header.join(','), ...rows].join('\n')
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
    const a = document.createElement('a'); a.href = url; a.download = `push-results-session-${session.id}.csv`; a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Card sx={{ mt: 3, borderColor: 'success.main' }}>
      <CardContent>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
          <CheckIcon color="success" />
          <Typography variant="h6">Push results — {pushed.length} test case(s) pushed</Typography>
          <Box sx={{ flex: 1 }} />
          <Button size="small" variant="outlined" startIcon={<DownloadIcon />} onClick={downloadCsv}>Download CSV</Button>
        </Stack>
        <Table size="small">
          <TableHead><TableRow><TableCell sx={{ width: 96 }}>TC ID</TableCell><TableCell>Title</TableCell><TableCell sx={{ width: 64 }} /></TableRow></TableHead>
          <TableBody>
            {pushed.map(d => (
              <TableRow key={d.id}>
                <TableCell><strong>#{d.adoTestCaseId}</strong></TableCell>
                <TableCell>{d.title}</TableCell>
                <TableCell><Tooltip title="Open in Azure DevOps"><IconButton size="small" component="a" href={wiUrl(d.adoTestCaseId!)} target="_blank" rel="noreferrer"><OpenInNewIcon fontSize="small" /></IconButton></Tooltip></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  )
}

interface CodeBehaviorRow { id: string; behavior: string; comment: string }

// Code-behavior findings panel (pipeline step "Code behavior"). Shows the analyzed "what the code
// does" bullets; at the Analyst→Tester gate (AwaitingTester) each bullet gets a note box the reviewer
// can use to flag suspected misbehavior — the notes are fed to the Tester.
function CodeBehaviorPanel({ session, inFlight, onSaved, onError }: {
  session: SessionDetail; inFlight: boolean
  onSaved: () => void; onError: (e: unknown, fallback: string) => void
}) {
  const rows = useMemo<CodeBehaviorRow[]>(() => {
    try {
      const arr = JSON.parse(session.codeBehaviorJson || '[]')
      return Array.isArray(arr)
        ? arr.map((x, i) => ({
            id: String(x?.id ?? `b${i + 1}`),
            behavior: String(x?.behavior ?? ''),
            comment: String(x?.comment ?? ''),
          })).filter(r => r.behavior)
        : []
    } catch { return [] }
  }, [session.codeBehaviorJson])

  const editable = session.status === 'AwaitingTester'
  const [comments, setComments] = useState<Record<string, string>>({})
  useEffect(() => { setComments(Object.fromEntries(rows.map(r => [r.id, r.comment]))) }, [rows])
  const [saving, setSaving] = useState(false)

  const dirty = rows.some(r => (comments[r.id] ?? '') !== r.comment)
  const save = async () => {
    setSaving(true)
    try {
      const items: CodeBehaviorItem[] = rows.map(r => ({ ...r, comment: (comments[r.id] ?? '').trim() }))
      await saveCodeBehavior(session.id, items)
      onSaved()
    } catch (e) { onError(e, 'Save notes failed') } finally { setSaving(false) }
  }

  return (
    <Card variant="outlined"><CardContent>
      <Typography variant="subtitle2" gutterBottom>Code behavior ({rows.length})</Typography>
      {rows.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          {inFlight ? 'Analyzing code…' : 'No code was discovered or analyzed for this story.'}
        </Typography>
      ) : (
        <>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
            What the current implementation actually does, distilled from the repo.
            {editable ? ' Add a note on any bullet to flag suspected misbehavior — your notes steer the Tester.' : ''}
          </Typography>
          <Stack spacing={1.5}>
            {rows.map((r, i) => (
              <Box key={r.id} sx={{ borderLeft: 2, borderColor: 'divider', pl: 1.5 }}>
                <Typography variant="body2" sx={{ mb: editable || r.comment ? 0.75 : 0 }}>
                  <b>{i + 1}.</b> {r.behavior}
                </Typography>
                {editable ? (
                  <TextField size="small" fullWidth value={comments[r.id] ?? ''}
                    onChange={e => setComments(c => ({ ...c, [r.id]: e.target.value }))}
                    placeholder="Add a note (optional) — e.g. this looks wrong: should reject empty names" />
                ) : r.comment ? (
                  <Alert severity="warning" icon={false} sx={{ py: 0, px: 1 }}>
                    <Typography variant="caption"><b>Note:</b> {r.comment}</Typography>
                  </Alert>
                ) : null}
              </Box>
            ))}
          </Stack>
          {editable && (
            <Box sx={{ textAlign: 'right', mt: 1.5 }}>
              <Button size="small" variant="outlined" startIcon={<SaveIcon />}
                disabled={saving || !dirty} onClick={save}>
                {saving ? 'Saving…' : 'Save notes'}
              </Button>
            </Box>
          )}
        </>
      )}
    </CardContent></Card>
  )
}

// ── Detail body (pipeline + gate + tiles→popup + push) ───────────────────────
function AuthorDetailBody({ session, refetch, onError }: DetailBodyProps) {
  const wiUrl = useAdoWorkItemUrl()
  const drafts = session.drafts as unknown as Draft[]
  const [plans, setPlans] = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])
  const [selectedPlan, setSelectedPlan] = useState<number | ''>('')
  const [selectedSuite, setSelectedSuite] = useState<number | ''>('')
  const [pushing, setPushing] = useState(false)
  const [pushError, setPushError] = useState('')
  const [pushedContext, setPushedContext] = useState<{ plan: TestPlan; suite: TestSuite }>()
  const [tcOpen, setTcOpen] = useState(false)
  const [tcFilter, setTcFilter] = useState<'all' | 'Draft' | 'Approved' | 'Pushed' | 'Rejected'>('all')
  // Free-text context + feedback-driven refine state.
  const [testerCtx, setTesterCtx] = useState(session.testerContext ?? '')
  const [analystFeedback, setAnalystFeedback] = useState('')
  const [analystBusy, setAnalystBusy] = useState(false)
  const [overallFeedback, setOverallFeedback] = useState('')
  const [perTestFb, setPerTestFb] = useState<Record<number, string>>({})
  const [removeSet, setRemoveSet] = useState<Record<number, boolean>>({})
  const [refineBusy, setRefineBusy] = useState(false)

  // Reference plan dedup state (seeded from session).
  const [refPlanId, setRefPlanId] = useState<number | ''>(session.referenceTestPlanId ?? '')
  const [refSuiteId, setRefSuiteId] = useState<number | ''>(session.referenceTestSuiteId ?? '')
  const [refSuites, setRefSuites] = useState<TestSuite[]>([])
  const [navHint, setNavHint] = useState(session.planNavigationHint ?? '')
  const [scanStatus, setScanStatus] = useState<{ caseCount: number; scannedAt: string } | null>(null)
  const [rescanning, setRescanning] = useState(false)

  useEffect(() => { getPlans().then(setPlans).catch(() => {}) }, [])
  useEffect(() => {
    if (!selectedPlan) { setSuites([]); return }
    getSuites(selectedPlan).then(setSuites).catch(() => {})
  }, [selectedPlan])

  // Ref plan suites + scan status.
  useEffect(() => {
    if (!refPlanId) { setRefSuites([]); return }
    getSuites(refPlanId).then(setRefSuites).catch(() => {})
  }, [refPlanId])
  useEffect(() => {
    if (!refPlanId) { setScanStatus(null); return }
    getScanStatus(refPlanId, refSuiteId !== '' ? refSuiteId : undefined)
      .then(s => { if (s.scanId) setScanStatus({ caseCount: s.caseCount!, scannedAt: s.scannedAt! }) })
      .catch(() => {})
  }, [refPlanId, refSuiteId])

  // Seed the push Plan/Suite from the Settings defaults (defaults.test_plan_id / _suite_id) so they're
  // pre-filled here. Fills only empty selectors — a manual choice is never overridden.
  const [pendingDefaultSuite, setPendingDefaultSuite] = useState<number | ''>('')
  useEffect(() => {
    getConfig().then(cfg => {
      const planId = Number(cfg['defaults.test_plan_id']) || ''
      const suiteId = Number(cfg['defaults.test_suite_id']) || ''
      if (planId) setSelectedPlan(prev => (prev === '' ? planId : prev))
      if (suiteId) setPendingDefaultSuite(suiteId)
    }).catch(() => {})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  // Apply the default suite once its plan's suites have loaded (avoids an out-of-range Select value).
  useEffect(() => {
    if (pendingDefaultSuite !== '' && selectedSuite === '' && suites.some(s => s.id === pendingDefaultSuite)) {
      setSelectedSuite(pendingDefaultSuite)
      setPendingDefaultSuite('')
    }
  }, [suites, pendingDefaultSuite, selectedSuite])

  const inFlight = ['Analyzing', 'Drafting', 'Pushing'].includes(session.status)
  const approvedCount = drafts.filter(d => d.status === 'Approved').length
  const canPush = approvedCount > 0 && !!selectedPlan && !!selectedSuite

  const approveDraft = async (id: number) => { try { await patchDraft(id, { status: 'Approved' }); refetch() } catch (e) { onError(e, 'Approve failed') } }
  const rejectDraft = async (id: number) => { try { await patchDraft(id, { status: 'Rejected' }); refetch() } catch (e) { onError(e, 'Reject failed') } }
  const pushDraftRow = async (id: number) => {
    if (!selectedPlan || !selectedSuite) return
    setPushError('')
    try {
      await pushDraft(id, { testPlanId: selectedPlan, testSuiteId: selectedSuite })
      const plan = plans.find(p => p.id === selectedPlan); const suite = suites.find(s => s.id === selectedSuite)
      if (plan && suite) setPushedContext({ plan, suite })
      refetch()
    } catch (e) { setPushError(axiosMessage(e)) }
  }
  const approveAll = async () => { try { await approveAllDrafts(session.id); refetch() } catch (e) { onError(e, 'Approve all failed') } }
  const saveDraft = async (id: number, patch: DraftPatch) => {
    const body: { title?: string; stepsJson?: string } = {}
    if (patch.title !== undefined) body.title = patch.title
    if (patch.steps !== undefined) body.stepsJson = JSON.stringify(patch.steps)
    try { await patchDraft(id, body); refetch() } catch (e) { onError(e, 'Save failed') }
  }
  const rescan = async () => {
    if (!refPlanId) return
    setRescanning(true)
    try {
      const result = await rescanPlan(refPlanId, refSuiteId !== '' ? refSuiteId : undefined)
      setScanStatus({ caseCount: result.caseCount, scannedAt: result.scannedAt })
    } catch (e) { onError(e, 'Rescan failed') } finally { setRescanning(false) }
  }
  const runTester = async () => {
    try {
      await apiRunTester(session.id, {
        additionalContext: testerCtx.trim() || undefined,
        referenceTestPlanId: refPlanId !== '' ? refPlanId : undefined,
        referenceTestSuiteId: refSuiteId !== '' ? refSuiteId : undefined,
        planNavigationHint: navHint.trim() || undefined,
      })
      refetch()
    }
    catch (e) { onError(e, 'Run Tester failed') }
  }
  const refineAnalyst = async () => {
    if (!analystFeedback.trim()) return
    setAnalystBusy(true)
    try { await apiRerunAnalyst(session.id, analystFeedback.trim()); setAnalystFeedback(''); refetch() }
    catch (e) { onError(e, 'Refine analysis failed') } finally { setAnalystBusy(false) }
  }
  const regenerateTester = async () => {
    const perTest: TestFeedbackItem[] = []
    for (const d of drafts) {
      if (d.status === 'Pushed') continue
      const fb = (perTestFb[d.id] ?? '').trim()
      const rm = !!removeSet[d.id]
      if (fb || rm) perTest.push({ draftId: d.id, feedback: fb || undefined, remove: rm })
    }
    if (!overallFeedback.trim() && perTest.length === 0) return
    setRefineBusy(true)
    try {
      await apiRefineTester(session.id, { overallFeedback: overallFeedback.trim() || undefined, perTest })
      setOverallFeedback(''); setPerTestFb({}); setRemoveSet({}); refetch()
    } catch (e) { onError(e, 'Regenerate failed') } finally { setRefineBusy(false) }
  }
  const cancel = async () => { try { await cancelSession(session.id); refetch() } catch (e) { onError(e, 'Cancel failed') } }
  const push = async () => {
    if (!selectedPlan || !selectedSuite) return
    setPushing(true); setPushError('')
    try {
      await pushSession(session.id, { testPlanId: selectedPlan, testSuiteId: selectedSuite })
      const plan = plans.find(p => p.id === selectedPlan); const suite = suites.find(s => s.id === selectedSuite)
      if (plan && suite) setPushedContext({ plan, suite })
      refetch()
    } catch (e) { setPushError(axiosMessage(e)) } finally { setPushing(false) }
  }

  const cnt = (s: Draft['status']) => drafts.filter(d => d.status === s).length
  const tiles = [
    { key: 'all' as const, label: 'Total', value: drafts.length, color: 'primary.main' },
    { key: 'Draft' as const, label: 'Pending', value: cnt('Draft'), color: 'warning.main' },
    { key: 'Approved' as const, label: 'Approved', value: cnt('Approved'), color: 'success.main' },
    { key: 'Pushed' as const, label: 'Pushed', value: cnt('Pushed'), color: 'info.main' },
  ]
  const visible = [...drafts].filter(d => tcFilter === 'all' || d.status === tcFilter).sort((a, b) => a.sortOrder - b.sortOrder)
  const openTile = (k: typeof tcFilter) => { setTcFilter(k); setTcOpen(true) }

  // ── Phase navigation: clicking a pipeline circle selects which phase's findings to show.
  const persisted = useMemo(() => parseEventLog(session.eventLogJson), [session.eventLogJson])
  const streamUrl = sessionStreamUrl(session.id)
  // Merge the durable log with the LIVE SSE stream so findings (KB chunks, fetch, dedup) update in
  // real time. EventLogJson only persists at phase boundaries — the retrieve event isn't saved until
  // after code-discovery — so poll-only findings lag the console by a whole phase.
  const { events, connected } = useEventStream(inFlight ? streamUrl : null, persisted)
  const fetchEv = events.find(e => e.phase === 'fetch')
  const wiType = (fetchEv?.data as Record<string, unknown> | null | undefined)?.type as string | undefined
  const kbEv = events.find(e => e.phase === 'retrieve' && Array.isArray((e.data as Record<string, unknown> | null | undefined)?.files))
  const kbFiles = ((kbEv?.data as Record<string, unknown> | undefined)?.files as string[] | undefined) ?? []
  // On a terminal failure/cancel currentActivity is a generic "Failed: …" string, so infer the
  // furthest phase reached from durable data, landing the failed circle + default panel there.
  const terminalFailed = session.status === 'Failed' || session.status === 'Cancelled'
  const dedupEv = events.find(e => e.phase === 'dedup')
  const skippedDups = ((dedupEv?.data as Record<string, unknown> | undefined)?.skipped as Array<{ title: string; matchedTitle?: string; matchedAdoId?: number }> | undefined) ?? []
  const flaggedDups = ((dedupEv?.data as Record<string, unknown> | undefined)?.flagged as Array<{ title: string; matchedTitle?: string; matchedAdoId?: number }> | undefined) ?? []
  const reachedIdx = drafts.length > 0 ? 4 : session.analysisJson ? 3 : session.codeContext ? 2 : kbFiles.length > 0 ? 1 : 0
  const activeIdx = terminalFailed ? reachedIdx : activeStepIndex(session.status, session.currentActivity)
  const [picked, setPicked] = useState<number | null>(null)
  const sel = Math.min(picked ?? activeIdx, PIPELINE_STEPS.length - 1)
  const [view, setView] = useState<'findings' | 'activity'>('findings')

  const phaseLabel = PIPELINE_STEPS[sel]?.label ?? ''

  return (
    <Box>
      <AgentPipeline steps={PIPELINE_STEPS} activeIndex={activeIdx}
        inFlight={['Analyzing', 'Drafting'].includes(session.status)}
        failed={session.status === 'Failed' || session.status === 'Cancelled'}
        currentActivity={session.currentActivity}
        selectedIndex={sel} onSelect={setPicked} />

      {inFlight && (
        <Stack direction="row" sx={{ mb: 1, justifyContent: 'flex-end' }}>
          <Button color="warning" size="small" startIcon={<StopIcon />} onClick={cancel}>Cancel</Button>
        </Stack>
      )}

      {session.status === 'Failed' && (
        <Alert severity="error" sx={{ mb: 2 }}>{session.errorMessage || session.currentActivity || 'Session failed.'}</Alert>
      )}
      {session.status === 'Cancelled' && <Alert severity="warning" sx={{ mb: 2 }}>Session cancelled by user.</Alert>}

      {session.status === 'AwaitingTester' && (
        <Alert severity="info" icon={false} sx={{ mb: 2 }}>
          <Stack spacing={1.5}>
            <Box>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>Analyst done — review (and edit) the analysis, then run the Tester</Typography>
              <Typography variant="caption" color="text.secondary">Open the Analyst circle to review/edit. Edits you save steer the Tester.</Typography>
            </Box>
            <TextField label="Extra context for the Tester (optional)" size="small" multiline minRows={2} fullWidth
              value={testerCtx} onChange={e => setTesterCtx(e.target.value)}
              placeholder="Anything the Tester should keep in mind (special data, environment, flows to emphasize)…" />
            <Box>
              <Typography variant="caption" color="text.secondary" sx={{ mb: 0.75, display: 'block' }}>Reference Test Plan (optional — avoid recreating existing tests)</Typography>
              <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1, alignItems: 'center' }}>
                <FormControl size="small" sx={{ minWidth: 200 }}>
                  <InputLabel>Reference Test Plan</InputLabel>
                  <Select label="Reference Test Plan" value={refPlanId}
                    onChange={e => { setRefPlanId(e.target.value as number); setRefSuiteId('') }}>
                    <MenuItem value=""><em>None</em></MenuItem>
                    {plans.map(p => <MenuItem key={p.id} value={p.id}>{p.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{p.id}</Box></MenuItem>)}
                  </Select>
                </FormControl>
                <FormControl size="small" sx={{ minWidth: 200 }} disabled={!refPlanId}>
                  <InputLabel>Suite scope (optional)</InputLabel>
                  <Select label="Suite scope (optional)" value={refSuiteId}
                    onChange={e => setRefSuiteId(e.target.value as number)}>
                    <MenuItem value=""><em>All suites</em></MenuItem>
                    {refSuites.map(s => <MenuItem key={s.id} value={s.id}>{s.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{s.id}</Box></MenuItem>)}
                  </Select>
                </FormControl>
                {!!refPlanId && (
                  <Button size="small" variant="outlined" disabled={rescanning}
                    startIcon={rescanning ? <CircularProgress size={12} /> : undefined}
                    onClick={rescan}>
                    {rescanning ? 'Rescanning…' : 'Rescan plan'}
                  </Button>
                )}
              </Stack>
              {!!refPlanId && scanStatus && (
                <Typography variant="caption" color="text.secondary" sx={{ mt: 0.5, display: 'block' }}>
                  Indexed {scanStatus.caseCount} cases · scanned {new Date(scanStatus.scannedAt).toLocaleString()}
                </Typography>
              )}
            </Box>
            <TextField label="Plan navigation hint (optional)" size="small" fullWidth
              value={navHint} onChange={e => setNavHint(e.target.value)}
              placeholder='e.g. "Only Billing → Invoices; ignore Regression folder"'
              disabled={!refPlanId} />
            <Box sx={{ textAlign: 'right' }}>
              <Button variant="contained" size="small" onClick={runTester}>Run Tester</Button>
            </Box>
          </Stack>
        </Alert>
      )}

      {/* Findings (selected phase) vs Activity (verbose live console) */}
      <Stack direction="row" sx={{ mb: 1.5, alignItems: 'center' }}>
        <Typography variant="overline" color="text.secondary" sx={{ flex: 1 }}>{phaseLabel}</Typography>
        <ToggleButtonGroup size="small" exclusive value={view} onChange={(_e, v) => { if (v) setView(v) }}>
          <ToggleButton value="findings" sx={{ textTransform: 'none', py: 0.25 }}>Findings</ToggleButton>
          <ToggleButton value="activity" sx={{ textTransform: 'none', py: 0.25 }}>Activity</ToggleButton>
        </ToggleButtonGroup>
      </Stack>

      {view === 'activity' && (
        <AgentConsole title="Activity log" events={events} connected={connected} />
      )}

      {view === 'findings' && sel === 0 && (
        <Card variant="outlined"><CardContent>
          <Typography variant="subtitle2" gutterBottom>Ticket</Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
            <Chip size="small" label={`#${session.workItemId}`} />
            {wiType && <Chip size="small" variant="outlined" label={wiType} />}
            <Typography variant="body2" sx={{ flex: 1, minWidth: 0 }}>{session.workItemTitle}</Typography>
            <Link href={wiUrl(session.workItemId as number)} target="_blank" rel="noreferrer" variant="body2">Open in Azure DevOps</Link>
          </Stack>
        </CardContent></Card>
      )}

      {view === 'findings' && sel === 1 && (
        <Card variant="outlined"><CardContent>
          <Typography variant="subtitle2" gutterBottom>Knowledgebase retrieved ({kbFiles.length})</Typography>
          {kbFiles.length > 0 ? (
            <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
              {kbFiles.map((f, i) => <Chip key={`${f}-${i}`} size="small" variant="outlined" icon={<MenuBookIcon />} label={f} />)}
            </Stack>
          ) : (
            <Typography variant="body2" color="text.secondary">
              {inFlight ? 'Retrieving…' : 'No knowledgebase chunks were retrieved for this story.'}
            </Typography>
          )}
        </CardContent></Card>
      )}

      {view === 'findings' && sel === 2 && (
        <CodeBehaviorPanel session={session} inFlight={inFlight} onSaved={refetch} onError={onError} />
      )}

      {view === 'findings' && sel === 3 && (
        <>
          {session.status === 'Analyzing' && !session.analysisJson && <AnalystProgress streamUrl={streamUrl} />}
          <AnalysisPanel analysisJson={session.analysisJson}
            onSave={async (a) => { await saveAnalysis(session.id, a); refetch() }} />
          {session.status !== 'Analyzing' && !session.analysisJson && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>No analysis available.</Typography>
          )}
          {!!session.analysisJson && !inFlight && (
            <Card variant="outlined" sx={{ mt: 2 }}><CardContent>
              <Typography variant="subtitle2" gutterBottom>Refine analysis with feedback</Typography>
              <Typography variant="caption" color="text.secondary">
                Missing edge cases, permissions, or a whole area? Describe what to add — the Analyst improves
                the existing analysis in place (it won't start over).
              </Typography>
              <TextField size="small" multiline minRows={2} fullWidth sx={{ mt: 1 }}
                value={analystFeedback} onChange={e => setAnalystFeedback(e.target.value)}
                placeholder="e.g. No edge cases for expired sessions; add admin-vs-viewer permission checks." />
              <Box sx={{ textAlign: 'right', mt: 1 }}>
                <Button variant="outlined" size="small" disabled={analystBusy || !analystFeedback.trim()}
                  startIcon={analystBusy ? <CircularProgress size={14} /> : <PsychologyIcon />} onClick={refineAnalyst}>
                  Refine analysis
                </Button>
              </Box>
            </CardContent></Card>
          )}
        </>
      )}

      {view === 'findings' && sel === 4 && (
        <>
          {session.status === 'Drafting' && drafts.length === 0 && <TesterProgress streamUrl={streamUrl} />}
          {drafts.length === 0 && session.status !== 'Drafting' && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
              {session.status === 'AwaitingTester' ? 'Tester has not run yet — click "Run Tester" above.' : 'No test cases yet.'}
            </Typography>
          )}
          {(skippedDups.length > 0 || flaggedDups.length > 0) && (
            <Card variant="outlined" sx={{ mb: 2, borderColor: 'info.main' }}><CardContent>
              <Typography variant="subtitle2" gutterBottom>Dedup — existing ADO tests considered</Typography>
              {skippedDups.length > 0 && (
                <Box sx={{ mb: 1 }}>
                  <Typography variant="caption" color="text.secondary">Skipped (already covered):</Typography>
                  <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5, mt: 0.5 }}>
                    {skippedDups.map((d, i) => (
                      <Tooltip key={i} title={d.matchedTitle ? `Matched: ${d.matchedTitle}${d.matchedAdoId ? ` (TC #${d.matchedAdoId})` : ''}` : ''}>
                        <Chip size="small" color="info" variant="outlined" label={d.title} />
                      </Tooltip>
                    ))}
                  </Stack>
                </Box>
              )}
              {flaggedDups.length > 0 && (
                <Box>
                  <Typography variant="caption" color="text.secondary">Similar to existing (kept for review):</Typography>
                  <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5, mt: 0.5 }}>
                    {flaggedDups.map((d, i) => (
                      <Tooltip key={i} title={d.matchedTitle ? `Similar to: ${d.matchedTitle}${d.matchedAdoId ? ` (TC #${d.matchedAdoId})` : ''}` : ''}>
                        <Chip size="small" color="warning" variant="outlined" label={d.title} />
                      </Tooltip>
                    ))}
                  </Stack>
                </Box>
              )}
            </CardContent></Card>
          )}
          {drafts.length > 0 && (
        <>
          <Typography variant="subtitle2" color="text.secondary" sx={{ mb: 1 }}>Test cases generated — click a tile to review</Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(116px, 1fr))', gap: 1.5 }}>
            {tiles.map(t => (
              <Box key={t.key} role="button" tabIndex={0} onClick={() => openTile(t.key)}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openTile(t.key) } }}
                sx={{ cursor: 'pointer', borderRadius: 3, transition: 'transform .12s', '&:hover': { transform: 'translateY(-2px)' }, '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}>
                <StatTile icon={<RuleIcon />} label={t.label} value={t.value} color={t.color} />
              </Box>
            ))}
          </Box>

          <TestPushResults session={session} context={pushedContext} />

          <Dialog open={tcOpen} onClose={() => setTcOpen(false)} maxWidth="md" fullWidth scroll="paper">
            <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
              <Typography variant="h6" component="span" sx={{ fontWeight: 600 }}>
                {tcFilter === 'all' ? 'Generated test cases' : `${tcFilter === 'Draft' ? 'Pending' : tcFilter} test cases`}
              </Typography>
              <Chip size="small" label={visible.length} />
              <Box sx={{ flex: 1 }} />
              {tcFilter !== 'all' && <Button size="small" onClick={() => setTcFilter('all')}>Show all</Button>}
              <Button size="small" variant="outlined" onClick={approveAll}>Approve all</Button>
              <IconButton size="small" aria-label="close" onClick={() => setTcOpen(false)}><CloseIcon /></IconButton>
            </DialogTitle>
            <DialogContent dividers>
              <Stack spacing={1.5}>
                {visible.map(d => (
                  <TestDraftCard key={d.id} draft={d}
                    onApprove={() => approveDraft(d.id)} onReject={() => rejectDraft(d.id)} onPush={() => pushDraftRow(d.id)} onSave={p => saveDraft(d.id, p)}
                    canPush={!!selectedPlan && !!selectedSuite}
                    feedback={perTestFb[d.id]} onFeedbackChange={v => setPerTestFb(m => ({ ...m, [d.id]: v }))}
                    markedRemove={!!removeSet[d.id]} onToggleRemove={v => setRemoveSet(m => ({ ...m, [d.id]: v }))} />
                ))}
                {visible.length === 0 && (
                  <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
                    No {tcFilter === 'Draft' ? 'pending' : tcFilter} test cases.
                  </Typography>
                )}
              </Stack>
              <Divider sx={{ my: 2.5 }} />
              <Typography variant="subtitle2" gutterBottom>Regenerate with feedback</Typography>
              <Typography variant="caption" color="text.secondary">
                Leave feedback on individual tests above (or mark some to remove), and/or give overall direction.
                The Tester updates flagged tests, adds new ones, and drops only those you marked — pushed tests are kept.
              </Typography>
              <TextField size="small" multiline minRows={2} fullWidth sx={{ mt: 1 }}
                value={overallFeedback} onChange={e => setOverallFeedback(e.target.value)}
                placeholder="Overall feedback — e.g. add negative cases for invalid input; split the long happy-path test." />
              <Box sx={{ textAlign: 'right', mt: 1 }}>
                <Button variant="outlined" size="small"
                  disabled={refineBusy || (!overallFeedback.trim()
                    && !Object.values(perTestFb).some(v => (v ?? '').trim())
                    && !Object.values(removeSet).some(Boolean))}
                  startIcon={refineBusy ? <CircularProgress size={14} /> : <RuleIcon />}
                  onClick={regenerateTester}>Regenerate with feedback</Button>
              </Box>
              <Divider sx={{ my: 2.5 }} />
              <Typography variant="subtitle2" gutterBottom>Push approved to ADO</Typography>
              <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap', gap: 1, alignItems: 'center' }}>
                <FormControl size="small" sx={{ minWidth: 220 }}>
                  <InputLabel>Test Plan</InputLabel>
                  <Select label="Test Plan" value={selectedPlan} onChange={e => { setSelectedPlan(e.target.value as number); setSelectedSuite('') }}>
                    {plans.map(p => <MenuItem key={p.id} value={p.id}>{p.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{p.id}</Box></MenuItem>)}
                  </Select>
                </FormControl>
                <FormControl size="small" sx={{ minWidth: 220 }} disabled={!selectedPlan}>
                  <InputLabel>Test Suite</InputLabel>
                  <Select label="Test Suite" value={selectedSuite} onChange={e => setSelectedSuite(e.target.value as number)}>
                    {suites.map(s => <MenuItem key={s.id} value={s.id}>{s.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{s.id}</Box></MenuItem>)}
                  </Select>
                </FormControl>
              </Stack>
              {pushError && <Alert severity="error" sx={{ mt: 2 }}>{pushError}</Alert>}
            </DialogContent>
            <DialogActions sx={{ px: 3, py: 1.5 }}>
              <Button onClick={() => setTcOpen(false)}>Close</Button>
              <Button variant="contained" color="success" disabled={!canPush || pushing}
                startIcon={pushing ? <CircularProgress size={16} /> : <PushPinIcon />} onClick={push}>Push to ADO</Button>
            </DialogActions>
          </Dialog>
        </>
          )}
        </>
      )}
    </Box>
  )
}

// ── Start form (inside New dialog) ───────────────────────────────────────────
function AuthorStartForm({ onStarted, onError, onClose }: StartFormProps) {
  const [wiQuery, setWiQuery] = useState('')
  const [wiOptions, setWiOptions] = useState<WorkItemOption[]>([])
  const [selectedWi, setSelectedWi] = useState<WorkItemOption | null>(null)
  const [autoHandoff, setAutoHandoff] = useState(true)
  const [pauseAtReview, setPauseAtReview] = useState(false)
  const [additional, setAdditional] = useState('')
  const [starting, setStarting] = useState(false)
  const [startPlans, setStartPlans] = useState<TestPlan[]>([])
  const [startSuites, setStartSuites] = useState<TestSuite[]>([])
  const [startRefPlanId, setStartRefPlanId] = useState<number | ''>('')
  const [startRefSuiteId, setStartRefSuiteId] = useState<number | ''>('')
  const [startNavHint, setStartNavHint] = useState('')

  useEffect(() => {
    if (wiQuery.length < 2) { setWiOptions([]); return }
    const t = setTimeout(() => {
      searchWorkItems(wiQuery).then(setWiOptions).catch(() => setWiOptions([]))
    }, 400)
    return () => clearTimeout(t)
  }, [wiQuery])

  useEffect(() => { getPlans().then(setStartPlans).catch(() => {}) }, [])
  useEffect(() => {
    if (!startRefPlanId) { setStartSuites([]); return }
    getSuites(startRefPlanId).then(setStartSuites).catch(() => {})
  }, [startRefPlanId])

  async function start() {
    if (!selectedWi) return
    setStarting(true)
    try {
      // Auto + pause-at-review reuses the manual stop: don't auto-trigger Tester.
      const auto = autoHandoff && !pauseAtReview
      const { sessionId } = await startSession(PROJECT_ID, {
        workItemId: selectedWi.id, autoHandoff: auto,
        additionalContext: additional.trim() || undefined,
        referenceTestPlanId: startRefPlanId !== '' ? startRefPlanId : undefined,
        referenceTestSuiteId: startRefSuiteId !== '' ? startRefSuiteId : undefined,
        planNavigationHint: startNavHint.trim() || undefined,
      })
      onClose(); onStarted(sessionId)
    } catch (e) { onError(e, 'Start failed') } finally { setStarting(false) }
  }

  return (
    <Stack spacing={2} sx={{ pt: 1 }}>
      <Autocomplete options={wiOptions} filterOptions={x => x}
        getOptionLabel={o => `#${o.id} — ${o.title}`} isOptionEqualToValue={(a, b) => a.id === b.id}
        onInputChange={(_, v) => setWiQuery(v)} onChange={(_, v) => setSelectedWi(v)}
        renderInput={p => <TextField {...p} label="Search work item" size="small" />}
        noOptionsText={wiQuery.length < 2 ? 'Type 2+ chars' : 'No results'} />
      <Box>
        <Typography variant="body2" sx={{ mb: 0.75, fontWeight: 500 }}>Pipeline mode</Typography>
        <ToggleButtonGroup fullWidth size="small" exclusive value={autoHandoff ? 'auto' : 'manual'}
          onChange={(_, v) => { if (v) setAutoHandoff(v === 'auto') }}>
          <ToggleButton value="auto"><Stack sx={{ alignItems: 'flex-start', py: 0.5 }}>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>Automatic</Typography>
            <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>Analyst → Tester end-to-end</Typography>
          </Stack></ToggleButton>
          <ToggleButton value="manual"><Stack sx={{ alignItems: 'flex-start', py: 0.5 }}>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>Manual</Typography>
            <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>Pause after Analyst</Typography>
          </Stack></ToggleButton>
        </ToggleButtonGroup>
      </Box>
      <FormControlLabel control={<Switch checked={pauseAtReview} disabled={!autoHandoff} onChange={(_, v) => setPauseAtReview(v)} />}
        label={<Typography variant="body2">Pause at analysis review <Typography component="span" variant="caption" color="text.secondary">(stop before Tester)</Typography></Typography>} />
      <TextField label="Additional context (optional)" size="small" multiline minRows={2} fullWidth
        value={additional} onChange={e => setAdditional(e.target.value)}
        placeholder="Anything not in the story — e.g. the feature lives at route /admin/settings, Billing tab. Guides the Analyst (and code discovery)."
        helperText="Free-text guidance for the Analyst. Extra Tester context can be added later, at the review gate." />
      <Box>
        <Typography variant="body2" sx={{ mb: 0.75, fontWeight: 500 }}>Reference Test Plan (optional)</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ mb: 1, display: 'block' }}>Point the Tester at an existing plan so it avoids recreating covered tests.</Typography>
        <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1 }}>
          <FormControl size="small" sx={{ minWidth: 200 }}>
            <InputLabel>Reference Test Plan</InputLabel>
            <Select label="Reference Test Plan" value={startRefPlanId}
              onChange={e => { setStartRefPlanId(e.target.value as number); setStartRefSuiteId('') }}>
              <MenuItem value=""><em>None</em></MenuItem>
              {startPlans.map(p => <MenuItem key={p.id} value={p.id}>{p.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{p.id}</Box></MenuItem>)}
            </Select>
          </FormControl>
          <FormControl size="small" sx={{ minWidth: 200 }} disabled={!startRefPlanId}>
            <InputLabel>Suite scope (optional)</InputLabel>
            <Select label="Suite scope (optional)" value={startRefSuiteId}
              onChange={e => setStartRefSuiteId(e.target.value as number)}>
              <MenuItem value=""><em>All suites</em></MenuItem>
              {startSuites.map(s => <MenuItem key={s.id} value={s.id}>{s.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{s.id}</Box></MenuItem>)}
            </Select>
          </FormControl>
        </Stack>
      </Box>
      <TextField label="Plan navigation hint (optional)" size="small" fullWidth
        value={startNavHint} onChange={e => setStartNavHint(e.target.value)}
        placeholder='e.g. "Only Billing → Invoices; ignore Regression folder"'
        disabled={!startRefPlanId} />
      <Button variant="contained" onClick={start} disabled={starting || !selectedWi}>
        {starting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Run Analyst + Tester'}
      </Button>
    </Stack>
  )
}

const analyticsColumns: AnalyticsColumn[] = [
  { key: 'total', label: 'Total', align: 'right', value: d => d ? d.drafts.length : '—' },
  { key: 'pending', label: 'Pending', align: 'right', value: d => d ? d.drafts.filter(x => x.status === 'Draft').length : '—' },
  { key: 'approved', label: 'Approved', align: 'right', value: d => d ? d.drafts.filter(x => x.status === 'Approved').length : '—' },
  { key: 'pushed', label: 'Pushed', align: 'right', value: d => d ? d.drafts.filter(x => x.status === 'Pushed').length : '—' },
]

export const authorTestsConfig: SessionModuleConfig = {
  key: 'author-tests',
  apiBase: '/api/author-tests',
  route: '/author-tests',
  labels: {
    title: 'Author Tests',
    subtitle: 'Run the Analyst → Tester pipeline on a work item, review the drafted test cases, then push them into an ADO Test Suite.',
    newCta: 'New test run',
  },
  moduleChip: { label: 'TESTS', icon: <RuleIcon fontSize="small" /> },
  inFlightStatuses: ['Analyzing', 'Drafting', 'Pushing'],
  pipelineSteps: PIPELINE_STEPS,
  activeStepIndex,
  analyticsColumns,
  listSessions: (take, skip) => listSessionsPage(PROJECT_ID, take, skip),
  getDetail: (id) => getSessionDetail(id),
  streamUrl: sessionStreamUrl,
  StartForm: AuthorStartForm,
  DetailBody: AuthorDetailBody,
}
