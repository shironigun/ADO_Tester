import { useEffect, useRef, useState } from 'react'
import {
  Alert, Box, Button, Chip, CircularProgress, Divider, LinearProgress, Paper,
  Stack, Tab, Tabs, Typography,
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
  type ScanInventory, type SchemaProduct,
} from '../api/schema'
import { useEventStream } from '../hooks/useEventStream'

type ExplorerTab = 'live' | 'mindmap'

const BUCKET_LABELS: Record<string, string> = {
  dbContexts: 'DbContexts', entityConfigs: 'Entity configs', migrations: 'Migrations',
  sql: 'SQL files', entities: 'Entities', controllers: 'Controllers', frontendApi: 'Frontend API',
}

// ── Inventory summary ────────────────────────────────────────────────────────
function InventorySummary({ inventory }: { inventory: ScanInventory }) {
  const buckets = Object.entries(inventory.buckets).sort((a, b) => b[1].length - a[1].length)
  return (
    <Stack spacing={1.5}>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Chip size="small" color="primary" variant="outlined" label={`${inventory.totalFiles} files`} />
        <Chip size="small" variant="outlined" label={`branch: ${inventory.branch}`} />
        {inventory.commitId && (
          <Chip size="small" variant="outlined" label={`commit ${inventory.commitId.slice(0, 7)}`} />
        )}
      </Stack>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
        {buckets.map(([name, paths]) => (
          <Chip key={name} size="small" color="info" variant="outlined"
            label={`${BUCKET_LABELS[name] ?? name} · ${paths.length}`} />
        ))}
      </Stack>
      <Typography variant="caption" color="text.secondary">
        These candidate files feed the next phase — parsing them into the ERD (tables + relationships).
      </Typography>
    </Stack>
  )
}

// ── Scan console (live SSE) + inventory. Remount per scan via key={scanId}. ──────
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

  const inventory = scanQuery.data?.inventory ?? null
  const running = (status === 'Running' || status === 'Queued' || status === undefined) && !doneRef.current

  return (
    <Stack spacing={2}>
      {running && <LinearProgress />}
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
      {status === 'Succeeded' && (
        <>
          <Divider textAlign="left"><Typography variant="overline">Schema graph</Typography></Divider>
          <SchemaGraphView scanId={scanId} />
        </>
      )}
      {inventory && (
        <>
          <Divider textAlign="left"><Typography variant="overline">Inventory</Typography></Divider>
          <InventorySummary inventory={inventory} />
        </>
      )}
      {scanQuery.data?.scan.status === 'Failed' && (
        <Alert severity="error">{scanQuery.data.scan.errorMessage ?? 'Scan failed.'}</Alert>
      )}
    </Stack>
  )
}

// ── Scan panel (the Database Mindmap tab, pre-graph) ────────────────────────────
function ScanPanel({ product }: { product: SchemaProduct }) {
  const qc = useQueryClient()
  const [scanId, setScanId] = useState<number | null>(product.latestScan?.id ?? null)
  const [live, setLive] = useState(false)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState('')

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
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
        <Button variant="contained" startIcon={starting ? <CircularProgress size={14} /> : <RadarIcon />}
          disabled={starting || live} onClick={doScan}>
          {hasScan ? 'Rescan repository' : 'Scan repository'}
        </Button>
        <Typography variant="body2" color="text.secondary">
          One read-only pass lists the repo and finds the schema-relevant files. The ERD is built from these next.
        </Typography>
      </Stack>
      {error && <Alert severity="error">{error}</Alert>}
      {hasScan
        ? <ScanConsole key={scanId} scanId={scanId!} live={live}
            onDone={() => { setLive(false); qc.invalidateQueries({ queryKey: ['schema-product'] }) }} />
        : <EmptyState title="Not scanned yet"
            hint="Run the first scan to inventory this product's database sources." />}
    </Stack>
  )
}

// ── Page ───────────────────────────────────────────────────────────────────
export default function SchemaExplorerPage() {
  const [tab, setTab] = useState<ExplorerTab>('live')
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

      <Tabs value={tab} onChange={(_, v: ExplorerTab) => setTab(v)}
        sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}>
        <Tab value="live" icon={<TravelExploreIcon />} iconPosition="start"
          label="Live Explorer" sx={{ textTransform: 'none', fontWeight: 600, minHeight: 48 }} />
        <Tab value="mindmap" icon={<HubIcon />} iconPosition="start"
          label="Database Mindmap" sx={{ textTransform: 'none', fontWeight: 600, minHeight: 48 }} />
      </Tabs>

      {tab === 'live' && <LiveExplorer product={product} />}
      {tab === 'mindmap' && <ScanPanel product={product} />}
    </Box>
  )
}
