import { useEffect, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Box, Button, Chip, CircularProgress,
  Dialog, DialogActions, DialogContent, DialogTitle, IconButton, Stack, Table, TableBody,
  TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import CloseIcon from '@mui/icons-material/Close'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import SaveIcon from '@mui/icons-material/Save'
import StatusChip from '../components/StatusChip'

export interface DraftStep { action: string; expected: string }

// Shared shape between qaLoop.tsx (ApiTestDraftDto) and authorTests.tsx (Draft). We only need the
// fields this dialog renders — either config trims/adapts before passing in.
export interface DraftDetailInput {
  id: number
  title: string
  preconditions: string
  stepsJson: string
  status: string
  specCode?: string | null
  adoTestCaseId?: number | null
}

export interface DraftDetailPatch {
  title?: string
  preconditions?: string
  steps?: DraftStep[]
}

interface Props {
  open: boolean
  onClose: () => void
  draft: DraftDetailInput
  readOnly: boolean
  onSave: (patch: DraftDetailPatch) => Promise<void>
  /** Deep-link URL for the ADO test case; only used when adoTestCaseId is set. */
  adoUrl?: (id: number) => string
}

function parseSteps(json: string): DraftStep[] {
  try {
    const v = JSON.parse(json)
    return Array.isArray(v) ? v.map(s => ({ action: s.action ?? '', expected: s.expected ?? '' })) : []
  } catch { return [] }
}

export default function DraftDetailDialog({ open, onClose, draft, readOnly, onSave, adoUrl }: Props) {
  const [editing, setEditing] = useState(false)
  const [saving, setSaving]   = useState(false)
  const [title, setTitle]     = useState(draft.title)
  const [precond, setPrecond] = useState(draft.preconditions)
  const [steps, setSteps]     = useState<DraftStep[]>(parseSteps(draft.stepsJson))

  // Reset local state when the draft changes or when leaving edit mode.
  useEffect(() => {
    if (!editing) {
      setTitle(draft.title)
      setPrecond(draft.preconditions)
      setSteps(parseSteps(draft.stepsJson))
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft.id, draft.title, draft.preconditions, draft.stepsJson])

  const updateStep = (i: number, f: 'action' | 'expected', v: string) =>
    setSteps(c => c.map((s, idx) => idx === i ? { ...s, [f]: v } : s))
  const addStep    = () => setSteps(c => [...c, { action: '', expected: '' }])
  const removeStep = (i: number) => setSteps(c => c.filter((_, idx) => idx !== i))
  const moveStep   = (i: number, dir: -1 | 1) => setSteps(c => {
    const j = i + dir; if (j < 0 || j >= c.length) return c
    const n = [...c]; [n[i], n[j]] = [n[j], n[i]]; return n
  })

  async function commit() {
    setSaving(true)
    try { await onSave({ title, preconditions: precond, steps }); setEditing(false) }
    finally { setSaving(false) }
  }
  function cancel() {
    setTitle(draft.title); setPrecond(draft.preconditions); setSteps(parseSteps(draft.stepsJson))
    setEditing(false)
  }
  function close() { if (editing) cancel(); onClose() }

  return (
    <Dialog open={open} onClose={close} maxWidth="md" fullWidth scroll="paper">
      <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
        <StatusChip value={draft.status} kind="draft" />
        <Box sx={{ flex: 1, minWidth: 0 }}>
          {editing
            ? <TextField fullWidth size="small" value={title} onChange={e => setTitle(e.target.value)}
                placeholder="[Module] - [Feature] - Verify that ..." />
            : <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>{draft.title}</Typography>}
        </Box>
        {draft.adoTestCaseId && (
          <Chip size="small" color="success" variant="outlined"
            label={`TC #${draft.adoTestCaseId}`}
            onClick={adoUrl ? () => window.open(adoUrl(draft.adoTestCaseId!), '_blank', 'noreferrer') : undefined}
            icon={adoUrl ? <OpenInNewIcon fontSize="small" /> : undefined} />
        )}
        <IconButton size="small" onClick={close} aria-label="close"><CloseIcon /></IconButton>
      </DialogTitle>

      <DialogContent dividers>
        <Stack spacing={2}>
          <Box>
            <Typography variant="caption" color="text.secondary">Preconditions</Typography>
            {editing
              ? <TextField fullWidth size="small" multiline minRows={2} value={precond}
                  onChange={e => setPrecond(e.target.value)} sx={{ mt: 0.5 }} />
              : <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', mt: 0.5 }}>
                  {draft.preconditions || <Box component="span" sx={{ color: 'text.disabled' }}>—</Box>}
                </Typography>}
          </Box>

          <Box>
            <Typography variant="caption" color="text.secondary">Steps</Typography>
            <Box sx={{ overflowX: 'auto', mt: 0.5 }}>
              <Table size="small">
                <TableHead><TableRow>
                  <TableCell sx={{ width: 32 }}>#</TableCell>
                  <TableCell>Action</TableCell>
                  <TableCell>Expected Result</TableCell>
                  {editing && <TableCell sx={{ width: 96 }} />}
                </TableRow></TableHead>
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
                              <ExpandLessIcon fontSize="small" /></IconButton></span></Tooltip>
                          <Tooltip title="Move down"><span>
                            <IconButton size="small" disabled={i === steps.length - 1} onClick={() => moveStep(i, 1)}>
                              <ExpandMoreIcon fontSize="small" /></IconButton></span></Tooltip>
                          <Tooltip title="Delete step">
                            <IconButton size="small" color="error" onClick={() => removeStep(i)}>
                              <DeleteIcon fontSize="small" /></IconButton></Tooltip>
                        </TableCell>
                      )}
                    </TableRow>
                  ))}
                  {steps.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={editing ? 4 : 3} sx={{ color: 'text.disabled', textAlign: 'center', py: 2 }}>
                        No steps.
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
              {editing && <Button size="small" startIcon={<AddIcon />} onClick={addStep} sx={{ mt: 1 }}>Add step</Button>}
            </Box>
          </Box>

          {draft.specCode && (
            <Accordion disableGutters variant="outlined">
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>
                <Typography variant="body2">Playwright spec</Typography>
              </AccordionSummary>
              <AccordionDetails>
                <Box component="pre" sx={{ m: 0, p: 1, bgcolor: 'action.hover', borderRadius: 1,
                  fontSize: 11, overflowX: 'auto', whiteSpace: 'pre-wrap', maxHeight: 360 }}>
                  {draft.specCode}
                </Box>
              </AccordionDetails>
            </Accordion>
          )}
        </Stack>
      </DialogContent>

      <DialogActions sx={{ px: 3, py: 1.5 }}>
        {editing ? (
          <>
            <Button onClick={cancel} disabled={saving}>Cancel</Button>
            <Button variant="contained" disabled={saving}
              startIcon={saving ? <CircularProgress size={16} /> : <SaveIcon />}
              onClick={commit}>Save</Button>
          </>
        ) : (
          <>
            {!readOnly && <Button startIcon={<EditIcon />} onClick={() => setEditing(true)}>Edit</Button>}
            <Button onClick={onClose}>Close</Button>
          </>
        )}
      </DialogActions>
    </Dialog>
  )
}
