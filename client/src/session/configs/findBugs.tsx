import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Collapse, Dialog, DialogActions, DialogContent, DialogTitle, FormControl,
  IconButton, InputLabel, Link, MenuItem, Select, Stack, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import EditIcon from '@mui/icons-material/Edit'
import SaveIcon from '@mui/icons-material/Save'
import StopIcon from '@mui/icons-material/Stop'
import PushPinIcon from '@mui/icons-material/PushPin'
import BugReportIcon from '@mui/icons-material/BugReport'
import SearchIcon from '@mui/icons-material/Search'
import PsychologyIcon from '@mui/icons-material/Psychology'
import VisibilityIcon from '@mui/icons-material/Visibility'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import RuleIcon from '@mui/icons-material/Rule'
import { alpha } from '@mui/material/styles'
import AgentPipeline, { type PipelineStep } from '../../components/AgentPipeline'
import AgentConsole from '../../components/AgentConsole'
import AnalysisPanel from '../../components/AnalysisPanel'
import AnalystProgress from '../../components/AnalystProgress'
import StatTile from '../../components/StatTile'
import StatusChip from '../../components/StatusChip'
import { ThinkingShimmer } from '../../components/LoadingSkeleton'
import type {
  AnalyticsColumn, DetailBodyProps, SessionDetail, SessionModuleConfig,
  StartFormProps, WorkItemOption,
} from '../types'
// (SessionSummary is referenced only via api/findBugs now.)
import { parseEventLog } from '../shared'
import { PROJECT_ID } from '../../config/env'
import { searchWorkItems } from '../../api/authorTests'
import {
  listSessionsPage, getSessionDetail, startSession, getLinkedRefs, getTestCases,
  runBugWriter as apiRunBugWriter, cancelSession, patchDraft, approveAllDrafts, pushSession,
  bugFromVerdict, saveAnalysis, sessionStreamUrl,
  type AttachedTestCase, type LinkedChange,
} from '../../api/findBugs'

const ADO_ORG = 'YourOrg'
const ADO_PROJECT = 'Your Project'
const ADO_WI_URL = (id: number) => `https://dev.azure.com/${encodeURIComponent(ADO_ORG)}/${encodeURIComponent(ADO_PROJECT)}/_workitems/edit/${id}`

type Severity = 'Critical' | 'High' | 'Medium' | 'Low'
type DraftStatus = 'Draft' | 'Approved' | 'Rejected' | 'Pushed'
type VerdictStatus = 'pass' | 'fail' | 'inconclusive'
interface VerdictStep { action: string; expected: string; status?: VerdictStatus; actual?: string }
interface CodeVerdict {
  testCase: string; status: VerdictStatus; evidence: string; reasoning: string
  source?: 'plan' | 'draft'; adoTestCaseId?: number
  failedStepIndex?: number | null
  steps?: VerdictStep[]
}
// AttachedTestCase + LinkedChange are imported from api/findBugs.
// A report row = one attached test case, enriched with its verdict if verification has run.
interface ReportRow {
  key?: string  // stable selection id; only attached cases have one (verdict-only rows don't)
  title: string; source?: 'plan' | 'draft'; adoTestCaseId?: number | null
  status: VerdictStatus | 'notrun'
  steps: VerdictStep[]
  evidence?: string; reasoning?: string; failedStepIndex?: number | null
}
const rowKey = (r: { source?: string; title: string }) => `${r.source ?? ''}|${r.title}`
interface BugDraft {
  id: number; title: string; severity: Severity; preconditions: string
  reproStepsJson: string; expectedBehavior: string; actualBehavior: string
  suggestedFix: string; status: DraftStatus; adoBugId: number | null
}

const PIPELINE_STEPS: PipelineStep[] = [
  { key: 'fetch',   label: 'Fetch ticket', icon: <SearchIcon /> },
  { key: 'analyst', label: 'Analyst',      icon: <PsychologyIcon /> },
  { key: 'verify',  label: 'Code Verify',  icon: <VisibilityIcon /> },
  { key: 'bug',     label: 'Bug Writer',   icon: <BugReportIcon /> },
]

function activeStepIndex(s: string, activity: string | null): number {
  const a = (activity ?? '').toLowerCase()
  if (s === 'Done' || s === 'Drafted') return PIPELINE_STEPS.length
  if (s === 'Pushing') return 3 // push fires from the Bug Writer phase — keep that circle lit
  if (s === 'AwaitingActual' || s === 'Verifying') return 2
  if (s === 'Drafting' || a.includes('bug writer')) return 3
  if (a.includes('analyst')) return 1
  if (a.includes('fetch') || a.includes('ticket')) return 0
  return 0
}

function DraftField({ label, value, display, editing, onChange, multiline }: {
  label: string; value: string; display: string; editing: boolean
  onChange: (v: string) => void; multiline?: boolean
}) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" sx={{ textTransform: 'uppercase' }}>{label}</Typography>
      {editing
        ? <TextField fullWidth size="small" multiline={multiline} minRows={multiline ? 2 : 1} value={value} onChange={e => onChange(e.target.value)} />
        : <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{display || <em>—</em>}</Typography>}
    </Box>
  )
}

function BugDraftCard({ draft, onApprove, onReject, onSave }: {
  draft: BugDraft; onApprove: () => void; onReject: () => void
  onSave: (patch: Partial<BugDraft>) => Promise<void> | void
}) {
  const initialSteps = (): string[] => { try { return JSON.parse(draft.reproStepsJson) } catch { return [] } }
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState(draft.title)
  const [severity, setSeverity] = useState<Severity>(draft.severity)
  const [expected, setExpected] = useState(draft.expectedBehavior)
  const [actual, setActual] = useState(draft.actualBehavior)
  const [fix, setFix] = useState(draft.suggestedFix)
  const [steps, setSteps] = useState<string[]>(initialSteps())
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!editing) {
      setTitle(draft.title); setSeverity(draft.severity); setExpected(draft.expectedBehavior)
      setActual(draft.actualBehavior); setFix(draft.suggestedFix); setSteps(initialSteps())
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft.id, draft.title, draft.severity, draft.expectedBehavior, draft.actualBehavior, draft.suggestedFix, draft.reproStepsJson])

  const isPushed = draft.status === 'Pushed'
  const editable = !isPushed && draft.status !== 'Rejected'

  async function commit() {
    setSaving(true)
    try {
      await onSave({ title, severity, expectedBehavior: expected, actualBehavior: actual, suggestedFix: fix, reproStepsJson: JSON.stringify(steps) })
      setEditing(false)
    } finally { setSaving(false) }
  }
  function cancelEdit() {
    setTitle(draft.title); setSeverity(draft.severity); setExpected(draft.expectedBehavior)
    setActual(draft.actualBehavior); setFix(draft.suggestedFix); setSteps(initialSteps()); setEditing(false)
  }

  return (
    <>
      <Card variant="outlined" sx={{ transition: 'border-color .15s, box-shadow .15s', '&:hover': { borderColor: 'primary.main', boxShadow: 1 } }}>
        <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <StatusChip value={draft.status} kind="draft" />
            <StatusChip value={draft.severity} kind="severity" />
            <Typography variant="body2" onClick={() => { setEditing(false); setOpen(true) }}
              sx={{ flex: 1, fontWeight: 500, cursor: 'pointer', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              title={draft.title}>{draft.title}</Typography>
            <Chip size="small" variant="outlined" label={`${initialSteps().length} step${initialSteps().length === 1 ? '' : 's'}`} />
            {draft.adoBugId && <Chip size="small" color="success" variant="outlined" label={`Bug #${draft.adoBugId}`} />}
            {editable && (
              <>
                <Tooltip title="Approve"><IconButton size="small" color="success" onClick={onApprove}><CheckIcon fontSize="small" /></IconButton></Tooltip>
                <Tooltip title="Reject"><IconButton size="small" color="error" onClick={onReject}><CloseIcon fontSize="small" /></IconButton></Tooltip>
              </>
            )}
            <Button size="small" variant="outlined" startIcon={<VisibilityIcon />} onClick={() => { setEditing(false); setOpen(true) }}>View</Button>
          </Stack>
        </CardContent>
      </Card>

      <Dialog open={open} onClose={() => { if (editing) cancelEdit(); setOpen(false) }} maxWidth="md" fullWidth scroll="paper">
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
          <StatusChip value={draft.status} kind="draft" />
          {!editing && <StatusChip value={draft.severity} kind="severity" />}
          <Box sx={{ flex: 1, minWidth: 0 }}>
            {editing
              ? <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)} />
              : <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>{draft.title}</Typography>}
          </Box>
          <IconButton size="small" aria-label="close" onClick={() => { if (editing) cancelEdit(); setOpen(false) }}><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={1.5}>
            {editing && (
              <FormControl size="small" sx={{ minWidth: 180 }}>
                <InputLabel>Severity</InputLabel>
                <Select label="Severity" value={severity} onChange={e => setSeverity(e.target.value as Severity)}>
                  {(['Critical', 'High', 'Medium', 'Low'] as Severity[]).map(s => <MenuItem key={s} value={s}>{s}</MenuItem>)}
                </Select>
              </FormControl>
            )}
            <Box>
              <Typography variant="caption" color="text.secondary" sx={{ textTransform: 'uppercase' }}>Repro steps</Typography>
              {editing ? (
                <Stack spacing={0.5}>
                  {steps.map((s, i) => (
                    <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                      <Typography variant="body2" sx={{ mt: 1, minWidth: 24 }}>{i + 1}.</Typography>
                      <TextField fullWidth size="small" multiline value={s} onChange={e => setSteps(c => c.map((x, j) => j === i ? e.target.value : x))} />
                      <IconButton size="small" color="error" onClick={() => setSteps(c => c.filter((_, j) => j !== i))}><CloseIcon fontSize="small" /></IconButton>
                    </Stack>
                  ))}
                  <Button size="small" onClick={() => setSteps(c => [...c, ''])}>+ add step</Button>
                </Stack>
              ) : (
                <ol style={{ margin: '4px 0 0', paddingLeft: '1.25rem' }}>{initialSteps().map((s, i) => <li key={i}>{s}</li>)}</ol>
              )}
            </Box>
            <DraftField label="Expected" value={expected} editing={editing} onChange={setExpected} display={draft.expectedBehavior} multiline />
            <DraftField label="Actual" value={actual} editing={editing} onChange={setActual} display={draft.actualBehavior} multiline />
            <DraftField label="Suggested fix" value={fix} editing={editing} onChange={setFix} display={draft.suggestedFix} multiline />
            {draft.adoBugId && (
              <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                <Typography variant="caption" color="success.main">ADO Bug #{draft.adoBugId}</Typography>
                <IconButton size="small" component="a" href={`https://dev.azure.com/${ADO_ORG}/${encodeURIComponent(ADO_PROJECT)}/_workitems/edit/${draft.adoBugId}`} target="_blank" rel="noreferrer"><OpenInNewIcon fontSize="small" /></IconButton>
              </Stack>
            )}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 1.5 }}>
          {editing ? (
            <>
              <Button onClick={cancelEdit} disabled={saving}>Cancel</Button>
              <Button variant="contained" disabled={saving} startIcon={saving ? <CircularProgress size={16} /> : <SaveIcon />} onClick={commit}>Save</Button>
            </>
          ) : (
            <>
              {editable && <Button startIcon={<EditIcon />} onClick={() => setEditing(true)}>Edit</Button>}
              {editable && <Button color="error" startIcon={<CloseIcon />} onClick={() => { onReject(); setOpen(false) }}>Reject</Button>}
              {editable && <Button color="success" variant="contained" startIcon={<CheckIcon />} onClick={() => { onApprove(); setOpen(false) }}>Approve</Button>}
              <Button onClick={() => setOpen(false)}>Close</Button>
            </>
          )}
        </DialogActions>
      </Dialog>
    </>
  )
}

const verdictColor = (s: string) => (s === 'pass' ? 'success' : s === 'fail' ? 'error' : 'warning') as 'success' | 'error' | 'warning'

function SourceChip({ source, adoTestCaseId }: { source?: 'plan' | 'draft'; adoTestCaseId?: number | null }) {
  if (source === 'plan') {
    return adoTestCaseId
      ? <Chip size="small" color="info" variant="outlined" clickable component="a"
          href={ADO_WI_URL(adoTestCaseId)} target="_blank" rel="noreferrer"
          icon={<OpenInNewIcon />} label={`Test Plan · #${adoTestCaseId}`} />
      : <Chip size="small" color="info" variant="outlined" label="Test Plan" />
  }
  if (source === 'draft') {
    // A draft that was already pushed to ADO carries its Test Case id — link it.
    return adoTestCaseId
      ? <Chip size="small" variant="outlined" clickable component="a"
          href={ADO_WI_URL(adoTestCaseId)} target="_blank" rel="noreferrer"
          icon={<OpenInNewIcon />} label={`Draft · #${adoTestCaseId}`} />
      : <Chip size="small" variant="outlined" label="Draft (Author Tests)" />
  }
  return null
}

/** Per-step result indicator: green tick (pass), red ✗ (fail), amber dot (inconclusive), — (none). */
function StepStatusCell({ status }: { status?: VerdictStatus }) {
  if (status === 'pass') return <CheckIcon sx={{ fontSize: 18 }} color="success" />
  if (status === 'fail') return <CloseIcon sx={{ fontSize: 18 }} color="error" />
  if (status === 'inconclusive') return <Box sx={{ width: 9, height: 9, borderRadius: '50%', bgcolor: 'warning.main', mx: 'auto' }} />
  return <Typography variant="caption" color="text.disabled">—</Typography>
}

function ReportRowCard({ row, onCreateBug, creating, disabledCreate, selectable, checked, onToggleSelect }: {
  row: ReportRow; onCreateBug: (r: ReportRow) => void; creating: boolean; disabledCreate?: boolean
  selectable?: boolean; checked?: boolean; onToggleSelect?: () => void
}) {
  const [open, setOpen] = useState(false)
  const hasStepStatus = row.steps.some(s => s.status)
  const hasActual = row.steps.some(s => s.actual)
  const fidx = row.failedStepIndex
  const accent = row.status === 'fail' ? 'error.main' : row.status === 'pass' ? 'success.main'
    : row.status === 'inconclusive' ? 'warning.main' : 'divider'
  const canBug = row.status === 'fail' || row.status === 'inconclusive'

  return (
    <Box sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', borderLeft: '4px solid', borderLeftColor: accent }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5, p: 1.25 }}>
        {selectable && row.key && (
          <Tooltip title="Include this test case when you run verification">
            <Checkbox size="small" checked={!!checked} onChange={onToggleSelect} sx={{ p: 0.25 }} />
          </Tooltip>
        )}
        {row.status === 'notrun'
          ? <Chip size="small" variant="outlined" label="Not run" />
          : <Chip size="small" color={verdictColor(row.status)} label={row.status} sx={{ fontWeight: 600, textTransform: 'capitalize' }} />}
        <Typography variant="body2" sx={{ fontWeight: 600, flex: 1, minWidth: 160 }}>{row.title}</Typography>
        {typeof fidx === 'number' && fidx >= 0 && (
          <Chip size="small" color="error" variant="outlined" label={`Fails at step ${fidx + 1}`} />
        )}
        <SourceChip source={row.source} adoTestCaseId={row.adoTestCaseId} />
        {canBug && (
          <Tooltip title={disabledCreate ? 'Wait for the pipeline to finish' : ''}>
            <span>
              <Button size="small" variant="outlined" color="error" disabled={creating || disabledCreate}
                startIcon={creating ? <CircularProgress size={14} /> : <BugReportIcon />}
                onClick={() => onCreateBug(row)}>Create bug</Button>
            </span>
          </Tooltip>
        )}
        {row.steps.length > 0 && (
          <Button size="small" onClick={() => setOpen(o => !o)} sx={{ textTransform: 'none' }}
            startIcon={open ? <ExpandLessIcon /> : <ExpandMoreIcon />}>
            {row.steps.length} step{row.steps.length === 1 ? '' : 's'}
          </Button>
        )}
      </Stack>

      <Collapse in={open} unmountOnExit>
        <Box sx={{ px: 1.25, pb: 1.25 }}>
          {row.evidence && (
            <Box sx={{ mb: 1 }}>
              <Typography variant="caption" sx={{ fontWeight: 600, textTransform: 'uppercase', color: 'text.secondary', display: 'block' }}>What the code actually does</Typography>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{row.evidence}</Typography>
            </Box>
          )}
          {row.reasoning && (
            <Box sx={{ mb: 1 }}>
              <Typography variant="caption" sx={{ fontWeight: 600, textTransform: 'uppercase', color: 'text.secondary', display: 'block' }}>Why this verdict</Typography>
              <Typography variant="body2" color="text.secondary">{row.reasoning}</Typography>
            </Box>
          )}
          {row.steps.length > 0 && (
            <Box sx={{ overflowX: 'auto' }}>
              <Table size="small">
                <TableHead><TableRow>
                  <TableCell sx={{ width: 28 }}>#</TableCell>
                  {hasStepStatus && <TableCell sx={{ width: 52, textAlign: 'center' }}>Result</TableCell>}
                  <TableCell>Action</TableCell>
                  <TableCell>Expected</TableCell>
                  {hasActual && <TableCell>What the code does</TableCell>}
                </TableRow></TableHead>
                <TableBody>
                  {row.steps.map((st, i) => (
                    <TableRow key={i} sx={fidx === i ? { '& td': { bgcolor: theme => alpha(theme.palette.error.main, 0.1) } } : undefined}>
                      <TableCell sx={{ verticalAlign: 'top' }}>{i + 1}</TableCell>
                      {hasStepStatus && <TableCell sx={{ verticalAlign: 'top', textAlign: 'center' }}><StepStatusCell status={st.status} /></TableCell>}
                      <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'pre-wrap', minWidth: 150 }}>{st.action}</TableCell>
                      <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'pre-wrap', minWidth: 150 }}>{st.expected}</TableCell>
                      {hasActual && <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'pre-wrap', minWidth: 150 }}>{st.actual || '—'}</TableCell>}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          )}
        </Box>
      </Collapse>
    </Box>
  )
}

/** The test-execution report: every attached test case (ADO plan + Author-Tests drafts), overlaid
 * with verdicts + per-step results once Code Verify has run. Stats tiles + filter + per-case drill-in
 * with green-tick/red-✗ steps, and a Create-bug action on failing/inconclusive cases. */
function TestExecutionReport({
  session, cases, casesLoaded, selectedKeys, onToggleKey, onSetAll, selectable,
  onCreateBug, creatingKey, inFlight,
}: {
  session: SessionDetail
  cases: AttachedTestCase[]          // owned by the parent so selection survives phase navigation
  casesLoaded: boolean
  selectedKeys: Set<string>          // case keys chosen to run
  onToggleKey: (key: string) => void
  onSetAll: (on: boolean) => void
  selectable?: boolean               // show the per-case checkboxes (only when a run is possible)
  onCreateBug: (r: ReportRow) => void
  creatingKey: string | null
  inFlight?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [filter, setFilter] = useState<'all' | VerdictStatus | 'notrun'>('all')

  const verdicts = useMemo<CodeVerdict[]>(() => {
    if (!session.codeVerificationJson) return []
    try { const v = JSON.parse(session.codeVerificationJson); return Array.isArray(v) ? v : [] } catch { return [] }
  }, [session.codeVerificationJson])

  const rows = useMemo<ReportRow[]>(() => {
    const norm = (s?: string) => (s ?? '').trim().toLowerCase()
    const byKey = new Map<string, CodeVerdict>()
    const byTitle = new Map<string, CodeVerdict>()
    verdicts.forEach(v => { byKey.set(`${v.source ?? ''}|${norm(v.testCase)}`, v); byTitle.set(norm(v.testCase), v) })
    const used = new Set<CodeVerdict>()
    const out: ReportRow[] = []
    for (const c of cases) {
      const v = byKey.get(`${c.source}|${norm(c.title)}`) ?? byTitle.get(norm(c.title))
      if (v) used.add(v)
      out.push({
        key: c.key, title: c.title, source: c.source, adoTestCaseId: c.adoTestCaseId,
        status: v ? v.status : 'notrun',
        steps: (v?.steps && v.steps.length) ? v.steps : c.steps.map(s => ({ action: s.action, expected: s.expected })),
        evidence: v?.evidence, reasoning: v?.reasoning, failedStepIndex: v?.failedStepIndex,
      })
    }
    // Verdicts with no matching attached case (e.g. a "Manual: …" assessment).
    for (const v of verdicts) {
      if (used.has(v)) continue
      out.push({
        title: v.testCase, source: v.source, adoTestCaseId: v.adoTestCaseId, status: v.status,
        steps: v.steps ?? [], evidence: v.evidence, reasoning: v.reasoning, failedStepIndex: v.failedStepIndex,
      })
    }
    return out
  }, [cases, verdicts])

  // Selection is owned by the parent (so it survives phase navigation); derive counts here.
  const allKeys = cases.map(c => c.key)
  const selectedCount = cases.reduce((acc, c) => acc + (selectedKeys.has(c.key) ? 1 : 0), 0)

  // Still loading the attached list — avoid flashing 0s before the fetch resolves. Once loaded,
  // the section renders normally with 0-valued tiles even when nothing is linked to the story.
  if (rows.length === 0 && !casesLoaded) return null

  const n = (st: ReportRow['status']) => rows.filter(r => r.status === st).length
  const planCount = rows.filter(r => r.source === 'plan').length
  const draftCount = rows.filter(r => r.source === 'draft').length
  const order: Record<string, number> = { fail: 0, inconclusive: 1, notrun: 2, pass: 3 }
  const shown = [...rows]
    .sort((a, b) => (order[a.status] ?? 4) - (order[b.status] ?? 4))
    .filter(r => filter === 'all' || r.status === filter)

  const tiles = [
    { key: 'all' as const, label: 'Total', value: rows.length, color: 'primary.main' },
    { key: 'pass' as const, label: 'Pass', value: n('pass'), color: 'success.main' },
    { key: 'fail' as const, label: 'Fail', value: n('fail'), color: 'error.main' },
    { key: 'inconclusive' as const, label: 'Inconclusive', value: n('inconclusive'), color: 'warning.main' },
    { key: 'notrun' as const, label: 'Not run', value: n('notrun'), color: 'text.secondary' },
  ]
  const openWith = (k: typeof filter) => { setFilter(k); setOpen(true) }
  const fchip = (key: typeof filter, label: string, color: 'default' | 'success' | 'error' | 'warning') => (
    <Chip size="small" clickable color={color} variant={filter === key ? 'filled' : 'outlined'} label={label} onClick={() => setFilter(key)} />
  )

  // Dialog list is grouped by where the case lives. Manual/unsourced verdicts fall into "Other".
  const GROUPS: { key: 'plan' | 'draft'; label: string }[] = [
    { key: 'plan', label: 'In ADO (Test Plan)' },
    { key: 'draft', label: 'Drafts (Author Tests)' },
  ]
  const groupRows = (k: 'plan' | 'draft') => shown.filter(r => r.source === k)
  const otherRows = shown.filter(r => r.source !== 'plan' && r.source !== 'draft')
  const renderRows = (rs: ReportRow[]) => rs.map((r, i) => (
    <ReportRowCard key={`${r.source ?? ''}-${r.title}-${i}`} row={r}
      onCreateBug={onCreateBug} creating={creatingKey === rowKey(r)} disabledCreate={inFlight}
      selectable={selectable} checked={!!r.key && selectedKeys.has(r.key)}
      onToggleSelect={() => r.key && onToggleKey(r.key)} />
  ))

  return (
    <Box sx={{ mb: 2 }}>
      <Stack direction="row" sx={{ alignItems: 'center', mb: 0.5 }}>
        <Typography variant="subtitle1" sx={{ flex: 1 }}>Test execution report</Typography>
        <Button size="small" variant="outlined" startIcon={<VisibilityIcon />} onClick={() => setOpen(true)}>View report</Button>
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
        {planCount} in ADO Test Plan · {draftCount} draft{draftCount === 1 ? '' : 's'}
        {selectable && allKeys.length > 0 ? ` · ${selectedCount} of ${allKeys.length} selected to run` : ''}
      </Typography>
      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(104px, 1fr))', gap: 1.5 }}>
        {tiles.map(t => (
          <Box key={t.key} role="button" tabIndex={0} onClick={() => openWith(t.key)}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openWith(t.key) } }}
            sx={{ cursor: 'pointer', borderRadius: 3, transition: 'transform .12s', '&:hover': { transform: 'translateY(-2px)' }, '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}>
            <StatTile icon={<RuleIcon />} label={t.label} value={t.value} color={t.color} />
          </Box>
        ))}
      </Box>

      <Dialog open={open} onClose={() => setOpen(false)} maxWidth="md" fullWidth scroll="paper">
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <Typography variant="h6" component="span" sx={{ fontWeight: 600, flex: 1 }}>Test execution report</Typography>
          <IconButton size="small" onClick={() => setOpen(false)} aria-label="close"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Stack direction="row" spacing={0.75} sx={{ mb: 1.5, flexWrap: 'wrap', gap: 0.5 }}>
            {fchip('all', `All ${rows.length}`, 'default')}
            {fchip('fail', `${n('fail')} fail`, 'error')}
            {fchip('inconclusive', `${n('inconclusive')} inconclusive`, 'warning')}
            {fchip('pass', `${n('pass')} pass`, 'success')}
            {n('notrun') > 0 && fchip('notrun', `${n('notrun')} not run`, 'default')}
          </Stack>
          {selectable && allKeys.length > 0 && (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.5 }}>
              <Checkbox size="small" sx={{ p: 0.25 }}
                checked={selectedCount === allKeys.length}
                indeterminate={selectedCount > 0 && selectedCount < allKeys.length}
                onChange={e => onSetAll(e.target.checked)} />
              <Typography variant="body2">{selectedCount} of {allKeys.length} selected to run</Typography>
              <Box sx={{ flex: 1 }} />
              <Button size="small" onClick={() => onSetAll(false)} disabled={selectedCount === 0}>Clear</Button>
            </Stack>
          )}
          <Stack spacing={2}>
            {GROUPS.map(g => {
              const rs = groupRows(g.key)
              if (rs.length === 0) return null
              return (
                <Box key={g.key}>
                  <Typography variant="overline" color="text.secondary">{g.label} ({rs.length})</Typography>
                  <Stack spacing={1} sx={{ mt: 0.5 }}>{renderRows(rs)}</Stack>
                </Box>
              )
            })}
            {otherRows.length > 0 && (
              <Box>
                <Typography variant="overline" color="text.secondary">Other ({otherRows.length})</Typography>
                <Stack spacing={1} sx={{ mt: 0.5 }}>{renderRows(otherRows)}</Stack>
              </Box>
            )}
            {shown.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>No matching cases.</Typography>
            )}
          </Stack>
        </DialogContent>
        <DialogActions><Button onClick={() => setOpen(false)}>Close</Button></DialogActions>
      </Dialog>
    </Box>
  )
}

function BugPushResults({ session }: { session: SessionDetail }) {
  const drafts = session.drafts as unknown as BugDraft[]
  const pushed = drafts.filter(d => d.status === 'Pushed' && d.adoBugId)
  if (pushed.length === 0) return null
  if (!['Done', 'Cancelled', 'Failed'].includes(session.status)) return null
  const url = (id: number) => `https://dev.azure.com/${ADO_ORG}/${encodeURIComponent(ADO_PROJECT)}/_workitems/edit/${id}`
  return (
    <Card sx={{ mt: 3, borderColor: 'success.main' }}>
      <CardContent>
        <Typography variant="h6" sx={{ mb: 1 }}>Pushed {pushed.length} bug(s) — linked to story #{session.workItemId}</Typography>
        <Stack spacing={0.5}>
          {pushed.map(d => (
            <Stack key={d.id} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <StatusChip value={d.severity} kind="severity" />
              <Typography variant="body2" sx={{ flex: 1 }}><strong>#{d.adoBugId}</strong> — {d.title}</Typography>
              <IconButton size="small" component="a" href={url(d.adoBugId!)} target="_blank" rel="noreferrer"><OpenInNewIcon fontSize="small" /></IconButton>
            </Stack>
          ))}
        </Stack>
      </CardContent>
    </Card>
  )
}

function BugDetailBody({ session, refetch, onError }: DetailBodyProps) {
  const drafts = session.drafts as unknown as BugDraft[]
  const [actualText, setActualText] = useState(session.actualBehavior ?? '')
  const [prIdsText, setPrIdsText] = useState('')
  const [commitIdsText, setCommitIdsText] = useState('')
  const [linkedRefs, setLinkedRefs] = useState<{ pullRequestIds: string[]; commitShas: string[]; changes: LinkedChange[] }>({ pullRequestIds: [], commitShas: [], changes: [] })
  const [pushing, setPushing] = useState(false)
  const [showOverride, setShowOverride] = useState(false)
  const [bugsOpen, setBugsOpen] = useState(false)
  const [sevFilter, setSevFilter] = useState<Severity | 'all'>('all')
  const [creatingKey, setCreatingKey] = useState<string | null>(null)
  const [picked, setPicked] = useState<number | null>(null)
  // Attached test cases + which to run — owned here (not in the report) so the selection survives
  // navigating between pipeline phases / the Findings⇄Activity toggle (which unmount the report).
  const [cases, setCases] = useState<AttachedTestCase[]>([])
  const [casesLoaded, setCasesLoaded] = useState(false)
  const [selectedKeys, setSelectedKeys] = useState<Set<string>>(new Set())

  useEffect(() => { setActualText(session.actualBehavior ?? '') }, [session.actualBehavior])
  useEffect(() => {
    let cancelled = false
    getLinkedRefs(session.id)
      .then(refs => { if (!cancelled) setLinkedRefs(refs) }).catch(() => {})
    return () => { cancelled = true }
  }, [session.id])
  useEffect(() => {
    let cancelled = false
    setCasesLoaded(false)
    getTestCases(session.id)
      .then(cs => { if (!cancelled) { setCases(cs); setSelectedKeys(new Set(cs.map(c => c.key))) } })
      .catch(() => {})
      .finally(() => { if (!cancelled) setCasesLoaded(true) })
    return () => { cancelled = true }
  }, [session.id])

  const inFlight = ['Analyzing', 'Verifying', 'Drafting', 'Pushing'].includes(session.status)
  const approvedCount = drafts.filter(d => d.status === 'Approved').length
  const canPush = approvedCount > 0 && session.status === 'Drafted'

  const toggleCaseKey = (k: string) => setSelectedKeys(prev => {
    const next = new Set(prev); if (next.has(k)) next.delete(k); else next.add(k); return next
  })
  const setAllCases = (on: boolean) => setSelectedKeys(on ? new Set(cases.map(c => c.key)) : new Set())
  const selectedCaseKeys = cases.filter(c => selectedKeys.has(c.key)).map(c => c.key)
  const caseTotal = cases.length

  const splitIds = (t: string) => t.split(/[\s,]+/).map(x => x.trim()).filter(Boolean)
  const runBugWriter = async () => {
    try {
      // Code verification is always on (anchored on the ticket's change). actualBehavior is optional.
      await apiRunBugWriter(session.id, { actualBehavior: actualText, verifyCode: true, prIds: splitIds(prIdsText), commitIds: splitIds(commitIdsText), selectedTestCases: selectedCaseKeys })
      refetch()
    } catch (e) { onError(e, 'Run Bug Writer failed') }
  }
  const cancel = async () => { try { await cancelSession(session.id); refetch() } catch (e) { onError(e, 'Cancel failed') } }
  const approveDraft = async (id: number) => { try { await patchDraft(id, { status: 'Approved' }); refetch() } catch (e) { onError(e, 'Approve failed') } }
  const rejectDraft = async (id: number) => { try { await patchDraft(id, { status: 'Rejected' }); refetch() } catch (e) { onError(e, 'Reject failed') } }
  const approveAll = async () => { try { await approveAllDrafts(session.id); refetch() } catch (e) { onError(e, 'Approve all failed') } }
  const saveDraft = async (id: number, patch: Partial<BugDraft>) => { try { await patchDraft(id, patch); refetch() } catch (e) { onError(e, 'Save failed') } }
  const push = async () => { setPushing(true); try { await pushSession(session.id); refetch() } catch (e) { onError(e, 'Push failed') } finally { setPushing(false) } }
  const createBugFromVerdict = async (r: ReportRow) => {
    setCreatingKey(rowKey(r))
    try {
      await bugFromVerdict(session.id, {
        testCase: r.title, source: r.source, adoTestCaseId: r.adoTestCaseId ?? undefined,
        evidence: r.evidence ?? '', reasoning: r.reasoning ?? '',
        failedStepIndex: r.failedStepIndex ?? undefined,
        steps: r.steps.map(s => ({ action: s.action, expected: s.expected, status: s.status, actual: s.actual })),
      })
      setPicked(3) // jump to the Bug Writer phase so the new draft is visible
      refetch()
    } catch (e) { onError(e, 'Create bug failed') } finally { setCreatingKey(null) }
  }

  const sevCount = (s: Severity) => drafts.filter(d => d.severity === s).length
  const tiles = [
    { key: 'all' as const, label: 'Total', value: drafts.length, color: 'primary.main' },
    { key: 'Critical' as const, label: 'Critical', value: sevCount('Critical'), color: 'error.main' },
    { key: 'High' as const, label: 'High', value: sevCount('High'), color: 'warning.main' },
    { key: 'Medium' as const, label: 'Medium', value: sevCount('Medium'), color: 'info.main' },
    { key: 'Low' as const, label: 'Low', value: sevCount('Low'), color: 'text.secondary' },
  ]
  const visible = [...drafts].filter(d => sevFilter === 'all' || d.severity === sevFilter).sort((a, b) => a.id - b.id)
  const openTile = (k: Severity | 'all') => { setSevFilter(k); setBugsOpen(true) }
  const showActualInput = ['AwaitingActual', 'Drafted', 'Failed', 'Cancelled'].includes(session.status)

  // ── Phase navigation: clicking a pipeline circle selects which phase's findings to show.
  const persisted = useMemo(() => parseEventLog(session.eventLogJson), [session.eventLogJson])
  const fetchEv = persisted.find(e => e.phase === 'fetch')
  const wiType = (fetchEv?.data as Record<string, unknown> | null | undefined)?.type as string | undefined
  // On a terminal failure/cancel the backend overwrites currentActivity with a generic
  // "Failed: …" / "Cancelled by user" string, so the phase can't be recovered from it. Infer the
  // furthest phase reached from durable session data so the failed circle + default panel land
  // where work actually stopped, instead of always snapping back to Fetch.
  const terminalFailed = session.status === 'Failed' || session.status === 'Cancelled'
  const reachedIdx = drafts.length > 0 ? 3 : session.codeVerificationJson ? 2 : session.analysisJson ? 1 : 0
  const activeIdx = terminalFailed ? reachedIdx : activeStepIndex(session.status, session.currentActivity)
  const sel = Math.min(picked ?? activeIdx, PIPELINE_STEPS.length - 1)
  const [view, setView] = useState<'findings' | 'activity'>('findings')
  const streamUrl = sessionStreamUrl(session.id)
  const phaseLabel = PIPELINE_STEPS[sel]?.label ?? ''

  return (
    <Box>
      <AgentPipeline steps={PIPELINE_STEPS} activeIndex={activeIdx}
        inFlight={['Analyzing', 'Verifying', 'Drafting'].includes(session.status)}
        failed={session.status === 'Failed' || session.status === 'Cancelled'}
        currentActivity={session.status === 'AwaitingActual' ? null : session.currentActivity}
        selectedIndex={sel} onSelect={setPicked} />

      {inFlight && (
        <Stack direction="row" sx={{ mb: 1, justifyContent: 'flex-end' }}>
          <Button color="warning" size="small" startIcon={<StopIcon />} onClick={cancel}>Cancel</Button>
        </Stack>
      )}

      {session.status === 'Failed' && <Alert severity="error" sx={{ mb: 2 }}>{session.errorMessage || session.currentActivity || 'Session failed.'}</Alert>}
      {session.status === 'Cancelled' && <Alert severity="warning" sx={{ mb: 2 }}>Session cancelled by user.</Alert>}

      {/* Findings (selected phase) vs Activity (verbose live console) */}
      <Stack direction="row" sx={{ mb: 1.5, alignItems: 'center' }}>
        <Typography variant="overline" color="text.secondary" sx={{ flex: 1 }}>{phaseLabel}</Typography>
        <ToggleButtonGroup size="small" exclusive value={view} onChange={(_e, v) => { if (v) setView(v) }}>
          <ToggleButton value="findings" sx={{ textTransform: 'none', py: 0.25 }}>Findings</ToggleButton>
          <ToggleButton value="activity" sx={{ textTransform: 'none', py: 0.25 }}>Activity</ToggleButton>
        </ToggleButtonGroup>
      </Stack>

      {view === 'activity' && (
        <AgentConsole title="Activity log" streamUrl={inFlight ? streamUrl : null} seedEvents={persisted} />
      )}

      {/* Fetch ticket */}
      {view === 'findings' && sel === 0 && (
        <Card variant="outlined"><CardContent>
          <Typography variant="subtitle2" gutterBottom>Ticket</Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
            <Chip size="small" label={`#${session.workItemId}`} />
            {wiType && <Chip size="small" variant="outlined" label={wiType} />}
            <Typography variant="body2" sx={{ flex: 1, minWidth: 0 }}>{session.workItemTitle}</Typography>
            <Link href={ADO_WI_URL(session.workItemId)} target="_blank" rel="noreferrer" variant="body2">Open in Azure DevOps</Link>
          </Stack>
        </CardContent></Card>
      )}

      {/* Analyst */}
      {view === 'findings' && sel === 1 && (
        <>
          {session.status === 'Analyzing' && !session.analysisJson && <AnalystProgress streamUrl={streamUrl} />}
          <AnalysisPanel analysisJson={session.analysisJson}
            onSave={async (a) => { await saveAnalysis(session.id, a); refetch() }} />
          {session.status !== 'Analyzing' && !session.analysisJson && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>No analysis available.</Typography>
          )}
        </>
      )}

      {/* Code Verify */}
      {view === 'findings' && sel === 2 && (
        <>
          {showActualInput && (
            <Card sx={{ mb: 2 }}>
              <CardContent>
                <Typography variant="subtitle1">Verify against code &amp; draft bugs</Typography>

                {linkedRefs.changes.length > 0 ? (
                  <Stack spacing={1.25} sx={{ mt: 1.5 }}>
                    {([
                      { kind: 'pr' as const, heading: 'Pull requests' },
                      { kind: 'commit' as const, heading: 'Commits' },
                    ]).map(group => {
                      const items = linkedRefs.changes.filter(c => c.kind === group.kind)
                      if (items.length === 0) return null
                      return (
                        <Stack key={group.kind} spacing={0.5}>
                          <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.4 }}>
                            {group.heading} ({items.length})
                          </Typography>
                          {items.map(c => (
                            <Stack key={`${c.kind}-${c.id}`} direction="row" spacing={1} sx={{ alignItems: 'center', minWidth: 0 }}>
                              <Chip
                                size="small"
                                color={c.kind === 'pr' ? 'primary' : 'default'}
                                variant="outlined"
                                label={c.kind === 'pr' ? `PR #${c.id}` : c.id.slice(0, 8)}
                                sx={{ flexShrink: 0, fontFamily: c.kind === 'commit' ? 'monospace' : undefined }}
                              />
                              <Tooltip title={c.title}>
                                <Link href={c.url} target="_blank" rel="noreferrer" variant="body2"
                                  sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                                  {c.title}
                                </Link>
                              </Tooltip>
                              <OpenInNewIcon fontSize="small" color="action" sx={{ flexShrink: 0 }} />
                            </Stack>
                          ))}
                        </Stack>
                      )
                    })}
                  </Stack>
                ) : (
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
                    No linked PRs/commits — Bug Writer will anchor on the closest matching code.
                  </Typography>
                )}

                <Box sx={{ mt: 1.5 }}>
                  <Button size="small" color="inherit" sx={{ px: 0.5, color: 'text.secondary' }}
                    endIcon={showOverride ? <ExpandLessIcon /> : <ExpandMoreIcon />}
                    onClick={() => setShowOverride(v => !v)}>
                    Add PRs / commits manually
                  </Button>
                  <Collapse in={showOverride}>
                    <Stack spacing={1.5} sx={{ mt: 1 }}>
                      <TextField size="small" fullWidth label="PR ids (comma-separated)" value={prIdsText} onChange={e => setPrIdsText(e.target.value)} />
                      <TextField size="small" fullWidth label="Commit SHAs (comma-separated)" value={commitIdsText} onChange={e => setCommitIdsText(e.target.value)} />
                    </Stack>
                  </Collapse>
                </Box>

                <TextField multiline minRows={3} fullWidth sx={{ mt: 2 }} value={actualText} onChange={e => setActualText(e.target.value)}
                  label="Observed actual behavior (optional)"
                  placeholder="The SAVE button stays enabled even when the Name field is empty…" />

                <Stack direction="row" spacing={1} sx={{ mt: 1.5, alignItems: 'center', justifyContent: 'flex-end' }}>
                  {caseTotal > 0 && (
                    <Typography variant="caption" color="text.secondary">
                      {selectedCaseKeys.length} of {caseTotal} test case(s) selected
                    </Typography>
                  )}
                  <Button variant="contained" startIcon={<BugReportIcon />} onClick={runBugWriter}
                    disabled={caseTotal > 0 && selectedCaseKeys.length === 0}>
                    {caseTotal === 0
                      ? (drafts.length > 0 ? 'Re-run verification + Bug Writer' : 'Verify against code + draft bugs')
                      : selectedCaseKeys.length === 0 ? 'Select test cases to verify'
                      : selectedCaseKeys.length === caseTotal ? `Verify all ${caseTotal} + draft bugs`
                      : `Verify ${selectedCaseKeys.length} of ${caseTotal} + draft bugs`}
                  </Button>
                </Stack>
              </CardContent>
            </Card>
          )}

          <TestExecutionReport session={session} onCreateBug={createBugFromVerdict} creatingKey={creatingKey}
            inFlight={inFlight} selectable={showActualInput}
            cases={cases} casesLoaded={casesLoaded} selectedKeys={selectedKeys}
            onToggleKey={toggleCaseKey} onSetAll={setAllCases} />

          {session.status === 'Analyzing' && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
              Code verification runs after the Analyst finishes.
            </Typography>
          )}
        </>
      )}

      {/* Bug Writer */}
      {view === 'findings' && sel === 3 && (
        <>
          {session.status === 'Drafting' && drafts.length === 0 && <ThinkingShimmer label="Bug Writer is drafting bugs…" />}
          {drafts.length === 0 && session.status !== 'Drafting' && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
              {session.status === 'AwaitingActual'
                ? 'Bug Writer has not run yet — open the Code Verify circle to run it.'
                : 'No bugs drafted.'}
            </Typography>
          )}
          {drafts.length > 0 && (
        <>
          <Typography variant="subtitle2" color="text.secondary" sx={{ mb: 1 }}>Bugs generated — click a tile to review</Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(116px, 1fr))', gap: 1.5 }}>
            {tiles.map(t => (
              <Box key={t.key} role="button" tabIndex={0} onClick={() => openTile(t.key)}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openTile(t.key) } }}
                sx={{ cursor: 'pointer', borderRadius: 3, transition: 'transform .12s', '&:hover': { transform: 'translateY(-2px)' }, '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}>
                <StatTile icon={<BugReportIcon />} label={t.label} value={t.value} color={t.color} />
              </Box>
            ))}
          </Box>

          <BugPushResults session={session} />

          <Dialog open={bugsOpen} onClose={() => setBugsOpen(false)} maxWidth="md" fullWidth scroll="paper">
            <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
              <Typography variant="h6" component="span" sx={{ fontWeight: 600 }}>{sevFilter === 'all' ? 'Generated bugs' : `${sevFilter} bugs`}</Typography>
              <Chip size="small" label={visible.length} />
              <Box sx={{ flex: 1 }} />
              {sevFilter !== 'all' && <Button size="small" onClick={() => setSevFilter('all')}>Show all</Button>}
              {session.status === 'Drafted' && <Button size="small" variant="outlined" onClick={approveAll}>Approve all</Button>}
              <IconButton size="small" aria-label="close" onClick={() => setBugsOpen(false)}><CloseIcon /></IconButton>
            </DialogTitle>
            <DialogContent dividers>
              <Stack spacing={1}>
                {visible.map(d => (
                  <BugDraftCard key={d.id} draft={d} onApprove={() => approveDraft(d.id)} onReject={() => rejectDraft(d.id)} onSave={p => saveDraft(d.id, p)} />
                ))}
                {visible.length === 0 && <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>No {sevFilter} bugs.</Typography>}
              </Stack>
            </DialogContent>
            <DialogActions sx={{ px: 3, py: 1.5 }}>
              <Button onClick={() => setBugsOpen(false)}>Close</Button>
              {session.status === 'Drafted' && (
                <Button variant="contained" color="success" disabled={!canPush || pushing}
                  startIcon={pushing ? <CircularProgress size={16} /> : <PushPinIcon />} onClick={push}>{`Push ${approvedCount} bug(s) to ADO`}</Button>
              )}
            </DialogActions>
          </Dialog>
        </>
          )}
        </>
      )}
    </Box>
  )
}

function BugStartForm({ onStarted, onError, onClose }: StartFormProps) {
  const [wiQuery, setWiQuery] = useState('')
  const [wiOptions, setWiOptions] = useState<WorkItemOption[]>([])
  const [selectedWi, setSelectedWi] = useState<WorkItemOption | null>(null)
  const [starting, setStarting] = useState(false)

  useEffect(() => {
    if (wiQuery.length < 2) { setWiOptions([]); return }
    const t = setTimeout(() => {
      searchWorkItems(wiQuery).then(setWiOptions).catch(() => setWiOptions([]))
    }, 400)
    return () => clearTimeout(t)
  }, [wiQuery])

  async function start() {
    if (!selectedWi) return
    setStarting(true)
    try {
      const { sessionId } = await startSession(PROJECT_ID, { workItemId: selectedWi.id })
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
      <Typography variant="caption" color="text.secondary">
        Analyst runs first; you'll paste the observed actual behavior and run Bug Writer from the session detail.
      </Typography>
      <Button variant="contained" onClick={start} disabled={starting || !selectedWi}>
        {starting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Run Analyst'}
      </Button>
    </Stack>
  )
}

const analyticsColumns: AnalyticsColumn[] = [
  { key: 'total', label: 'Total', align: 'right', value: d => d ? d.drafts.length : '—' },
  { key: 'critical', label: 'Critical', align: 'right', value: d => d ? (d.drafts as unknown as BugDraft[]).filter(x => x.severity === 'Critical').length : '—' },
  { key: 'high', label: 'High', align: 'right', value: d => d ? (d.drafts as unknown as BugDraft[]).filter(x => x.severity === 'High').length : '—' },
  { key: 'medium', label: 'Medium', align: 'right', value: d => d ? (d.drafts as unknown as BugDraft[]).filter(x => x.severity === 'Medium').length : '—' },
  { key: 'low', label: 'Low', align: 'right', value: d => d ? (d.drafts as unknown as BugDraft[]).filter(x => x.severity === 'Low').length : '—' },
  { key: 'pushed', label: 'Pushed', align: 'right', value: d => d ? d.drafts.filter(x => x.status === 'Pushed').length : '—' },
]

export const findBugsConfig: SessionModuleConfig = {
  key: 'find-bugs',
  apiBase: '/api/find-bugs',
  route: '/find-bugs',
  labels: {
    title: 'Find Bugs',
    subtitle: 'Analyst extracts expected behavior; paste the observed actual behavior and Bug Writer drafts bug work items for the gaps.',
    newCta: 'New bug hunt',
  },
  moduleChip: { label: 'BUGS', icon: <BugReportIcon fontSize="small" /> },
  inFlightStatuses: ['Analyzing', 'Verifying', 'Drafting', 'Pushing'],
  pipelineSteps: PIPELINE_STEPS,
  activeStepIndex,
  analyticsColumns,
  listSessions: (take, skip) => listSessionsPage(PROJECT_ID, take, skip),
  getDetail: (id) => getSessionDetail(id),
  streamUrl: sessionStreamUrl,
  StartForm: BugStartForm,
  DetailBody: BugDetailBody,
}
