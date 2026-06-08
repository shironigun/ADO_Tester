import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, CardHeader, Chip, CircularProgress,
  Collapse, Divider, FormControl, IconButton, InputLabel, List, ListItem, ListItemButton,
  ListItemText, MenuItem, Select, Snackbar, Stack, Step, StepLabel, Stepper, TextField,
  Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import EditIcon from '@mui/icons-material/Edit'
import SaveIcon from '@mui/icons-material/Save'
import StopIcon from '@mui/icons-material/Stop'
import PushPinIcon from '@mui/icons-material/PushPin'
import BugReportIcon from '@mui/icons-material/BugReport'
import SearchIcon from '@mui/icons-material/Search'
import PsychologyIcon from '@mui/icons-material/Psychology'
import VisibilityIcon from '@mui/icons-material/Visibility'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import axios from 'axios'

const PROJECT_ID = 1
const ADO_ORG = 'ConstellationDealer'
const ADO_PROJECT = 'Ideal Agile'

type Status =
  | 'Analyzing' | 'AwaitingActual' | 'Drafting' | 'Drafted'
  | 'Pushing' | 'Done' | 'Failed' | 'Cancelled'

type DraftStatus = 'Draft' | 'Approved' | 'Rejected' | 'Pushed'
type Severity = 'Critical' | 'High' | 'Medium' | 'Low'

interface WorkItemOption { id: number; title: string; workItemType: string }
interface SessionSummary { id: number; workItemId: number; workItemTitle: string; status: Status; createdAt: string }
interface BugDraft {
  id: number
  title: string
  severity: Severity
  preconditions: string
  reproStepsJson: string
  expectedBehavior: string
  actualBehavior: string
  suggestedFix: string
  status: DraftStatus
  adoBugId: number | null
}
interface BugSession {
  id: number
  workItemId: number
  workItemTitle: string
  status: Status
  currentActivity: string | null
  errorMessage: string | null
  actualBehavior: string | null
  codeContext: string | null
  drafts: BugDraft[]
}

const statusColors: Record<Status, 'default' | 'warning' | 'success' | 'error' | 'info'> = {
  Analyzing: 'warning', AwaitingActual: 'info', Drafting: 'warning', Drafted: 'info',
  Pushing: 'warning', Done: 'success', Failed: 'error', Cancelled: 'default',
}
const draftColors: Record<DraftStatus, 'default' | 'warning' | 'success' | 'error'> = {
  Draft: 'default', Approved: 'success', Pushed: 'success', Rejected: 'error',
}
const sevColors: Record<Severity, 'default' | 'warning' | 'success' | 'error' | 'info'> = {
  Critical: 'error', High: 'warning', Medium: 'info', Low: 'default',
}

const PIPELINE_STEPS = [
  { key: 'fetch',    label: 'Fetch ticket',  icon: <SearchIcon /> },
  { key: 'analyst',  label: 'Analyst',       icon: <PsychologyIcon /> },
  { key: 'actual',   label: 'Actual behavior', icon: <VisibilityIcon /> },
  { key: 'bug',      label: 'Bug Writer',    icon: <BugReportIcon /> },
]

function activeStepIndex(s: Status, activity: string | null): number {
  const a = (activity ?? '').toLowerCase()
  if (s === 'Done' || s === 'Drafted' || s === 'Pushing') return PIPELINE_STEPS.length
  if (s === 'AwaitingActual') return 2
  if (s === 'Drafting' || a.includes('bug writer')) return 3
  if (a.includes('analyst')) return 1
  if (a.includes('fetch') || a.includes('ticket')) return 0
  return 0
}

function PipelineIcon({ active, completed, running, failed, icon }: {
  active: boolean; completed: boolean; running: boolean; failed: boolean; icon: React.ReactNode
}) {
  const color = failed ? 'error.main' : completed ? 'success.main' : active ? 'primary.main' : 'text.disabled'
  const bg    = failed ? 'error.lighter' : completed ? 'success.lighter' : active ? 'primary.lighter' : 'transparent'
  return (
    <Box sx={{
      position: 'relative', width: 44, height: 44, borderRadius: '50%',
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      bgcolor: bg, border: '2px solid', borderColor: color, color, transition: 'all 200ms',
    }}>
      {running && (
        <CircularProgress size={48} thickness={3}
          sx={{ position: 'absolute', top: -4, left: -4, color: 'primary.main' }} />
      )}
      {completed ? <CheckIcon /> : icon}
    </Box>
  )
}

function axiosMessage(e: unknown): string {
  if (!axios.isAxiosError(e)) return String(e)
  const d = e.response?.data as unknown
  if (typeof d === 'string') return d
  if (d && typeof d === 'object' && 'title' in d) return String((d as { title: string }).title)
  return e.message
}

export default function FindBugsPage() {
  const [wiQuery, setWiQuery] = useState('')
  const [wiOptions, setWiOptions] = useState<WorkItemOption[]>([])
  const [selectedWi, setSelectedWi] = useState<WorkItemOption | null>(null)
  const [starting, setStarting] = useState(false)
  const [actionError, setActionError] = useState('')

  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [active, setActive] = useState<BugSession | null>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const [actualText, setActualText] = useState('')
  const [codeText, setCodeText] = useState('')
  const [pushing, setPushing] = useState(false)

  // ── work item search ────────────────────────────────────────────────────
  useEffect(() => {
    if (wiQuery.length < 2) { setWiOptions([]); return }
    const t = setTimeout(() => {
      axios.get<WorkItemOption[]>('/api/author-tests/search', { params: { q: wiQuery } })
        .then(r => setWiOptions(r.data))
        .catch(() => setWiOptions([]))
    }, 400)
    return () => clearTimeout(t)
  }, [wiQuery])

  const loadSessions = useCallback(async () => {
    try {
      const { data } = await axios.get<{ total: number; items: SessionSummary[] }>(
        `/api/find-bugs/sessions/${PROJECT_ID}`, { params: { take: 30 } })
      setSessions(data.items ?? [])
    } catch { /* swallow */ }
  }, [])
  useEffect(() => { loadSessions() }, [loadSessions])

  function stopPolling() {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null }
  }

  const openSession = useCallback(async (id: number) => {
    stopPolling()
    try {
      const { data } = await axios.get<BugSession>(`/api/find-bugs/sessions/detail/${id}`)
      setActive(data)
      setActualText(data.actualBehavior ?? '')
      setCodeText(data.codeContext ?? '')
      if (['Analyzing', 'Drafting', 'Pushing'].includes(data.status)) {
        pollRef.current = setInterval(async () => {
          try {
            const { data: d } = await axios.get<BugSession>(`/api/find-bugs/sessions/detail/${id}`)
            setActive(d)
            if (!['Analyzing', 'Drafting', 'Pushing'].includes(d.status)) {
              stopPolling(); loadSessions()
            }
          } catch { /* swallow */ }
        }, 2500)
      }
    } catch (e) { setActionError(axiosMessage(e)) }
  }, [loadSessions])

  useEffect(() => () => stopPolling(), [])

  async function start() {
    if (!selectedWi) return
    setStarting(true)
    try {
      const { data } = await axios.post<{ sessionId: number }>(
        `/api/find-bugs/${PROJECT_ID}/start`, { workItemId: selectedWi.id })
      await loadSessions()
      await openSession(data.sessionId)
    } catch (e) { setActionError(axiosMessage(e)) }
    finally { setStarting(false) }
  }

  async function runBugWriter() {
    if (!active || !actualText.trim()) return
    try {
      await axios.post(`/api/find-bugs/sessions/${active.id}/run`,
        { actualBehavior: actualText, codeContext: codeText || null })
      openSession(active.id)
    } catch (e) { setActionError(axiosMessage(e)) }
  }

  async function cancel() {
    if (!active) return
    try { await axios.post(`/api/find-bugs/sessions/${active.id}/cancel`) }
    catch (e) { setActionError(axiosMessage(e)) }
  }

  async function approveDraft(id: number) {
    try { await axios.patch(`/api/find-bugs/drafts/${id}`, { status: 'Approved' }); openSession(active!.id) }
    catch (e) { setActionError(axiosMessage(e)) }
  }
  async function rejectDraft(id: number) {
    try { await axios.patch(`/api/find-bugs/drafts/${id}`, { status: 'Rejected' }); openSession(active!.id) }
    catch (e) { setActionError(axiosMessage(e)) }
  }
  async function approveAll() {
    if (!active) return
    try { await axios.post(`/api/find-bugs/sessions/${active.id}/approve-all`); openSession(active.id) }
    catch (e) { setActionError(axiosMessage(e)) }
  }
  async function saveDraft(id: number, patch: Partial<BugDraft>) {
    try { await axios.patch(`/api/find-bugs/drafts/${id}`, patch); openSession(active!.id) }
    catch (e) { setActionError(axiosMessage(e)) }
  }

  async function push() {
    if (!active) return
    setPushing(true)
    try {
      await axios.post(`/api/find-bugs/sessions/${active.id}/push`)
      openSession(active.id)
    } catch (e) { setActionError(axiosMessage(e)) }
    finally { setPushing(false) }
  }

  const approvedCount = active?.drafts.filter(d => d.status === 'Approved').length ?? 0
  const canPush = approvedCount > 0 && active?.status === 'Drafted'

  return (
    <Box>
      <Typography variant="h5" gutterBottom>Find Bugs</Typography>

      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>

        {/* Left: new session + history */}
        <Box sx={{ minWidth: 320, maxWidth: 380, width: '100%' }}>
          <Card variant="outlined" sx={{ mb: 2 }}>
            <CardContent>
              <Typography variant="h6" gutterBottom>New bug session</Typography>
              <Stack spacing={2}>
                <Autocomplete
                  options={wiOptions}
                  filterOptions={x => x}
                  getOptionLabel={o => `#${o.id} — ${o.title}`}
                  isOptionEqualToValue={(a, b) => a.id === b.id}
                  onInputChange={(_, v) => setWiQuery(v)}
                  onChange={(_, v) => setSelectedWi(v)}
                  renderInput={p => <TextField {...p} label="Search work item" size="small" />}
                />
                <Button variant="contained" onClick={start} disabled={!selectedWi || starting}>
                  {starting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Run Analyst'}
                </Button>
                <Alert severity="info" sx={{ fontSize: '0.8rem' }}>
                  Analyst extracts expected behavior. Then paste observed actual behavior to run
                  Bug Writer. Playwright auto-capture lands in v2.
                </Alert>
              </Stack>
            </CardContent>
          </Card>

          <Card variant="outlined">
            <CardContent>
              <Typography variant="subtitle1" gutterBottom>Recent sessions</Typography>
              <List dense>
                {sessions.length === 0 && (
                  <Typography variant="body2" color="text.secondary">No sessions yet.</Typography>
                )}
                {sessions.map(s => (
                  <ListItem key={s.id} disablePadding>
                    <ListItemButton onClick={() => openSession(s.id)} selected={active?.id === s.id}>
                      <Chip size="small" label={s.status}
                        color={statusColors[s.status] ?? 'default'} sx={{ mr: 1 }} />
                      <ListItemText
                        primary={<Box component="span" sx={{ fontSize: '0.875rem', fontWeight: 600 }}>#{s.workItemId}</Box>}
                        secondary={<Box component="span" sx={{ fontSize: '0.75rem', display: 'block', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.workItemTitle}</Box>}
                      />
                    </ListItemButton>
                  </ListItem>
                ))}
              </List>
            </CardContent>
          </Card>
        </Box>

        {/* Right: active session */}
        {active && (
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Card variant="outlined">
              <CardHeader
                title={<Typography variant="h6">#{active.workItemId} — {active.workItemTitle}</Typography>}
                subheader={
                  <Stack direction="row" spacing={1} sx={{ mt: 0.5, alignItems: 'center' }}>
                    <Chip label={active.status} size="small" color={statusColors[active.status] ?? 'default'} />
                  </Stack>
                }
                action={
                  ['Analyzing', 'Drafting', 'Pushing'].includes(active.status) && (
                    <Button color="warning" size="small" startIcon={<StopIcon />} onClick={cancel}>Cancel</Button>
                  )
                }
              />
              <Divider />
              <CardContent>
                {/* Stepper */}
                {(() => {
                  const idx = activeStepIndex(active.status, active.currentActivity)
                  const inFlight = ['Analyzing', 'Drafting', 'Pushing'].includes(active.status)
                  const failed = active.status === 'Failed' || active.status === 'Cancelled'
                  return (
                    <Box sx={{ mb: 2 }}>
                      <Stepper activeStep={idx} alternativeLabel>
                        {PIPELINE_STEPS.map((s, i) => {
                          const completed = i < idx
                          const isActive = i === idx && !failed
                          const running = isActive && inFlight
                          return (
                            <Step key={s.key} completed={completed}>
                              <StepLabel slots={{
                                stepIcon: () => (
                                  <PipelineIcon active={isActive} completed={completed}
                                    running={running} failed={failed && i === idx} icon={s.icon} />
                                ),
                              }}>
                                <Typography variant="body2" sx={{
                                  fontWeight: isActive ? 700 : 500,
                                  color: isActive ? 'primary.main' : completed ? 'success.main' : 'text.secondary',
                                }}>{s.label}</Typography>
                              </StepLabel>
                            </Step>
                          )
                        })}
                      </Stepper>
                      {active.currentActivity && (
                        <Stack direction="row" spacing={1} sx={{ mt: 1.5, alignItems: 'center', justifyContent: 'center' }}>
                          {inFlight && <CircularProgress size={14} />}
                          <Typography variant="body2"
                            color={inFlight ? 'primary.main' : 'text.secondary'}
                            sx={{ fontWeight: inFlight ? 600 : 400 }}>
                            {active.currentActivity}
                          </Typography>
                        </Stack>
                      )}
                    </Box>
                  )
                })()}

                {active.status === 'Failed' && active.errorMessage && (
                  <Alert severity="error" sx={{ mb: 2 }}>{active.errorMessage}</Alert>
                )}
                {active.status === 'Cancelled' && (
                  <Alert severity="warning" sx={{ mb: 2 }}>Session cancelled by user.</Alert>
                )}

                {/* Actual behavior input */}
                {(active.status === 'AwaitingActual' || active.status === 'Drafted'
                  || active.status === 'Failed' || active.status === 'Cancelled') && (
                  <Card variant="outlined" sx={{ mb: 2 }}>
                    <CardContent>
                      <Typography variant="subtitle1" gutterBottom>Observed actual behavior</Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                        What does the app actually do? Be specific — exact UI labels, what shows / doesn't show.
                        Bug Writer compares this against the Analyst's expected behavior.
                      </Typography>
                      <TextField multiline minRows={4} fullWidth value={actualText}
                        onChange={e => setActualText(e.target.value)}
                        placeholder="The SAVE button stays enabled even when the Name field is empty…" />
                      <Typography variant="subtitle2" sx={{ mt: 2 }}>Code context (optional)</Typography>
                      <TextField multiline minRows={3} fullWidth value={codeText}
                        onChange={e => setCodeText(e.target.value)}
                        placeholder="// PipelineForm.tsx — line 42&#10;<Button disabled={!name}>Save</Button>" />
                      <Stack direction="row" sx={{ mt: 1, justifyContent: 'flex-end' }}>
                        <Button variant="contained" startIcon={<BugReportIcon />}
                          onClick={runBugWriter} disabled={!actualText.trim()}>
                          {active.drafts.length > 0 ? 'Re-run Bug Writer' : 'Run Bug Writer'}
                        </Button>
                      </Stack>
                    </CardContent>
                  </Card>
                )}

                {/* Drafts */}
                {active.drafts.length > 0 && (
                  <>
                    <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
                      <Typography variant="subtitle1" sx={{ flex: 1 }}>
                        {active.drafts.length} bug draft(s)
                      </Typography>
                      {active.status === 'Drafted' && (
                        <Button size="small" variant="outlined" onClick={approveAll}>Approve all</Button>
                      )}
                    </Stack>
                    <Stack spacing={1}>
                      {[...active.drafts].sort((a, b) => a.id - b.id).map(d => (
                        <BugDraftCard key={d.id} draft={d}
                          onApprove={() => approveDraft(d.id)}
                          onReject={() => rejectDraft(d.id)}
                          onSave={p => saveDraft(d.id, p)}
                        />
                      ))}
                    </Stack>

                    {/* Push */}
                    {active.status === 'Drafted' && (
                      <Stack direction="row" sx={{ mt: 2, alignItems: 'center', justifyContent: 'flex-end' }}>
                        <Button variant="contained" color="success" startIcon={<PushPinIcon />}
                          onClick={push} disabled={!canPush || pushing}>
                          {pushing ? <CircularProgress size={18} /> : `Push ${approvedCount} bug(s) to ADO`}
                        </Button>
                      </Stack>
                    )}

                    {/* Push results */}
                    <BugPushResults session={active} />
                  </>
                )}
              </CardContent>
            </Card>
          </Box>
        )}
      </Stack>

      <Snackbar open={!!actionError} autoHideDuration={6000} onClose={() => setActionError('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="error" onClose={() => setActionError('')} variant="filled">{actionError}</Alert>
      </Snackbar>
    </Box>
  )
}

// ────────────────────────────────────────────────────────────────────────────
function BugDraftCard({ draft, onApprove, onReject, onSave }: {
  draft: BugDraft
  onApprove: () => void
  onReject: () => void
  onSave: (patch: Partial<BugDraft>) => Promise<void> | void
}) {
  const initialSteps = (): string[] => { try { return JSON.parse(draft.reproStepsJson) } catch { return [] } }
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState(draft.title)
  const [severity, setSeverity] = useState<Severity>(draft.severity)
  const [pre, setPre] = useState(draft.preconditions)
  const [expected, setExpected] = useState(draft.expectedBehavior)
  const [actual, setActual] = useState(draft.actualBehavior)
  const [fix, setFix] = useState(draft.suggestedFix)
  const [steps, setSteps] = useState<string[]>(initialSteps())
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!editing) {
      setTitle(draft.title); setSeverity(draft.severity); setPre(draft.preconditions)
      setExpected(draft.expectedBehavior); setActual(draft.actualBehavior); setFix(draft.suggestedFix)
      setSteps(initialSteps())
    }

  }, [draft.id, draft.title, draft.severity, draft.preconditions, draft.expectedBehavior,
      draft.actualBehavior, draft.suggestedFix, draft.reproStepsJson])

  const isPushed = draft.status === 'Pushed'
  const editable = !isPushed && draft.status !== 'Rejected'

  async function commit() {
    setSaving(true)
    try {
      await onSave({
        title, severity, preconditions: pre,
        expectedBehavior: expected, actualBehavior: actual, suggestedFix: fix,
        reproStepsJson: JSON.stringify(steps),
      })
      setEditing(false)
    } finally { setSaving(false) }
  }
  function cancelEdit() {
    setTitle(draft.title); setSeverity(draft.severity); setPre(draft.preconditions)
    setExpected(draft.expectedBehavior); setActual(draft.actualBehavior); setFix(draft.suggestedFix)
    setSteps(initialSteps())
    setEditing(false)
  }

  return (
    <Card variant="outlined" sx={editing ? { borderColor: 'primary.main' } : undefined}>
      <CardContent sx={{ pb: '12px !important' }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
          <Chip label={draft.status} size="small" color={draftColors[draft.status] ?? 'default'} />
          <Chip label={draft.severity} size="small" color={sevColors[draft.severity] ?? 'default'} />
          {editing ? (
            <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)} />
          ) : (
            <Typography variant="body2" sx={{ flex: 1, fontWeight: 500 }}>{draft.title}</Typography>
          )}
          {!editing && editable && (
            <>
              <Tooltip title="Edit"><IconButton size="small" onClick={() => { setEditing(true); setOpen(true) }}><EditIcon fontSize="small" /></IconButton></Tooltip>
              <Tooltip title="Approve"><IconButton size="small" color="success" onClick={onApprove}><CheckIcon fontSize="small" /></IconButton></Tooltip>
              <Tooltip title="Reject"><IconButton size="small" color="error" onClick={onReject}><CloseIcon fontSize="small" /></IconButton></Tooltip>
            </>
          )}
          {editing && (
            <>
              <Tooltip title="Save"><span>
                <IconButton size="small" color="primary" onClick={commit} disabled={saving}>
                  {saving ? <CircularProgress size={16} /> : <SaveIcon fontSize="small" />}
                </IconButton></span></Tooltip>
              <Tooltip title="Cancel"><IconButton size="small" onClick={cancelEdit} disabled={saving}><CloseIcon fontSize="small" /></IconButton></Tooltip>
            </>
          )}
          <IconButton size="small" onClick={() => setOpen(v => !v)}>
            {open ? <ExpandLessIcon /> : <ExpandMoreIcon />}
          </IconButton>
        </Stack>

        <Collapse in={open}>
          <Stack spacing={1.5} sx={{ mt: 1.5 }}>
            {editing && (
              <FormControl size="small" sx={{ minWidth: 180 }}>
                <InputLabel>Severity</InputLabel>
                <Select label="Severity" value={severity}
                  onChange={e => setSeverity(e.target.value as Severity)}>
                  {(['Critical', 'High', 'Medium', 'Low'] as Severity[]).map(s =>
                    <MenuItem key={s} value={s}>{s}</MenuItem>)}
                </Select>
              </FormControl>
            )}

            <DraftField label="Preconditions" value={pre} editing={editing}
              onChange={setPre} display={draft.preconditions} />
            <Box>
              <Typography variant="caption" color="text.secondary" sx={{ textTransform: 'uppercase' }}>
                Repro steps
              </Typography>
              {editing ? (
                <Stack spacing={0.5}>
                  {steps.map((s, i) => (
                    <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                      <Typography variant="body2" sx={{ mt: 1, minWidth: 24 }}>{i + 1}.</Typography>
                      <TextField fullWidth size="small" multiline value={s}
                        onChange={e => setSteps(curr => curr.map((x, j) => j === i ? e.target.value : x))} />
                      <IconButton size="small" color="error"
                        onClick={() => setSteps(curr => curr.filter((_, j) => j !== i))}>
                        <CloseIcon fontSize="small" /></IconButton>
                    </Stack>
                  ))}
                  <Button size="small" onClick={() => setSteps(curr => [...curr, ''])}>+ add step</Button>
                </Stack>
              ) : (
                <ol style={{ margin: 0, paddingLeft: '1.25rem' }}>
                  {initialSteps().map((s, i) => <li key={i}>{s}</li>)}
                </ol>
              )}
            </Box>
            <DraftField label="Expected" value={expected} editing={editing}
              onChange={setExpected} display={draft.expectedBehavior} />
            <DraftField label="Actual" value={actual} editing={editing}
              onChange={setActual} display={draft.actualBehavior} />
            <DraftField label="Suggested fix" value={fix} editing={editing}
              onChange={setFix} display={draft.suggestedFix} multiline />

            {draft.adoBugId && (
              <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                <Typography variant="caption" color="success.main">
                  ADO Bug #{draft.adoBugId}
                </Typography>
                <IconButton size="small" component="a"
                  href={`https://dev.azure.com/${ADO_ORG}/${encodeURIComponent(ADO_PROJECT)}/_workitems/edit/${draft.adoBugId}`}
                  target="_blank" rel="noreferrer">
                  <OpenInNewIcon fontSize="small" />
                </IconButton>
              </Stack>
            )}
          </Stack>
        </Collapse>
      </CardContent>
    </Card>
  )
}

function DraftField({ label, value, display, editing, onChange, multiline }: {
  label: string; value: string; display: string; editing: boolean
  onChange: (v: string) => void; multiline?: boolean
}) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" sx={{ textTransform: 'uppercase' }}>{label}</Typography>
      {editing ? (
        <TextField fullWidth size="small" multiline={multiline} minRows={multiline ? 2 : 1}
          value={value} onChange={e => onChange(e.target.value)} />
      ) : (
        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{display || <em>—</em>}</Typography>
      )}
    </Box>
  )
}

function BugPushResults({ session }: { session: BugSession }) {
  const pushed = session.drafts.filter(d => d.status === 'Pushed' && d.adoBugId)
  if (pushed.length === 0) return null
  if (!['Done', 'Cancelled', 'Failed'].includes(session.status)) return null

  const url = (id: number) =>
    `https://dev.azure.com/${ADO_ORG}/${encodeURIComponent(ADO_PROJECT)}/_workitems/edit/${id}`

  return (
    <Card variant="outlined" sx={{ mt: 3, borderColor: 'success.main' }}>
      <CardContent>
        <Typography variant="h6" sx={{ mb: 1 }}>
          Pushed {pushed.length} bug(s) — linked to story #{session.workItemId}
        </Typography>
        <Stack spacing={0.5}>
          {pushed.map(d => (
            <Stack key={d.id} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Chip size="small" label={d.severity} color={sevColors[d.severity]} />
              <Typography variant="body2" sx={{ flex: 1 }}>
                <strong>#{d.adoBugId}</strong> — {d.title}
              </Typography>
              <IconButton size="small" component="a" href={url(d.adoBugId!)} target="_blank" rel="noreferrer">
                <OpenInNewIcon fontSize="small" />
              </IconButton>
            </Stack>
          ))}
        </Stack>
      </CardContent>
    </Card>
  )
}
