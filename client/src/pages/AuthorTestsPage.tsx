import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, CardHeader,
  Chip, CircularProgress, Collapse, Divider, FormControl, IconButton,
  InputLabel, MenuItem, Select, Snackbar, Stack, TextField,
  Table, TableBody, TableCell, TableHead, TableRow,
  ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import PushPinIcon from '@mui/icons-material/PushPin'
import StopIcon from '@mui/icons-material/Stop'
import EditIcon from '@mui/icons-material/Edit'
import SaveIcon from '@mui/icons-material/Save'
import DeleteIcon from '@mui/icons-material/Delete'
import AddIcon from '@mui/icons-material/Add'
import DownloadIcon from '@mui/icons-material/Download'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import HistoryIcon from '@mui/icons-material/HistoryOutlined'
import SearchIcon from '@mui/icons-material/Search'
import MenuBookIcon from '@mui/icons-material/MenuBook'
import PsychologyIcon from '@mui/icons-material/Psychology'
import RuleIcon from '@mui/icons-material/Rule'
import LibraryAddCheckIcon from '@mui/icons-material/LibraryAddCheckOutlined'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutlineOutlined'
import axios from 'axios'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import StatusChip from '../components/StatusChip'
import StatTile from '../components/StatTile'
import EmptyState from '../components/EmptyState'
import AgentPipeline, { type PipelineStep } from '../components/AgentPipeline'
import AgentConsole from '../components/AgentConsole'
import { ThinkingShimmer } from '../components/LoadingSkeleton'
import { Stagger, StaggerItem } from '../motion/primitives'
import { type ConsoleEvent } from '../hooks/useEventStream'

function parseEventLog(json: string | null | undefined): ConsoleEvent[] {
  if (!json) return []
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : [] } catch { return [] }
}

const PROJECT_ID = 1

interface WorkItemOption { id: number; title: string; workItemType: string; state: string | null }
interface TestPlan { id: number; name: string }
interface TestSuite { id: number; name: string }
interface Step { action: string; expected: string }
interface Draft {
  id: number; title: string; preconditions: string; stepsJson: string
  status: 'Draft' | 'Approved' | 'Pushed' | 'Rejected'; sortOrder: number
  adoTestCaseId: number | null
}
type Status =
  | 'Analyzing' | 'AwaitingTester' | 'Drafting' | 'Drafted'
  | 'Pushing' | 'Done' | 'Failed' | 'Cancelled'
interface Session {
  id: number; workItemId: number; workItemTitle: string
  status: Status
  currentActivity: string | null
  analysisJson: string | null; errorMessage: string | null; drafts: Draft[]
  eventLogJson?: string | null
}
interface SessionSummary { id: number; workItemId: number; workItemTitle: string; status: string; createdAt: string; errorMessage: string | null }

/** Pipeline stages, in display order. Each maps to which session statuses count as "past" it. */
const PIPELINE_STEPS: PipelineStep[] = [
  { key: 'fetch',    label: 'Fetch ticket',    icon: <SearchIcon /> },
  { key: 'retrieve', label: 'Retrieve KB',     icon: <MenuBookIcon /> },
  { key: 'analyst',  label: 'Analyst',         icon: <PsychologyIcon /> },
  { key: 'tester',   label: 'Tester',          icon: <RuleIcon /> },
]

function activeStepIndex(status: Status, activity: string | null): number {
  // Map current status / activity text → which step is currently in progress.
  const a = (activity ?? '').toLowerCase()
  if (status === 'Done' || status === 'Drafted') return PIPELINE_STEPS.length
  // Manual mode parks after Analyst — show the Tester step as "next up" so the bar
  // visibly stops at the gate.
  if (status === 'AwaitingTester') return 3
  if (status === 'Failed' || status === 'Cancelled') {
    if (a.includes('analyst')) return 2
    if (a.includes('retriev')) return 1
    if (a.includes('fetch'))   return 0
    if (a.includes('tester'))  return 3
    return 0
  }
  if (status === 'Drafting' || a.includes('tester')) return 3
  if (a.includes('analyst'))   return 2
  if (a.includes('retriev'))   return 1
  if (a.includes('fetch') || a.includes('work item')) return 0
  return 0
}

export default function AuthorTestsPage() {
  const [wiQuery, setWiQuery] = useState('')
  const [wiOptions, setWiOptions] = useState<WorkItemOption[]>([])
  const [selectedWi, setSelectedWi] = useState<WorkItemOption | null>(null)
  const [autoHandoff, setAutoHandoff] = useState(true)
  const [starting, setStarting] = useState(false)
  const [startError, setStartError] = useState('')
  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [activeSession, setActiveSession] = useState<Session | null>(null)
  const [polling, setPolling] = useState(false)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const [plans, setPlans] = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])
  const [selectedPlan, setSelectedPlan] = useState<number | ''>('')
  const [selectedSuite, setSelectedSuite] = useState<number | ''>('')
  const [pushing, setPushing] = useState(false)
  const [pushError, setPushError] = useState('')
  // Track last-pushed plan/suite per session so the results card shows context.
  const [pushedContext, setPushedContext] = useState<Record<number, { plan: TestPlan; suite: TestSuite }>>({})
  // Generic action-error toast — surfaced from approve/reject/save/cancel.
  const [actionError, setActionError] = useState('')

  // Search work items with debounce
  useEffect(() => {
    if (wiQuery.length < 2) { setWiOptions([]); return }
    const t = setTimeout(() => {
      axios.get<WorkItemOption[]>('/api/author-tests/search', { params: { q: wiQuery } })
        .then(r => setWiOptions(r.data))
        .catch(() => setWiOptions([]))
    }, 400)
    return () => clearTimeout(t)
  }, [wiQuery])

  // Load session list (paged)
  const loadSessions = useCallback(async () => {
    try {
      const { data } = await axios.get<{ total: number; items: SessionSummary[] }>(
        `/api/author-tests/sessions/${PROJECT_ID}`, { params: { take: 30 } })
      setSessions(data.items ?? [])
    } catch { /* surfaced elsewhere */ }
  }, [])
  useEffect(() => { loadSessions() }, [loadSessions])

  // Load plans on mount
  useEffect(() => {
    axios.get<TestPlan[]>('/api/author-tests/plans').then(r => setPlans(r.data)).catch(() => {})
  }, [])

  useEffect(() => {
    if (!selectedPlan) { setSuites([]); return }
    axios.get<TestSuite[]>(`/api/author-tests/plans/${selectedPlan}/suites`).then(r => setSuites(r.data)).catch(() => {})
  }, [selectedPlan])

  async function start() {
    if (!selectedWi) return
    setStartError(''); setStarting(true)
    try {
      const { data } = await axios.post<{ sessionId: number }>(
        `/api/author-tests/${PROJECT_ID}/start`,
        { workItemId: selectedWi.id, autoHandoff })
      await loadSessions()
      await openSession(data.sessionId)
    } catch (e: unknown) {
      // FastAPI returns { detail: "..." } — make sure the actual message surfaces
      // instead of "[object Object]".
      let msg = 'Unknown error'
      if (axios.isAxiosError(e)) {
        const d = e.response?.data as unknown
        if (typeof d === 'string') msg = d
        else if (d && typeof d === 'object' && 'detail' in d) msg = String((d as { detail: string }).detail)
        else msg = e.message
      } else if (e instanceof Error) msg = e.message
      else msg = String(e)
      setStartError(msg)
    } finally { setStarting(false) }
  }

  async function openSession(id: number) {
    const { data } = await axios.get<Session>(`/api/author-tests/sessions/detail/${id}`)
    setActiveSession(data)
    if (['Analyzing', 'Drafting', 'Pushing'].includes(data.status)) startPolling(id)
    else stopPolling()
  }

  function startPolling(id: number) {
    setPolling(true)
    if (pollRef.current) clearInterval(pollRef.current)
    pollRef.current = setInterval(async () => {
      const { data } = await axios.get<Session>(`/api/author-tests/sessions/detail/${id}`)
      setActiveSession(data)
      if (!['Analyzing', 'Drafting', 'Pushing'].includes(data.status)) {
        stopPolling(); await loadSessions()
      }
    }, 2500)
  }
  function stopPolling() {
    setPolling(false)
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null }
  }
  useEffect(() => () => stopPolling(), [])

  function reportError(e: unknown, fallback: string) {
    const msg = axios.isAxiosError(e)
      ? (typeof e.response?.data === 'string' ? e.response.data : e.response?.data?.title ?? e.message)
      : String(e)
    setActionError(`${fallback}: ${msg}`)
  }

  async function approveDraft(id: number) {
    try {
      await axios.patch(`/api/author-tests/drafts/${id}`, { status: 'Approved' })
      if (activeSession) openSession(activeSession.id)
    } catch (e) { reportError(e, 'Approve failed') }
  }
  async function rejectDraft(id: number) {
    try {
      await axios.patch(`/api/author-tests/drafts/${id}`, { status: 'Rejected' })
      if (activeSession) openSession(activeSession.id)
    } catch (e) { reportError(e, 'Reject failed') }
  }
  async function approveAll() {
    if (!activeSession) return
    try {
      await axios.post(`/api/author-tests/sessions/${activeSession.id}/approve-all`)
      openSession(activeSession.id)
    } catch (e) { reportError(e, 'Approve all failed') }
  }
  async function saveDraft(id: number, patch: { title?: string; preconditions?: string; steps?: Step[] }) {
    const body: { title?: string; preconditions?: string; stepsJson?: string } = {}
    if (patch.title !== undefined) body.title = patch.title
    if (patch.preconditions !== undefined) body.preconditions = patch.preconditions
    if (patch.steps !== undefined) body.stepsJson = JSON.stringify(patch.steps)
    try {
      await axios.patch(`/api/author-tests/drafts/${id}`, body)
      if (activeSession) openSession(activeSession.id)
    } catch (e) { reportError(e, 'Save failed') }
  }

  async function runTester() {
    if (!activeSession) return
    setActionError('')
    try {
      await axios.post(`/api/author-tests/sessions/${activeSession.id}/run-tester`)
      // Optimistic flip — polling will then keep status fresh.
      setActiveSession(s => s ? { ...s, status: 'Drafting', currentActivity: 'Tester — generating test cases…' } : s)
      startPolling(activeSession.id)
    } catch (e) { reportError(e, 'Run Tester failed') }
  }

  async function cancel() {
    if (!activeSession) return
    try { await axios.post(`/api/author-tests/sessions/${activeSession.id}/cancel`) }
    catch (e) { reportError(e, 'Cancel failed') }
  }

  async function push() {
    if (!activeSession || !selectedPlan || !selectedSuite) return
    setPushing(true); setPushError('')
    try {
      // Fire-and-forget — push runs in the background; polling tracks progress.
      await axios.post(`/api/author-tests/sessions/${activeSession.id}/push`,
        { testPlanId: selectedPlan, testSuiteId: selectedSuite })
      const plan = plans.find(p => p.id === selectedPlan)
      const suite = suites.find(s => s.id === selectedSuite)
      if (plan && suite) setPushedContext(prev => ({ ...prev, [activeSession.id]: { plan, suite } }))
      openSession(activeSession.id)   // starts polling because Pushing ∈ in-flight statuses
    } catch (e: unknown) {
      const data = axios.isAxiosError(e) ? e.response?.data : null
      const msg = (data && typeof data === 'object' && 'error' in data)
        ? String((data as { error: string }).error)
        : (axios.isAxiosError(e) ? e.message : String(e))
      setPushError(msg)
    } finally { setPushing(false) }
  }

  const canPush = activeSession?.drafts.some(d => d.status === 'Approved') && selectedPlan && selectedSuite

  return (
    <Box>
      <PageHeader
        title="Author Tests"
        subtitle="Run the Analyst → Tester pipeline on a work item, review the drafted test cases, then push them into an ADO Test Suite."
      />

      <Stagger>
        <Box sx={{ display: 'grid', gap: 2, mb: 3, gridTemplateColumns: { xs: '1fr 1fr', sm: 'repeat(3, 220px)' } }}>
          <StaggerItem>
            <StatTile icon={<RuleIcon />} label="Sessions" value={sessions.length} color="primary.main" />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<LibraryAddCheckIcon />} label="Drafted" color="success.main"
              value={sessions.filter(s => s.status === 'Drafted' || s.status === 'Done').length} />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<ErrorOutlineIcon />} label="Failed" color="error.main"
              value={sessions.filter(s => s.status === 'Failed').length} />
          </StaggerItem>
        </Box>
      </Stagger>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>

        {/* Left: start panel + session list */}
        <Stack spacing={2} sx={{ minWidth: 320, maxWidth: 380, width: '100%' }}>
          <Section title="New session">
            <Stack spacing={2}>
              <Autocomplete
                options={wiOptions}
                filterOptions={x => x}
                getOptionLabel={o => `#${o.id} — ${o.title}`}
                isOptionEqualToValue={(a, b) => a.id === b.id}
                onInputChange={(_, v) => setWiQuery(v)}
                onChange={(_, v) => setSelectedWi(v)}
                renderInput={p => <TextField {...p} label="Search work item" size="small" />}
                noOptionsText={wiQuery.length < 2 ? 'Type 2+ chars' : 'No results'}
              />
              <Box>
                <Typography variant="body2" sx={{ mb: 0.75, fontWeight: 500 }}>
                  Pipeline mode
                </Typography>
                <ToggleButtonGroup
                  fullWidth size="small" exclusive
                  value={autoHandoff ? 'auto' : 'manual'}
                  onChange={(_, v) => { if (v) setAutoHandoff(v === 'auto') }}
                >
                  <ToggleButton value="auto">
                    <Stack sx={{ alignItems: 'flex-start', textAlign: 'left', py: 0.5 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600 }}>Automatic</Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>
                        Analyst → Tester runs end-to-end
                      </Typography>
                    </Stack>
                  </ToggleButton>
                  <ToggleButton value="manual">
                    <Stack sx={{ alignItems: 'flex-start', textAlign: 'left', py: 0.5 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600 }}>Manual</Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>
                        Pause after Analyst, then click Run Tester
                      </Typography>
                    </Stack>
                  </ToggleButton>
                </ToggleButtonGroup>
              </Box>
              <Button variant="contained" onClick={start} disabled={starting || !selectedWi}>
                {starting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Run Analyst + Tester'}
              </Button>
              {startError && <Alert severity="error">{startError}</Alert>}
            </Stack>
          </Section>

          <Section title="Recent sessions" dense>
            {sessions.length === 0 ? (
              <EmptyState compact icon={<HistoryIcon />} title="No sessions yet"
                hint="Start a session above to see it here." />
            ) : (
              <Stagger>
                <Stack spacing={0.5}>
                  {sessions.slice(0, 15).map(s => (
                    <StaggerItem key={s.id}>
                      <Box onClick={() => openSession(s.id)} sx={{
                        p: 1, borderRadius: 1.5, cursor: 'pointer',
                        bgcolor: activeSession?.id === s.id ? 'action.selected' : 'transparent',
                        '&:hover': { bgcolor: 'action.hover' },
                      }}>
                        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                          <StatusChip value={s.status} />
                          <Typography variant="body2" noWrap>#{s.workItemId}</Typography>
                        </Stack>
                        <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
                          {s.workItemTitle}
                        </Typography>
                      </Box>
                    </StaggerItem>
                  ))}
                </Stack>
              </Stagger>
            )}
          </Section>
        </Stack>

        {/* Right: session detail */}
        {activeSession && (
          <Box sx={{ flex: 1, minWidth: 0, width: '100%' }}>
            <Card>
              <CardHeader
                title={<Typography variant="h6">#{activeSession.workItemId} — {activeSession.workItemTitle}</Typography>}
                subheader={
                  <Stack direction="row" spacing={1} sx={{ mt: 0.5, alignItems: 'center' }}>
                    <StatusChip value={activeSession.status} />
                    {polling && <CircularProgress size={14} />}
                  </Stack>
                }
                action={
                  ['Analyzing', 'Drafting', 'Pushing'].includes(activeSession.status) && (
                    <Tooltip title="Cancel this session">
                      <Button color="warning" size="small" startIcon={<StopIcon />} onClick={cancel}>
                        Cancel
                      </Button>
                    </Tooltip>
                  )
                }
              />
              <Divider />
              <CardContent>
                {/* Pipeline stepper */}
                <AgentPipeline
                  steps={PIPELINE_STEPS}
                  activeIndex={activeStepIndex(activeSession.status, activeSession.currentActivity)}
                  inFlight={['Analyzing', 'Drafting'].includes(activeSession.status)}
                  failed={activeSession.status === 'Failed' || activeSession.status === 'Cancelled'}
                  currentActivity={activeSession.currentActivity}
                />

                <AgentConsole
                  key={activeSession.id}
                  title="Pipeline console"
                  streamUrl={['Analyzing', 'Drafting', 'Pushing'].includes(activeSession.status)
                    ? `/api/author-tests/sessions/${activeSession.id}/stream` : null}
                  seedEvents={parseEventLog(activeSession.eventLogJson)}
                />

                {activeSession.status === 'Failed' && (
                  <Alert severity="error" sx={{ mb: 2 }}>
                    <Box sx={{ fontWeight: 600, mb: 0.25 }}>Session failed</Box>
                    <Box sx={{ whiteSpace: 'pre-wrap' }}>
                      {activeSession.errorMessage
                        || activeSession.currentActivity
                        || 'No error details were captured. Check the API console log.'}
                    </Box>
                  </Alert>
                )}
                {activeSession.status === 'Cancelled' && (
                  <Alert severity="warning" sx={{ mb: 2 }}>Session cancelled by user.</Alert>
                )}

                {/* Manual hand-off gate */}
                {activeSession.status === 'AwaitingTester' && (
                  <Alert severity="info" icon={false} sx={{
                    mb: 2, alignItems: 'center',
                    '& .MuiAlert-message': { width: '100%' },
                  }}>
                    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}
                      sx={{ alignItems: { sm: 'center' } }}>
                      <Box sx={{ flex: 1 }}>
                        <Typography variant="body2" sx={{ fontWeight: 600 }}>
                          Analyst done — review the analysis, then run the Tester
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          Manual hand-off is on. Tester will turn the Analyst's expected behavior into test cases.
                        </Typography>
                      </Box>
                      <Button variant="contained" size="small" onClick={runTester}>
                        Run Tester
                      </Button>
                    </Stack>
                  </Alert>
                )}

                {/* Drafts */}
                {activeSession.drafts.length > 0 && (
                  <>
                    <Stack direction="row" spacing={1} sx={{ mb: 2, alignItems: 'center' }}>
                      <Typography variant="subtitle1">{activeSession.drafts.length} test case(s)</Typography>
                      <Button size="small" variant="outlined" onClick={approveAll}>Approve all</Button>
                    </Stack>

                    <Stagger>
                      <Stack spacing={2}>
                        {[...activeSession.drafts].sort((a, b) => a.sortOrder - b.sortOrder).map(d => (
                          <StaggerItem key={d.id}>
                            <DraftCard draft={d}
                              onApprove={() => approveDraft(d.id)}
                              onReject={() => rejectDraft(d.id)}
                              onSave={(patch) => saveDraft(d.id, patch)}
                            />
                          </StaggerItem>
                        ))}
                      </Stack>
                    </Stagger>

                    <Divider sx={{ my: 3 }} />
                    <Typography variant="subtitle2" gutterBottom>Push approved to ADO</Typography>
                    <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap', gap: 1 }}>
                      <FormControl size="small" sx={{ minWidth: 240 }}>
                        <InputLabel>Test Plan</InputLabel>
                        <Select label="Test Plan" value={selectedPlan}
                          onChange={e => { setSelectedPlan(e.target.value as number); setSelectedSuite('') }}>
                          {plans.map(p => (
                            <MenuItem key={p.id} value={p.id}>
                              {p.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{p.id}</Box>
                            </MenuItem>
                          ))}
                        </Select>
                      </FormControl>
                      <FormControl size="small" sx={{ minWidth: 240 }} disabled={!selectedPlan}>
                        <InputLabel>Test Suite</InputLabel>
                        <Select label="Test Suite" value={selectedSuite}
                          onChange={e => setSelectedSuite(e.target.value as number)}>
                          {suites.map(s => (
                            <MenuItem key={s.id} value={s.id}>
                              {s.name} <Box component="span" sx={{ ml: 1, color: 'text.secondary', fontSize: '0.85em' }}>#{s.id}</Box>
                            </MenuItem>
                          ))}
                        </Select>
                      </FormControl>
                      <Button variant="contained" color="success" startIcon={<PushPinIcon />}
                        onClick={push} disabled={!canPush || pushing}>
                        {pushing ? <CircularProgress size={18} /> : 'Push to ADO'}
                      </Button>
                    </Stack>
                    {pushError && <Alert severity="error" sx={{ mt: 2 }}>{pushError}</Alert>}
                    {activeSession.status === 'Failed' && activeSession.errorMessage &&
                      !pushError && <Alert severity="error" sx={{ mt: 2 }}>{activeSession.errorMessage}</Alert>}

                    <PushResults
                      session={activeSession}
                      context={pushedContext[activeSession.id]}
                      adoOrg="ConstellationDealer"
                      adoProject="Ideal Agile"
                    />
                  </>
                )}

                {['Analyzing', 'Drafting'].includes(activeSession.status) && activeSession.drafts.length === 0 && (
                  <ThinkingShimmer
                    label={activeSession.status === 'Drafting'
                      ? 'Tester is drafting test cases…'
                      : 'Analyst is analyzing the story…'}
                  />
                )}
              </CardContent>
            </Card>
          </Box>
        )}
      </Stack>

      <Snackbar
        open={!!actionError}
        autoHideDuration={6000}
        onClose={() => setActionError('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
      >
        <Alert severity="error" onClose={() => setActionError('')} variant="filled">{actionError}</Alert>
      </Snackbar>
    </Box>
  )
}

interface DraftPatch { title?: string; preconditions?: string; steps?: Step[] }

function DraftCard({ draft, onApprove, onReject, onSave }: {
  draft: Draft; onApprove: () => void; onReject: () => void
  onSave: (patch: DraftPatch) => Promise<void> | void
}) {
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  const [saving, setSaving] = useState(false)

  // Local working copies for edit mode
  const initialSteps = (): Step[] => { try { return JSON.parse(draft.stepsJson) } catch { return [] } }
  const [title, setTitle] = useState(draft.title)
  const [preconds, setPreconds] = useState(draft.preconditions)
  const [steps, setSteps] = useState<Step[]>(initialSteps())

  // Reset local state when the underlying draft changes (re-fetch from server after save)
  useEffect(() => {
    if (!editing) {
      setTitle(draft.title)
      setPreconds(draft.preconditions)
      setSteps(initialSteps())
    }

  }, [draft.id, draft.title, draft.preconditions, draft.stepsJson])

  const isPushed = draft.status === 'Pushed'
  const isEditable = !isPushed && draft.status !== 'Rejected'

  function updateStep(i: number, field: 'action' | 'expected', value: string) {
    setSteps(curr => curr.map((s, idx) => idx === i ? { ...s, [field]: value } : s))
  }
  function addStep() { setSteps(curr => [...curr, { action: '', expected: '' }]) }
  function removeStep(i: number) { setSteps(curr => curr.filter((_, idx) => idx !== i)) }
  function moveStep(i: number, dir: -1 | 1) {
    setSteps(curr => {
      const j = i + dir
      if (j < 0 || j >= curr.length) return curr
      const next = [...curr]
      ;[next[i], next[j]] = [next[j], next[i]]
      return next
    })
  }

  async function commitEdits() {
    setSaving(true)
    try {
      await onSave({ title, preconditions: preconds, steps })
      setEditing(false)
    } finally { setSaving(false) }
  }

  function cancelEdits() {
    setTitle(draft.title); setPreconds(draft.preconditions); setSteps(initialSteps())
    setEditing(false)
  }

  return (
    <Card sx={editing ? { borderColor: 'primary.main' } : undefined}>
      <CardContent sx={{ pb: '12px !important' }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
          <StatusChip value={draft.status} kind="draft" />
          {editing ? (
            <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)}
              placeholder="[Module] - [Feature] - Verify that ..." />
          ) : (
            <Typography variant="body2" sx={{ flex: 1, fontWeight: 500 }}>{draft.title}</Typography>
          )}

          {!editing && isEditable && (
            <>
              <Tooltip title="Edit"><IconButton size="small" onClick={() => { setEditing(true); setOpen(true) }}><EditIcon fontSize="small" /></IconButton></Tooltip>
              <Tooltip title="Approve"><IconButton size="small" color="success" onClick={onApprove}><CheckIcon fontSize="small" /></IconButton></Tooltip>
              <Tooltip title="Reject"><IconButton size="small" color="error" onClick={onReject}><CloseIcon fontSize="small" /></IconButton></Tooltip>
            </>
          )}
          {editing && (
            <>
              <Tooltip title="Save">
                <span><IconButton size="small" color="primary" onClick={commitEdits} disabled={saving}>
                  {saving ? <CircularProgress size={16} /> : <SaveIcon fontSize="small" />}
                </IconButton></span>
              </Tooltip>
              <Tooltip title="Cancel"><IconButton size="small" onClick={cancelEdits} disabled={saving}><CloseIcon fontSize="small" /></IconButton></Tooltip>
            </>
          )}
          <IconButton size="small" onClick={() => setOpen(v => !v)}>
            {open ? <ExpandLessIcon /> : <ExpandMoreIcon />}
          </IconButton>
        </Stack>

        <Collapse in={open}>
          <Box sx={{ mt: 1 }}>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
              <strong>Preconditions</strong>
            </Typography>
            {editing ? (
              <TextField fullWidth size="small" multiline minRows={1} value={preconds}
                onChange={e => setPreconds(e.target.value)} />
            ) : (
              <Typography variant="body2">{draft.preconditions || <em>none</em>}</Typography>
            )}
          </Box>

          <Box sx={{ overflowX: 'auto', mt: 1 }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ width: 32 }}>#</TableCell>
                  <TableCell>Action</TableCell>
                  <TableCell>Expected Result</TableCell>
                  {editing && <TableCell sx={{ width: 96 }} />}
                </TableRow>
              </TableHead>
              <TableBody>
                {steps.map((s, i) => (
                  <TableRow key={i}>
                    <TableCell>{i + 1}</TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top' }}>
                      {editing
                        ? <TextField fullWidth size="small" multiline value={s.action}
                            onChange={e => updateStep(i, 'action', e.target.value)} />
                        : s.action}
                    </TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top' }}>
                      {editing
                        ? <TextField fullWidth size="small" multiline value={s.expected}
                            onChange={e => updateStep(i, 'expected', e.target.value)} />
                        : s.expected}
                    </TableCell>
                    {editing && (
                      <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>
                        <Tooltip title="Move up"><span><IconButton size="small" disabled={i === 0}
                          onClick={() => moveStep(i, -1)}><ExpandLessIcon fontSize="small" /></IconButton></span></Tooltip>
                        <Tooltip title="Move down"><span><IconButton size="small" disabled={i === steps.length - 1}
                          onClick={() => moveStep(i, 1)}><ExpandMoreIcon fontSize="small" /></IconButton></span></Tooltip>
                        <Tooltip title="Delete step"><IconButton size="small" color="error"
                          onClick={() => removeStep(i)}><DeleteIcon fontSize="small" /></IconButton></Tooltip>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {editing && (
              <Button size="small" startIcon={<AddIcon />} onClick={addStep} sx={{ mt: 1 }}>
                Add step
              </Button>
            )}
          </Box>

          {draft.adoTestCaseId && (
            <Typography variant="caption" color="success.main" sx={{ mt: 1, display: 'block' }}>
              ADO Test Case #{draft.adoTestCaseId}
            </Typography>
          )}
        </Collapse>
      </CardContent>
    </Card>
  )
}

// ─────────────────────────────────────────────────────────────────────────────
// Post-push results card
// ─────────────────────────────────────────────────────────────────────────────
function PushResults({ session, context, adoOrg, adoProject }: {
  session: Session
  context: { plan: TestPlan; suite: TestSuite } | undefined
  adoOrg: string; adoProject: string
}) {
  const pushed = session.drafts.filter(d => d.status === 'Pushed' && d.adoTestCaseId)
  if (pushed.length === 0) return null
  // Only show the prominent results panel once the session is finished
  if (!['Done', 'Cancelled', 'Failed'].includes(session.status)) return null

  const wiUrl = (id: number) =>
    `https://dev.azure.com/${encodeURIComponent(adoOrg)}/${encodeURIComponent(adoProject)}/_workitems/edit/${id}`

  function downloadCsv() {
    const planId = context?.plan.id ?? ''
    const planName = context?.plan.name ?? ''
    const suiteId = context?.suite.id ?? ''
    const suiteName = context?.suite.name ?? ''
    const header = ['SessionId','StoryId','StoryTitle','TestCaseId','TestCaseTitle','PlanId','PlanName','SuiteId','SuiteName','AdoUrl']
    const escape = (v: unknown) => {
      const s = String(v ?? '')
      return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
    }
    const rows = pushed.map(d => ([
      session.id, session.workItemId, session.workItemTitle,
      d.adoTestCaseId, d.title, planId, planName, suiteId, suiteName,
      wiUrl(d.adoTestCaseId!),
    ] as unknown[]).map(escape).join(','))
    const csv = [header.join(','), ...rows].join('\n')
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `push-results-session-${session.id}.csv`
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Card sx={{ mt: 3, borderColor: 'success.main' }}>
      <CardContent>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
          <CheckIcon color="success" />
          <Typography variant="h6">Push results — {pushed.length} test case(s) pushed</Typography>
          <Box sx={{ flex: 1 }} />
          <Button size="small" variant="outlined" startIcon={<DownloadIcon />} onClick={downloadCsv}>
            Download CSV
          </Button>
        </Stack>
        {context && (
          <Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: 'wrap', gap: 0.5 }}>
            <Chip size="small" label={`Plan: ${context.plan.name} #${context.plan.id}`} />
            <Chip size="small" label={`Suite: ${context.suite.name} #${context.suite.id}`} />
            <Chip size="small" label={`Story: #${session.workItemId}`} />
          </Stack>
        )}
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ width: 96 }}>TC ID</TableCell>
              <TableCell>Title</TableCell>
              <TableCell sx={{ width: 64 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {pushed.map(d => (
              <TableRow key={d.id}>
                <TableCell><strong>#{d.adoTestCaseId}</strong></TableCell>
                <TableCell>{d.title}</TableCell>
                <TableCell>
                  <Tooltip title="Open in Azure DevOps">
                    <IconButton size="small" component="a" href={wiUrl(d.adoTestCaseId!)} target="_blank" rel="noreferrer">
                      <OpenInNewIcon fontSize="small" />
                    </IconButton>
                  </Tooltip>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  )
}
