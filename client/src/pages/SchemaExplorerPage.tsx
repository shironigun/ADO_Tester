import { useEffect, useRef, useState } from 'react'
import {
  Alert, Box, Button, CircularProgress, LinearProgress, Paper,
  Stack, Step, StepLabel, Stepper, Tab, Tabs, Typography,
} from '@mui/material'
import HubIcon from '@mui/icons-material/HubOutlined'
import TravelExploreIcon from '@mui/icons-material/TravelExploreOutlined'
import RadarIcon from '@mui/icons-material/RadarOutlined'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import PageHeader from '../components/PageHeader'
import EmptyState from '../components/EmptyState'
import SchemaGraphView from '../components/SchemaGraphView'
import LiveExplorer from '../components/LiveExplorer'
import { axiosMessage } from '../common/errors'
import {
  getCurrentProduct, getScan, scanStreamUrl, startScan,
  type SchemaProduct,
} from '../api/schema'
import { useEventStream } from '../hooks/useEventStream'

type ExplorerTab = 'live' | 'mindmap'

// The backend scan runs three phases (SchemaScanService: scan.Phase), in order.
const SCAN_STEPS = [
  { key: 'Inventory', label: 'Inventory', desc: 'Listing repo & finding schema files' },
  { key: 'Parse', label: 'Parse schema', desc: 'Reading DbContexts & entities' },
  { key: 'Infer', label: 'Infer relations', desc: 'Linking tables into the ERD' },
] as const

/** Map the scan's current phase/status onto a Stepper active-step index. */
function scanStep(phase: string | null | undefined, status: string | undefined): number {
  if (status === 'Succeeded') return SCAN_STEPS.length
  const i = SCAN_STEPS.findIndex(s => s.key === phase)
  return i < 0 ? 0 : i
}

// ── Scan console (live SSE) + ERD. Remount per scan via key={scanId}. ──────

function ScanConsole({ scanId, live, onDone }: { scanId: number; live: boolean; onDone: () => void }) {
  const { events } = useEventStream(live ? scanStreamUrl(scanId) : null)
  // Poll until terminal — robust even if the scan finishes before the SSE socket connects.
  const scanQuery = useQuery({
    queryKey: ['schema-scan', scanId],
    queryFn: () => getScan(scanId),
    refetchInterval: q => {
      const st = q.state.data?.scan.status
      return st === 'Succeeded' || st === 'Failed' ? false : 1500
    },
  })
  const doneRef = useRef(false)

  const status = scanQuery.data?.scan.status
  useEffect(() => {
    const last = events[events.length - 1]
    const terminalEvent = last && (last.kind === 'result' || last.kind === 'error')
    if (!doneRef.current && (status === 'Succeeded' || status === 'Failed' || terminalEvent)) {
      doneRef.current = true
      onDone()
    }
  }, [events, status, onDone])

  const running = (status === 'Running' || status === 'Queued' || status === undefined) && !doneRef.current
  const step = scanStep(scanQuery.data?.scan.phase, status)

  return (
    <Stack spacing={2}>
      {running && (
        <Paper variant="outlined" sx={{ p: 2, bgcolor: 'background.default' }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 2 }}>
            <CircularProgress size={18} />
            <Typography variant="subtitle2">Building the mindmap…</Typography>
          </Stack>
          <Stepper activeStep={step} alternativeLabel>
            {SCAN_STEPS.map(s => (
              <Step key={s.key}>
                <StepLabel optional={<Typography variant="caption" color="text.secondary">{s.desc}</Typography>}>
                  {s.label}
                </StepLabel>
              </Step>
            ))}
          </Stepper>
          <LinearProgress sx={{ mt: 2 }} />
        </Paper>
      )}
      {events.length > 0 && (
        <Paper variant="outlined" sx={{
          p: 1.5, maxHeight: 260, overflow: 'auto', bgcolor: 'background.default',
          fontFamily: 'monospace', fontSize: 12.5, lineHeight: 1.6,
        }}>
          {events.map(e => (
            <Box key={e.seq} sx={{
              whiteSpace: 'pre-wrap',
              color: e.level === 'error' ? 'error.main'
                : e.level === 'success' ? 'success.main'
                : e.level === 'warn' ? 'warning.main' : 'text.secondary',
            }}>
              {e.text}
            </Box>
          ))}
        </Paper>
      )}
      {status === 'Succeeded' && <SchemaGraphView scanId={scanId} />}
      {scanQuery.data?.scan.status === 'Failed' && (
        <Alert severity="error">{scanQuery.data.scan.errorMessage ?? 'Scan failed.'}</Alert>
      )}
    </Stack>
  )
}

// ── Explorer body (tabs + inline scan control + content) ────────────────────────
function ExplorerBody({ product }: { product: SchemaProduct }) {
  const qc = useQueryClient()
  const [tab, setTab] = useState<ExplorerTab>('live')
  const [scanId, setScanId] = useState<number | null>(product.latestScan?.id ?? null)
  const [live, setLive] = useState(false)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState('')
  // Container for the Live Explorer's trace controls, rendered inline with the tabs.
  const [liveToolbar, setLiveToolbar] = useState<HTMLDivElement | null>(null)

  async function doScan() {
    setStarting(true); setError('')
    try {
      const { scanId: id } = await startScan(product.id)
      setScanId(id); setLive(true)
    } catch (err) { setError(axiosMessage(err)) }
    finally { setStarting(false) }
  }

  const hasScan = scanId != null

  return (
    <>
      {/* Tab bar with the scan control inline — visible only on the Mindmap tab. */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, borderBottom: 1, borderColor: 'divider', mb: 2 }}>
        <Tabs value={tab} onChange={(_, v: ExplorerTab) => setTab(v)} sx={{ flex: 1, minHeight: 48 }}>
          <Tab value="live" icon={<TravelExploreIcon />} iconPosition="start"
            label="Live Explorer" sx={{ textTransform: 'none', fontWeight: 600, minHeight: 48 }} />
          <Tab value="mindmap" icon={<HubIcon />} iconPosition="start"
            label="Database Mindmap" sx={{ textTransform: 'none', fontWeight: 600, minHeight: 48 }} />
        </Tabs>
        {tab === 'mindmap' && (
          <Button variant="contained" size="small" sx={{ flexShrink: 0 }}
            startIcon={starting ? <CircularProgress size={14} /> : <RadarIcon />}
            disabled={starting || live} onClick={doScan}>
            {hasScan ? 'Rescan repository' : 'Scan repository'}
          </Button>
        )}
        {tab === 'live' && (
          <Box ref={setLiveToolbar} sx={{ display: 'flex', alignItems: 'center', flexShrink: 0 }} />
        )}
      </Box>

      {tab === 'live' && <LiveExplorer product={product} toolbarContainer={liveToolbar} />}
      {tab === 'mindmap' && (
        <Stack spacing={2}>
          {error && <Alert severity="error">{error}</Alert>}
          {hasScan
            ? <ScanConsole key={scanId} scanId={scanId!} live={live}
                onDone={() => { setLive(false); qc.invalidateQueries({ queryKey: ['schema-product'] }) }} />
            : <EmptyState title="Not scanned yet"
                hint="Run the first scan to inventory this product's database sources." />}
        </Stack>
      )}
    </>
  )
}

// ── Page ───────────────────────────────────────────────────────────────────
export default function SchemaExplorerPage() {
  const { data: product, isLoading } = useQuery({ queryKey: ['schema-product'], queryFn: getCurrentProduct })

  const header = <PageHeader title="Schema Explorer" />

  if (isLoading || !product) {
    return (
      <Box>
        {header}
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}><CircularProgress /></Box>
      </Box>
    )
  }

  return (
    <Box>
      {header}
      <ExplorerBody product={product} />
    </Box>
  )
}
