import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Chip, CircularProgress, Dialog,
  DialogContent, DialogTitle, FormControl, IconButton, InputLabel, MenuItem,
  Select, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import DownloadIcon from '@mui/icons-material/Download'
import OpenInFullIcon from '@mui/icons-material/OpenInFull'
import RefreshIcon from '@mui/icons-material/Refresh'
import CloseIcon from '@mui/icons-material/Close'
import AssessmentIcon from '@mui/icons-material/AssessmentOutlined'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import StatusChip from '../components/StatusChip'
import EmptyState from '../components/EmptyState'
import { alpha } from '@mui/material/styles'
import { Stagger, StaggerItem, PulsingAgentBadge } from '../motion/primitives'
import AgentConsole from '../components/AgentConsole'
import { type ConsoleEvent } from '../hooks/useEventStream'
import { PROJECT_ID } from '../config/env'
import { axiosMessage } from '../common/errors'
import { getConfig, getAdoAreaPaths } from '../api/settings'
import {
  listReports, generateReport, reportHtmlUrl, reportDownloadUrl, reportStreamUrl,
  type Report, type Theme,
} from '../api/reports'

function parseEventLog(json: string | null | undefined): ConsoleEvent[] {
  if (!json) return []
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : [] } catch { return [] }
}

// Theme → accent palette key for the report card stripe. (Theme + Report are imported from api/reports.)
const THEME_ACCENT: Record<Theme, 'info' | 'secondary' | 'primary'> = {
  Executive: 'info', Standup: 'secondary', Branded: 'primary',
}

const THEME_BLURB: Record<Theme, string> = {
  Executive: 'Light, minimal, serif headlines. Stakeholder reviews.',
  Standup:   'Dark slate, neon accents, dense. Team meetings + projectors.',
  Branded:   'ConstellationDealer corporate palette + logo lockup.',
}

function isoLocalDate(d: Date): string {
  // For <input type="date"> — YYYY-MM-DD
  const z = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${z(d.getMonth() + 1)}-${z(d.getDate())}`
}

export default function ReportsPage() {
  const today = new Date()
  const weekAgo = new Date(); weekAgo.setDate(weekAgo.getDate() - 7)

  const [fromDate, setFromDate] = useState(isoLocalDate(weekAgo))
  const [toDate, setToDate] = useState(isoLocalDate(today))
  const [boards, setBoards] = useState<string[]>([])
  const [boardOptions, setBoardOptions] = useState<string[]>([])
  const [boardsLoading, setBoardsLoading] = useState(false)
  const [theme, setTheme] = useState<Theme>('Executive')
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState('')

  const [viewing, setViewing] = useState<Report | null>(null)

  // Reports list, kept live via React Query (F2). Polls every 2.5s only while a report is still
  // generating, with a 25-min cap (600 polls) so a stuck "Generating" can't poll forever.
  const pollCountRef = useRef(0)
  const MAX_POLLS = 600
  const reportsQuery = useQuery({
    queryKey: ['reports', PROJECT_ID],
    queryFn: () => listReports(PROJECT_ID),
    refetchInterval: (q) => {
      const generating = (q.state.data ?? []).some(r => r.status === 'Generating')
      if (!generating) { pollCountRef.current = 0; return false }
      pollCountRef.current += 1
      return pollCountRef.current > MAX_POLLS ? false : 2500
    },
  })
  const reports = reportsQuery.data ?? []
  const loadReports = () => { void reportsQuery.refetch() }

  // Pull the saved default boards (from Settings) + the discoverable area paths.
  useEffect(() => {
    let cancelled = false
    async function go() {
      setBoardsLoading(true)
      try {
        // Saved defaults first — semicolon/comma separated.
        const cfg = await getConfig()
        if (!cancelled) {
          const saved = String(cfg['reports.default_boards'] ?? '')
          setBoards(saved.split(/[;,]/).map(s => s.trim()).filter(Boolean))
        }
        // Then discoverable area paths (drives the dropdown options).
        const areaPaths = await getAdoAreaPaths()
        if (!cancelled) setBoardOptions(areaPaths)
      } catch { /* silent — page still works with free-text fallback */ }
      finally { if (!cancelled) setBoardsLoading(false) }
    }
    go()
    return () => { cancelled = true }
  }, [])

  async function generate() {
    setError(''); setGenerating(true)
    try {
      const from = new Date(fromDate + 'T00:00:00').toISOString()
      const to = new Date(toDate + 'T23:59:59').toISOString()
      await generateReport(PROJECT_ID, { fromUtc: from, toUtc: to, boards, theme })
      await reportsQuery.refetch()
    } catch (e: unknown) {
      // Server may return ProblemDetails JSON or a long HTML error page — guard the body length.
      setError(axiosMessage(e, { maxLen: 500 }))
    } finally { setGenerating(false) }
  }

  return (
    <Box>
      <PageHeader
        title="Reports"
        subtitle="Generate a self-contained HTML report for a date range and boards — view full-screen or download."
      />
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>

        {/* Generate panel */}
        <Box sx={{ minWidth: 320, maxWidth: 380, width: '100%' }}>
          <Section title="Generate report">
            <Stack spacing={2}>
              <TextField label="From" type="date" size="small" value={fromDate}
                onChange={e => setFromDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              <TextField label="To" type="date" size="small" value={toDate}
                onChange={e => setToDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              <Autocomplete
                multiple
                freeSolo
                options={boardOptions}
                value={boards}
                onChange={(_, v) => setBoards(v as string[])}
                loading={boardsLoading}
                renderInput={params => (
                  <TextField {...params} label="Boards / area paths" size="small"
                    helperText={
                      boardsLoading ? 'Loading discovered area paths…'
                      : boardOptions.length > 0
                        ? `Pick from ${boardOptions.length} discovered path(s). Empty = whole project.`
                        : 'No discovered paths — type any area-path string (Enter to add).'
                    } />
                )}
              />
              <FormControl size="small">
                <InputLabel>Theme</InputLabel>
                <Select label="Theme" value={theme}
                  onChange={e => setTheme(e.target.value as Theme)}>
                  {(['Executive', 'Standup', 'Branded'] as Theme[]).map(t => (
                    <MenuItem key={t} value={t}>
                      <Box>
                        <Typography variant="body2"><strong>{t}</strong></Typography>
                        <Typography variant="caption" color="text.secondary">{THEME_BLURB[t]}</Typography>
                      </Box>
                    </MenuItem>
                  ))}
                </Select>
              </FormControl>
              <Button variant="contained" onClick={generate} disabled={generating}>
                {generating ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Generate now'}
              </Button>
              {error && <Alert severity="error">{error}</Alert>}
              <Alert severity="info" sx={{ fontSize: '0.8rem' }}>
                Scheduled run: every Thursday 11:00 AM (when app is running).
              </Alert>
            </Stack>
          </Section>
        </Box>

        {/* History */}
        <Box sx={{ flex: 1, minWidth: 0, width: '100%' }}>
          <Section
            title={`History (${reports.length})`}
            action={
              <Tooltip title="Refresh">
                <IconButton onClick={() => loadReports()}><RefreshIcon /></IconButton>
              </Tooltip>
            }
          >
            {reports.length === 0 ? (
              <EmptyState compact icon={<AssessmentIcon />} title="No reports yet"
                hint="Generate one with the panel on the left." />
            ) : (
              <Stagger>
                <Stack spacing={1}>
                  {reports.map(r => {
                    const accent = THEME_ACCENT[r.theme] ?? 'primary'
                    return (
                    <StaggerItem key={r.id}>
                      <Card sx={{
                        borderLeft: '4px solid',
                        borderLeftColor: theme => theme.palette[accent].main,
                      }}>
                        <CardContent sx={{ pb: '12px !important' }}>
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5 }}>
                            <StatusChip value={r.status} />
                            <Chip size="small" label={r.trigger} variant="outlined" />
                            {r.theme && (
                              <Chip size="small" label={r.theme} color={accent}
                                sx={{ bgcolor: theme => alpha(theme.palette[accent].main, 0.14), color: `${accent}.main` }} />
                            )}
                            <Typography variant="body2" sx={{ fontWeight: 500, flex: 1 }}>{r.title}</Typography>
                            {r.status === 'Generating' && <PulsingAgentBadge size={9} color="warning.main" />}
                            {r.status === 'Done' && (
                              <Chip size="small" variant="outlined"
                                label={`${r.ticketCount} ticket${r.ticketCount === 1 ? '' : 's'}`} />
                            )}
                            <Typography variant="caption" color="text.secondary">
                              {new Date(r.createdAt).toLocaleString()}
                            </Typography>
                          </Stack>

                          <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'center', flexWrap: 'wrap' }}>
                            <Typography variant="caption" color="text.secondary">
                              {new Date(r.fromUtc).toLocaleDateString()} → {new Date(r.toUtc).toLocaleDateString()}
                            </Typography>
                            {r.boards && <Typography variant="caption" color="text.secondary">· {r.boards}</Typography>}
                            <Box sx={{ flex: 1 }} />
                            {r.status === 'Done' && (
                              <>
                                <Tooltip title="Open full-screen">
                                  <IconButton size="small" onClick={() => setViewing(r)}><OpenInFullIcon fontSize="small" /></IconButton>
                                </Tooltip>
                                <Tooltip title="Download .html">
                                  <IconButton size="small" component="a"
                                    href={reportDownloadUrl(r.id)}
                                    download>
                                    <DownloadIcon fontSize="small" />
                                  </IconButton>
                                </Tooltip>
                              </>
                            )}
                          </Stack>
                          {r.status === 'Failed' && r.errorMessage && (
                            <Alert severity="error" sx={{ mt: 1 }}>{r.errorMessage}</Alert>
                          )}
                          {r.status === 'Generating' && (
                            <AgentConsole
                              title="Generation console"
                              streamUrl={reportStreamUrl(r.id)}
                              seedEvents={parseEventLog(r.eventLogJson)}
                            />
                          )}
                        </CardContent>
                      </Card>
                    </StaggerItem>
                  )})}
                </Stack>
              </Stagger>
            )}
          </Section>
        </Box>
      </Stack>

      {/* Full-screen viewer — kept OUTSIDE any motion/AnimatePresence so the iframe
          never remounts (which would reload the report). */}
      <Dialog open={!!viewing} onClose={() => setViewing(null)} fullScreen>
        <DialogTitle sx={{ display: 'flex', alignItems: 'center', gap: 1, p: 1 }}>
          <Typography variant="subtitle1" sx={{ flex: 1 }}>{viewing?.title}</Typography>
          {viewing && (
            <Button variant="outlined" size="small" startIcon={<DownloadIcon />}
              href={reportDownloadUrl(viewing.id)} download>Download</Button>
          )}
          <IconButton onClick={() => setViewing(null)}><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent sx={{ p: 0 }}>
          {viewing && (
            <iframe
              src={reportHtmlUrl(viewing.id)}
              title="report"
              style={{ width: '100%', height: 'calc(100vh - 64px)', border: 0 }}
            />
          )}
        </DialogContent>
      </Dialog>
    </Box>
  )
}
