import { useState } from 'react'
import {
  Alert, Box, Button, Card, CardContent, CardHeader, Chip, CircularProgress,
  Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel,
  Grid, Stack, Switch, TextField, Typography,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import AddIcon from '@mui/icons-material/Add'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import PageHeader from '../components/PageHeader'
import EmptyState from '../components/EmptyState'
import { axiosMessage } from '../common/errors'
import {
  listWatches, addWatch, updateWatch, runLatest,
  type PipelineWatchDto,
} from '../api/qaPipelines'
import { PROJECT_ID } from '../config/env'

function fmtDate(s: string | null) { return s ? new Date(s).toLocaleString() : 'Never' }

// ── AddWatchDialog ─────────────────────────────────────────────────────────────
function AddWatchDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient()
  const [pipelineId, setPipelineId] = useState('')
  const [pipelineName, setPipelineName] = useState('')
  const [module, setModule] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  async function doAdd() {
    const id = parseInt(pipelineId, 10)
    if (!id || !pipelineName.trim() || !module.trim()) { setError('All fields required'); return }
    setSaving(true); setError('')
    try {
      await addWatch(PROJECT_ID, { adoPipelineId: id, adoPipelineName: pipelineName.trim(), module: module.trim() })
      qc.invalidateQueries({ queryKey: ['qa-pipelines'] })
      onClose()
    } catch (err) { setError(axiosMessage(err)) }
    finally { setSaving(false) }
  }

  function handleClose() { setPipelineId(''); setPipelineName(''); setModule(''); setError(''); onClose() }

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="xs" fullWidth>
      <DialogTitle>Watch ADO Pipeline</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {error && <Alert severity="error">{error}</Alert>}
          <TextField label="ADO Pipeline ID" size="small" value={pipelineId}
            onChange={e => setPipelineId(e.target.value)} type="number" />
          <TextField label="Pipeline name" size="small" value={pipelineName}
            onChange={e => setPipelineName(e.target.value)} />
          <TextField label="Module" size="small" value={module}
            onChange={e => setModule(e.target.value)} placeholder="e.g. Deals" />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose}>Cancel</Button>
        <Button variant="contained" disabled={saving}
          startIcon={saving ? <CircularProgress size={14} /> : undefined} onClick={doAdd}>
          Add
        </Button>
      </DialogActions>
    </Dialog>
  )
}

// ── WatchCard ──────────────────────────────────────────────────────────────────
function WatchCard({ watch }: { watch: PipelineWatchDto }) {
  const qc = useQueryClient()
  const [running, setRunning] = useState(false)
  const [error, setError] = useState('')

  async function doEnable(enabled: boolean) {
    try { await updateWatch(watch.id, { enabled }); qc.invalidateQueries({ queryKey: ['qa-pipelines'] }) }
    catch (err) { setError(axiosMessage(err)) }
  }
  async function doRunLatest() {
    setRunning(true); setError('')
    try { await runLatest(watch.id); qc.invalidateQueries({ queryKey: ['qa-pipelines'] }) }
    catch (err) { setError(axiosMessage(err)) }
    finally { setRunning(false) }
  }

  return (
    <Card variant="outlined">
      <CardHeader
        title={<Typography variant="subtitle2">{watch.adoPipelineName}</Typography>}
        subheader={<Typography variant="caption">Module: {watch.module} · Pipeline #{watch.adoPipelineId}</Typography>}
        action={
          <FormControlLabel control={<Switch size="small" checked={watch.enabled} onChange={e => doEnable(e.target.checked)} />}
            label={<Typography variant="caption">{watch.enabled ? 'Enabled' : 'Paused'}</Typography>} />
        }
        sx={{ pb: 0 }}
      />
      <CardContent sx={{ pt: 1 }}>
        {error && <Alert severity="error" sx={{ mb: 1 }} onClose={() => setError('')}>{error}</Alert>}
        <Stack direction="row" spacing={2} sx={{ mb: 1, flexWrap: 'wrap' }}>
          <Typography variant="caption" color="text.secondary">Last poll: {fmtDate(watch.lastPollAt)}</Typography>
          {watch.lastSeenBuildId && <Chip size="small" label={`Build #${watch.lastSeenBuildId}`} variant="outlined" />}
        </Stack>
        {watch.lastPollError && (
          <Typography variant="caption" color="error" sx={{ display: 'block', mb: 1 }}>{watch.lastPollError}</Typography>
        )}
        <Button size="small" variant="outlined" startIcon={running ? <CircularProgress size={14} /> : <PlayArrowIcon />}
          disabled={running || !watch.enabled} onClick={doRunLatest}>
          Run latest
        </Button>
      </CardContent>
    </Card>
  )
}

// ── Page ──────────────────────────────────────────────────────────────────────
export default function QaPipelinesPage() {
  const [addOpen, setAddOpen] = useState(false)
  const watchesQ = useQuery({ queryKey: ['qa-pipelines'], queryFn: () => listWatches(PROJECT_ID) })
  const watches = watchesQ.data?.items ?? []

  return (
    <Box>
      <PageHeader title="QA Pipelines" subtitle="Watch ADO pipelines and trigger QA runs automatically"
        actions={<Button variant="contained" startIcon={<AddIcon />} onClick={() => setAddOpen(true)}>Watch pipeline</Button>} />
      <Box sx={{ px: 3 }}>
        {watches.length === 0 && !watchesQ.isLoading
          ? <EmptyState icon={<PlayArrowIcon />} title="No pipeline watchers" hint="Add a watcher to trigger QA runs when an ADO pipeline completes." />
          : (
            <Grid container spacing={2}>
              {watches.map(w => (
                <Grid size={{ xs: 12, sm: 6, md: 4 }} key={w.id}>
                  <WatchCard watch={w} />
                </Grid>
              ))}
            </Grid>
          )}
      </Box>
      <AddWatchDialog open={addOpen} onClose={() => setAddOpen(false)} />
    </Box>
  )
}
