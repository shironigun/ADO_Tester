import { useEffect, useState } from 'react'
import {
  Alert, Box, Button, Card, CardContent, Checkbox, Chip, CircularProgress,
  Dialog, DialogActions, DialogContent, DialogTitle, Divider, FormControlLabel,
  IconButton, Stack, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, Tooltip, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import RuleIcon from '@mui/icons-material/Rule'
import SaveIcon from '@mui/icons-material/Save'
import VisibilityIcon from '@mui/icons-material/Visibility'
import StatTile from '../components/StatTile'
import StatusChip from '../components/StatusChip'
import type { ApiTestDraftDto } from '../api/qaLoop'
import { patchDraft, refineUiCases } from '../api/qaLoop'

type Step = { action: string; expected: string }
type DraftPatch = { title: string; preconditions?: string; steps: Step[] }

const BUCKET_COLORS: Record<string, 'success' | 'warning' | 'error' | 'info' | 'default'> = {
  smoke: 'success', regression: 'warning', e2e: 'info',
}

function parseSteps(stepsJson: string | null | undefined): Step[] {
  try {
    const v = JSON.parse(stepsJson || '[]')
    if (!Array.isArray(v)) return []
    return v.map((s: Record<string, unknown>) => ({
      action: String(s.action ?? s.Action ?? ''),
      expected: String(s.expected ?? s.Expected ?? ''),
    }))
  } catch { return [] }
}

/** Independent copy of Author Tests' TestDraftCard — QA Loop UI cases only. */
function UiCaseDraftCard({
  draft, onApprove, onReject, onSave,
  feedback, onFeedbackChange, markedRemove, onToggleRemove,
}: {
  draft: ApiTestDraftDto
  onApprove: () => void
  onReject: () => void
  onSave: (patch: DraftPatch) => Promise<void> | void
  feedback?: string
  onFeedbackChange?: (v: string) => void
  markedRemove?: boolean
  onToggleRemove?: (v: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState(false)
  const [saving, setSaving] = useState(false)
  const initialSteps = () => parseSteps(draft.stepsJson)
  const [title, setTitle] = useState(draft.title)
  const [preconditions, setPreconditions] = useState(draft.preconditions ?? '')
  const [steps, setSteps] = useState<Step[]>(initialSteps())

  useEffect(() => {
    if (!editing) {
      setTitle(draft.title)
      setPreconditions(draft.preconditions ?? '')
      setSteps(initialSteps())
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft.id, draft.title, draft.preconditions, draft.stepsJson])

  const isEditable = draft.status !== 'Rejected' && draft.status !== 'Pushed'

  const updateStep = (i: number, f: 'action' | 'expected', v: string) =>
    setSteps(c => c.map((s, idx) => (idx === i ? { ...s, [f]: v } : s)))
  const addStep = () => setSteps(c => [...c, { action: '', expected: '' }])
  const removeStep = (i: number) => setSteps(c => c.filter((_, idx) => idx !== i))
  const moveStep = (i: number, dir: -1 | 1) => setSteps(c => {
    const j = i + dir
    if (j < 0 || j >= c.length) return c
    const n = [...c]; [n[i], n[j]] = [n[j], n[i]]; return n
  })

  async function commitEdits() {
    setSaving(true)
    try { await onSave({ title, preconditions, steps }); setEditing(false) }
    finally { setSaving(false) }
  }
  function cancelEdits() {
    setTitle(draft.title)
    setPreconditions(draft.preconditions ?? '')
    setSteps(initialSteps())
    setEditing(false)
  }
  const openView = () => { setEditing(false); setOpen(true) }
  const openEdit = () => { setEditing(true); setOpen(true) }
  const closeDialog = () => { if (editing) cancelEdits(); setOpen(false) }

  return (
    <>
      <Card variant="outlined" sx={{
        transition: 'border-color .15s, box-shadow .15s',
        '&:hover': { borderColor: 'primary.main', boxShadow: 1 },
      }}>
        <CardContent sx={{ py: 1, '&:last-child': { pb: 1 } }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <StatusChip value={draft.status} kind="draft" />
            {draft.bucket && (
              <Chip size="small" label={draft.bucket} color={BUCKET_COLORS[draft.bucket] ?? 'default'} />
            )}
            <Typography variant="body2" onClick={openView}
              sx={{
                flex: 1, fontWeight: 500, cursor: 'pointer', minWidth: 0,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}
              title={draft.title}>{draft.title}</Typography>
            <Chip size="small" variant="outlined"
              label={`${steps.length} step${steps.length === 1 ? '' : 's'}`} />
            {isEditable && (
              <>
                {draft.status !== 'Approved' && (
                  <Tooltip title="Approve">
                    <IconButton size="small" color="success" onClick={onApprove}>
                      <CheckIcon fontSize="small" />
                    </IconButton>
                  </Tooltip>
                )}
                <Tooltip title="Remove">
                  <IconButton size="small" color="error" onClick={onReject}>
                    <CloseIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </>
            )}
            <Button size="small" variant="outlined" startIcon={<VisibilityIcon />} onClick={openView}>
              View
            </Button>
          </Stack>
          {isEditable && onFeedbackChange && (
            <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'flex-start' }}>
              <TextField size="small" fullWidth multiline
                placeholder="Feedback for this case (used on Regenerate)"
                value={feedback ?? ''} onChange={e => onFeedbackChange(e.target.value)}
                disabled={markedRemove} />
              <Tooltip title="Mark this case for removal on Regenerate">
                <FormControlLabel sx={{ m: 0, whiteSpace: 'nowrap' }}
                  control={<Checkbox size="small" checked={!!markedRemove}
                    onChange={(_, v) => onToggleRemove?.(v)} />}
                  label={<Typography variant="caption">Remove</Typography>} />
              </Tooltip>
            </Stack>
          )}
        </CardContent>
      </Card>

      <Dialog open={open} onClose={closeDialog} maxWidth="md" fullWidth scroll="paper">
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
          <StatusChip value={draft.status} kind="draft" />
          {draft.bucket && <Chip size="small" label={draft.bucket} color={BUCKET_COLORS[draft.bucket] ?? 'default'} />}
          <Box sx={{ flex: 1, minWidth: 0 }}>
            {editing
              ? <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)}
                  placeholder="Case title" />
              : <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>{draft.title}</Typography>}
          </Box>
          <IconButton size="small" onClick={closeDialog} aria-label="close"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Box sx={{ mb: 2 }}>
            <Typography variant="overline" color="text.secondary">Preconditions</Typography>
            {editing
              ? <TextField fullWidth size="small" multiline minRows={2} value={preconditions}
                  onChange={e => setPreconditions(e.target.value)} />
              : <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                  {draft.preconditions?.trim() || '—'}
                </Typography>}
          </Box>
          <Box sx={{ overflowX: 'auto' }}>
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
                    <TableCell sx={{ verticalAlign: 'top' }}>{i + 1}</TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top', whiteSpace: 'pre-wrap' }}>
                      {editing
                        ? <TextField fullWidth size="small" multiline value={s.action}
                            onChange={e => updateStep(i, 'action', e.target.value)} />
                        : s.action}
                    </TableCell>
                    <TableCell sx={{ minWidth: 240, verticalAlign: 'top', whiteSpace: 'pre-wrap' }}>
                      {editing
                        ? <TextField fullWidth size="small" multiline value={s.expected}
                            onChange={e => updateStep(i, 'expected', e.target.value)} />
                        : s.expected}
                    </TableCell>
                    {editing && (
                      <TableCell sx={{ verticalAlign: 'top', whiteSpace: 'nowrap' }}>
                        <Tooltip title="Move up"><span>
                          <IconButton size="small" disabled={i === 0} onClick={() => moveStep(i, -1)}>
                            <ExpandLessIcon fontSize="small" />
                          </IconButton>
                        </span></Tooltip>
                        <Tooltip title="Move down"><span>
                          <IconButton size="small" disabled={i === steps.length - 1} onClick={() => moveStep(i, 1)}>
                            <ExpandMoreIcon fontSize="small" />
                          </IconButton>
                        </span></Tooltip>
                        <Tooltip title="Delete step">
                          <IconButton size="small" color="error" onClick={() => removeStep(i)}>
                            <DeleteIcon fontSize="small" />
                          </IconButton>
                        </Tooltip>
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
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 1.5 }}>
          {editing ? (
            <>
              <Button onClick={cancelEdits} disabled={saving}>Cancel</Button>
              <Button variant="contained" disabled={saving}
                startIcon={saving ? <CircularProgress size={16} /> : <SaveIcon />}
                onClick={commitEdits}>Save</Button>
            </>
          ) : (
            <>
              {isEditable && <Button startIcon={<EditIcon />} onClick={openEdit}>Edit</Button>}
              {isEditable && (
                <Button color="error" startIcon={<CloseIcon />}
                  onClick={() => { onReject(); setOpen(false) }}>Remove</Button>
              )}
              {isEditable && draft.status !== 'Approved' && (
                <Button color="success" variant="contained" startIcon={<CheckIcon />}
                  onClick={() => { onApprove(); setOpen(false) }}>Approve</Button>
              )}
              <Button onClick={() => setOpen(false)}>Close</Button>
            </>
          )}
        </DialogActions>
      </Dialog>
    </>
  )
}

/**
 * Tester-step equivalent for QA Loop UI cases — tiles → review dialog with full
 * draft cards (independent of Author Tests; duplicated on purpose).
 */
export default function UiCaseReviewPanel({
  sessionId, drafts, onError, refetch, onGenerateScripts, generating,
}: {
  sessionId: number
  drafts: ApiTestDraftDto[]
  onError: (e: unknown, msg: string) => void
  refetch: () => void
  onGenerateScripts: () => void
  generating: boolean
}) {
  const [tcOpen, setTcOpen] = useState(false)
  const [tcFilter, setTcFilter] = useState<'all' | 'Draft' | 'Approved' | 'Rejected'>('all')
  const [perTestFb, setPerTestFb] = useState<Record<number, string>>({})
  const [removeSet, setRemoveSet] = useState<Record<number, boolean>>({})
  const [overallFeedback, setOverallFeedback] = useState('')
  const [refineBusy, setRefineBusy] = useState(false)

  const cnt = (s: string) => drafts.filter(d => d.status === s).length
  const tiles = [
    { key: 'all' as const, label: 'Total', value: drafts.length, color: 'primary.main' },
    { key: 'Draft' as const, label: 'Pending', value: cnt('Draft'), color: 'warning.main' },
    { key: 'Approved' as const, label: 'Approved', value: cnt('Approved'), color: 'success.main' },
    { key: 'Rejected' as const, label: 'Rejected', value: cnt('Rejected'), color: 'error.main' },
  ]
  const visible = [...drafts]
    .filter(d => tcFilter === 'all' || d.status === tcFilter)
    .sort((a, b) => a.id - b.id)
  const openTile = (k: typeof tcFilter) => { setTcFilter(k); setTcOpen(true) }

  async function saveDraft(id: number, patch: DraftPatch) {
    try {
      await patchDraft(id, {
        title: patch.title,
        preconditions: patch.preconditions,
        stepsJson: JSON.stringify(patch.steps),
      })
      refetch()
    } catch (e) { onError(e, 'Failed to save case') }
  }

  async function approveDraft(id: number) {
    try { await patchDraft(id, { status: 'Approved' }); refetch() }
    catch (e) { onError(e, 'Failed to approve') }
  }

  async function rejectDraft(id: number) {
    try { await patchDraft(id, { status: 'Rejected' }); refetch() }
    catch (e) { onError(e, 'Failed to reject') }
  }

  async function approveAll() {
    // Only UiCase drafts — avoid shared approve-all which would touch API drafts too.
    try {
      await Promise.all(
        drafts.filter(d => d.status === 'Draft').map(d => patchDraft(d.id, { status: 'Approved' })),
      )
      refetch()
    } catch (e) { onError(e, 'Approve all failed') }
  }

  async function regenerate() {
    setRefineBusy(true)
    try {
      const perDraft = drafts
        .map(d => ({
          draftId: d.id,
          feedback: (perTestFb[d.id] ?? '').trim() || undefined,
          remove: !!removeSet[d.id],
        }))
        .filter(x => x.feedback || x.remove)
      await refineUiCases(sessionId, {
        overallFeedback: overallFeedback.trim() || undefined,
        perDraft,
      })
      setOverallFeedback('')
      setPerTestFb({})
      setRemoveSet({})
      setTcOpen(false)
      refetch()
    } catch (e) { onError(e, 'Failed to regenerate UI cases') }
    finally { setRefineBusy(false) }
  }

  const canRegen = overallFeedback.trim()
    || Object.values(perTestFb).some(v => (v ?? '').trim())
    || Object.values(removeSet).some(Boolean)

  return (
    <Stack spacing={1.5}>
      <Typography variant="subtitle2" color="text.secondary">
        UI cases generated — click a tile to review
      </Typography>
      <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(116px, 1fr))', gap: 1.5 }}>
        {tiles.map(t => (
          <Box key={t.key} role="button" tabIndex={0} onClick={() => openTile(t.key)}
            onKeyDown={e => {
              if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openTile(t.key) }
            }}
            sx={{
              cursor: 'pointer', borderRadius: 3, transition: 'transform .12s',
              '&:hover': { transform: 'translateY(-2px)' },
              '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 },
            }}>
            <StatTile icon={<RuleIcon />} label={t.label} value={t.value} color={t.color} />
          </Box>
        ))}
      </Box>

      <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
        <Button size="small" variant="contained" onClick={onGenerateScripts}
          disabled={generating || !drafts.some(d => d.status === 'Approved')}
          startIcon={generating ? <CircularProgress size={14} /> : undefined}>
          {generating ? 'Generating scripts…' : 'Generate Scripts'}
        </Button>
      </Stack>

      <Dialog open={tcOpen} onClose={() => setTcOpen(false)} maxWidth="md" fullWidth scroll="paper">
        <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
          <Typography variant="h6" component="span" sx={{ fontWeight: 600 }}>
            {tcFilter === 'all'
              ? 'Generated UI cases'
              : `${tcFilter === 'Draft' ? 'Pending' : tcFilter} UI cases`}
          </Typography>
          <Chip size="small" label={visible.length} />
          <Box sx={{ flex: 1 }} />
          {tcFilter !== 'all' && <Button size="small" onClick={() => setTcFilter('all')}>Show all</Button>}
          <Button size="small" variant="outlined" onClick={approveAll}>Approve all</Button>
          <IconButton size="small" aria-label="close" onClick={() => setTcOpen(false)}>
            <CloseIcon />
          </IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={1.5}>
            {visible.map(d => (
              <UiCaseDraftCard key={d.id} draft={d}
                onApprove={() => approveDraft(d.id)}
                onReject={() => rejectDraft(d.id)}
                onSave={p => saveDraft(d.id, p)}
                feedback={perTestFb[d.id]}
                onFeedbackChange={v => setPerTestFb(m => ({ ...m, [d.id]: v }))}
                markedRemove={!!removeSet[d.id]}
                onToggleRemove={v => setRemoveSet(m => ({ ...m, [d.id]: v }))} />
            ))}
            {visible.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
                No {tcFilter === 'Draft' ? 'pending' : tcFilter} UI cases.
              </Typography>
            )}
          </Stack>
          <Divider sx={{ my: 2.5 }} />
          <Typography variant="subtitle2" gutterBottom>Regenerate with feedback</Typography>
          <Typography variant="caption" color="text.secondary">
            Leave feedback on individual cases above (or mark some to remove), and/or give overall direction.
            Approved cases are kept; pending ones are replaced.
          </Typography>
          <TextField size="small" multiline minRows={2} fullWidth sx={{ mt: 1 }}
            value={overallFeedback} onChange={e => setOverallFeedback(e.target.value)}
            placeholder="Overall feedback — e.g. add a cancel journey; drop admin-only cases." />
          <Box sx={{ textAlign: 'right', mt: 1 }}>
            <Button variant="outlined" size="small" disabled={refineBusy || !canRegen}
              startIcon={refineBusy ? <CircularProgress size={14} /> : <RuleIcon />}
              onClick={regenerate}>
              Regenerate with feedback
            </Button>
          </Box>
          <Divider sx={{ my: 2.5 }} />
          <Typography variant="subtitle2" gutterBottom>Next step</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            Approve the cases you want, then generate Playwright scripts from them.
          </Typography>
          {drafts.filter(d => d.status === 'Approved').length === 0 && (
            <Alert severity="info">Approve at least one case before generating scripts.</Alert>
          )}
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 1.5 }}>
          <Button onClick={() => setTcOpen(false)}>Close</Button>
          <Button variant="contained" onClick={() => { setTcOpen(false); onGenerateScripts() }}
            disabled={generating || !drafts.some(d => d.status === 'Approved')}
            startIcon={generating ? <CircularProgress size={16} /> : undefined}>
            {generating ? 'Generating…' : 'Generate Scripts'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
