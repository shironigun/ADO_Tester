import { useEffect, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip, CircularProgress,
  FormControl, IconButton, InputLabel, MenuItem, Paper, Select, Stack, Table, TableBody,
  TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ContentCopyIcon from '@mui/icons-material/ContentCopyOutlined'
import StorageIcon from '@mui/icons-material/StorageOutlined'
import { Link as RouterLink } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { axiosMessage } from '../common/errors'
import { paths } from '../routes/paths'
import {
  listConnections, listEntities, traceEntity,
  type SchemaProduct, type TableTrace, type TraceResult,
} from '../api/schema'

function cell(v: unknown): string {
  if (v === null || v === undefined) return '∅'
  const s = String(v)
  return s.length > 60 ? s.slice(0, 60) + '…' : s
}

function CopyButton({ text }: { text: string }) {
  const [done, setDone] = useState(false)
  return (
    <Tooltip title={done ? 'Copied' : 'Copy SQL'}>
      <IconButton size="small" onClick={() => { navigator.clipboard?.writeText(text); setDone(true); setTimeout(() => setDone(false), 1200) }}>
        <ContentCopyIcon fontSize="inherit" />
      </IconButton>
    </Tooltip>
  )
}

function RowsTable({ trace }: { trace: TableTrace }) {
  if (trace.rows.length === 0) return null
  return (
    <Box sx={{ overflowX: 'auto', border: 1, borderColor: 'divider', borderRadius: 1 }}>
      <Table size="small" sx={{ '& td, & th': { whiteSpace: 'nowrap', fontSize: 12, py: 0.5 } }}>
        <TableHead>
          <TableRow>{trace.columns.map(c => <TableCell key={c} sx={{ fontWeight: 700 }}>{c}</TableCell>)}</TableRow>
        </TableHead>
        <TableBody>
          {trace.rows.map((r, i) => (
            <TableRow key={i}>{trace.columns.map(c => <TableCell key={c}>{cell(r[c])}</TableCell>)}</TableRow>
          ))}
        </TableBody>
      </Table>
    </Box>
  )
}

function SqlBlock({ sql }: { sql: string }) {
  return (
    <Paper variant="outlined" sx={{ p: 1, bgcolor: 'background.default', position: 'relative' }}>
      <Box sx={{ position: 'absolute', top: 4, right: 4 }}><CopyButton text={sql} /></Box>
      <Box component="pre" sx={{ m: 0, pr: 4, fontFamily: 'monospace', fontSize: 12, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
        {sql}
      </Box>
    </Paper>
  )
}

const DIR_COLOR: Record<string, 'primary' | 'info' | 'secondary'> = { root: 'primary', child: 'info', parent: 'secondary' }

function LinkedCard({ trace }: { trace: TableTrace }) {
  return (
    <Accordion disableGutters variant="outlined">
      <AccordionSummary expandIcon={<ExpandMoreIcon />}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5, width: '100%' }}>
          <Typography sx={{ fontFamily: 'monospace', fontWeight: 600 }}>{trace.table}</Typography>
          <Chip size="small" color={DIR_COLOR[trace.direction] ?? 'default'} variant="outlined" label={trace.direction} />
          {trace.service && <Chip size="small" variant="outlined" label={trace.service} />}
          {trace.matchColumn && <Chip size="small" variant="outlined" label={`on ${trace.matchColumn}`} sx={{ fontFamily: 'monospace' }} />}
          <Box sx={{ flexGrow: 1 }} />
          {trace.error
            ? <Chip size="small" color="error" variant="outlined" label="error" />
            : <Chip size="small" color={trace.rowCount > 0 ? 'success' : 'default'} label={`${trace.rowCount} rows`} />}
        </Stack>
      </AccordionSummary>
      <AccordionDetails>
        <Stack spacing={1}>
          <SqlBlock sql={trace.sql} />
          {trace.error
            ? <Alert severity="warning" sx={{ py: 0 }}>{trace.error}</Alert>
            : <RowsTable trace={trace} />}
        </Stack>
      </AccordionDetails>
    </Accordion>
  )
}

// ── Live Explorer ─────────────────────────────────────────────────────────────
export default function LiveExplorer({ product }: { product: SchemaProduct }) {
  const productId = product.id
  const { data: entities = [], isLoading: entLoading } = useQuery({
    queryKey: ['schema-entities', productId], queryFn: () => listEntities(productId),
  })
  const { data: connections = [] } = useQuery({
    queryKey: ['schema-connections', productId], queryFn: () => listConnections(productId),
  })

  const [entityKey, setEntityKey] = useState('')
  const [environment, setEnvironment] = useState('')
  const [idValue, setIdValue] = useState('')
  const [tracing, setTracing] = useState(false)
  const [result, setResult] = useState<TraceResult | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    if (entities.length && !entities.some(e => e.key === entityKey)) setEntityKey(entities[0].key)
  }, [entities, entityKey])
  useEffect(() => {
    if (connections.length === 0) { setEnvironment(''); return }
    setEnvironment(prev => (prev && connections.some(c => c.environment === prev)) ? prev : connections[0].environment)
  }, [connections])

  async function doTrace() {
    if (!entityKey || !idValue.trim() || !environment) return
    setTracing(true); setError(''); setResult(null)
    try { setResult(await traceEntity(productId, entityKey, idValue.trim(), environment)) }
    catch (e) { setError(axiosMessage(e)) }
    finally { setTracing(false) }
  }

  if (entLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>
  if (entities.length === 0) {
    return <Alert severity="info">Run a scan on the <strong>Database Mindmap</strong> tab first — the AI proposes the lookup entities (Customer, Deal, …) once the schema graph is built.</Alert>
  }

  const selected = entities.find(e => e.key === entityKey)
  const hasEnv = connections.length > 0
  const activeServer = connections.find(c => c.environment === environment)?.server

  return (
    <Stack spacing={2}>
      {/* Environment bar (connections are configured in Settings → Database) */}
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
        <StorageIcon fontSize="small" color={hasEnv ? 'success' : 'disabled'} />
        {hasEnv ? (
          <>
            <FormControl size="small" sx={{ minWidth: 160 }}>
              <InputLabel id="env-label">Environment</InputLabel>
              <Select labelId="env-label" label="Environment" value={environment} onChange={e => setEnvironment(e.target.value)}>
                {connections.map(c => <MenuItem key={c.environment} value={c.environment}>{c.environment}</MenuItem>)}
              </Select>
            </FormControl>
            {activeServer && <Chip size="small" variant="outlined" label={activeServer} sx={{ fontFamily: 'monospace' }} />}
          </>
        ) : (
          <Chip size="small" color="warning" variant="outlined" label="No environments configured" />
        )}
        <Button size="small" component={RouterLink} to={paths.settings}>Manage in Settings → Database</Button>
      </Stack>

      {/* Trace controls */}
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1.5 }}>
        <FormControl size="small" sx={{ minWidth: 200 }}>
          <InputLabel id="entity-label">Entity</InputLabel>
          <Select labelId="entity-label" label="Entity" value={entityKey} onChange={e => setEntityKey(e.target.value)}>
            {entities.map(e => <MenuItem key={e.key} value={e.key}>{e.displayName}</MenuItem>)}
          </Select>
        </FormControl>
        <TextField size="small" label={selected?.idColumn || 'id'} value={idValue}
          onChange={e => setIdValue(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') doTrace() }}
          placeholder="record id" sx={{ width: 220 }} />
        <Button variant="contained" onClick={doTrace}
          disabled={tracing || !hasEnv || !idValue.trim()}
          startIcon={tracing ? <CircularProgress size={14} /> : undefined}>
          Trace
        </Button>
        {selected?.scopeColumn && (
          <Typography variant="caption" color="text.secondary">Dealer-scoped via {selected.scopeColumn}</Typography>
        )}
      </Stack>

      {!hasEnv && (
        <Alert severity="warning" sx={{ py: 0 }}>
          Add a read-only environment connection in <strong>Settings → Database</strong> to trace live records.
        </Alert>
      )}
      {error && <Alert severity="error">{error}</Alert>}

      {result && (
        <Stack spacing={1.5}>
          {!result.found
            ? <Alert severity="info">
                No <strong>{result.entity}</strong> row found for {result.idColumn} = {result.idValue} in <strong>{environment}</strong>.
                {result.root.error && <> ({result.root.error})</>}
              </Alert>
            : <>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
                  <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{result.entity}</Typography>
                  <Chip size="small" color="success" variant="outlined" label={environment} />
                  <Chip size="small" color="primary" label={`${result.linkedTableCount} linked tables`} />
                  <Chip size="small" color="info" variant="outlined" label={`${result.totalRows} rows total`} />
                  {result.truncated && <Chip size="small" color="warning" variant="outlined" label={result.truncated} />}
                </Stack>

                <Paper variant="outlined" sx={{ p: 1.5 }}>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
                    <Typography sx={{ fontFamily: 'monospace', fontWeight: 700 }}>{result.root.table}</Typography>
                    <Chip size="small" color="primary" variant="outlined" label="root" />
                    {result.root.service && <Chip size="small" variant="outlined" label={result.root.service} />}
                  </Stack>
                  <Stack spacing={1}>
                    <SqlBlock sql={result.root.sql} />
                    <RowsTable trace={result.root} />
                  </Stack>
                </Paper>

                <Typography variant="overline" color="text.secondary">Linked tables</Typography>
                <Stack spacing={0.75}>
                  {result.linked.map(t => <LinkedCard key={`${t.table}-${t.direction}-${t.matchColumn}`} trace={t} />)}
                  {result.linked.length === 0 && <Typography variant="body2" color="text.secondary">No linked tables in the graph.</Typography>}
                </Stack>
              </>}
        </Stack>
      )}
    </Stack>
  )
}
