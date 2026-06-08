import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Chip, CircularProgress, Dialog,
  DialogContent, DialogTitle, FormControl, IconButton, InputLabel, MenuItem,
  Select, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import DownloadIcon from '@mui/icons-material/Download'
import OpenInFullIcon from '@mui/icons-material/OpenInFull'
import RefreshIcon from '@mui/icons-material/Refresh'
import CloseIcon from '@mui/icons-material/Close'
import axios from 'axios'

const PROJECT_ID = 1

type Theme = 'Executive' | 'Standup' | 'Branded'

const THEME_BLURB: Record<Theme, string> = {
  Executive: 'Light, minimal, serif headlines. Stakeholder reviews.',
  Standup:   'Dark slate, neon accents, dense. Team meetings + projectors.',
  Branded:   'YourOrg corporate palette + logo lockup.',
}

interface Report {
  id: number
  projectId: number
  fromUtc: string
  toUtc: string
  boards: string
  ticketCount: number
  status: 'Generating' | 'Done' | 'Failed'
  htmlPath: string | null
  errorMessage: string | null
  createdAt: string
  title: string
  trigger: string
  theme: Theme
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

  const [reports, setReports] = useState<Report[]>([])
  const [viewing, setViewing] = useState<Report | null>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const pollCountRef = useRef(0)
  const MAX_POLLS = 600 // 2.5s * 600 = 25 min cap

  const loadReports = useCallback(async () => {
    const { data } = await axios.get<Report[]>(`/api/reports/${PROJECT_ID}`)
    setReports(data)
    return data
  }, [])

  useEffect(() => {
    loadReports().catch(() => {})
    return () => { if (pollRef.current) clearInterval(pollRef.current) }
  }, [loadReports])

  // Pull the saved default boards (from Settings) + the discoverable area paths.
  useEffect(() => {
    let cancelled = false
    async function go() {
      setBoardsLoading(true)
      try {
        // Saved defaults first — semicolon/comma separated.
        const cfg = await axios.get<Record<string, string | boolean>>('/api/settings/config')
        if (!cancelled) {
          const saved = String(cfg.data['reports.default_boards'] ?? '')
          setBoards(saved.split(/[;,]/).map(s => s.trim()).filter(Boolean))
        }
        // Then discoverable area paths (drives the dropdown options).
        const r = await axios.get<{ areaPaths: string[] }>('/api/settings/ado/area-paths')
        if (!cancelled) setBoardOptions(r.data.areaPaths ?? [])
      } catch { /* silent — page still works with free-text fallback */ }
      finally { if (!cancelled) setBoardsLoading(false) }
    }
    go()
    return () => { cancelled = true }
  }, [])

  // Poll while any report is generating
  useEffect(() => {
    const hasGenerating = reports.some(r => r.status === 'Generating')
    if (hasGenerating && !pollRef.current) {
      pollCountRef.current = 0
      pollRef.current = setInterval(() => {
        pollCountRef.current += 1
        if (pollCountRef.current > MAX_POLLS) {
          if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null }
          return
        }
        loadReports().catch(() => {})
      }, 2500)
    } else if (!hasGenerating && pollRef.current) {
      clearInterval(pollRef.current); pollRef.current = null
    }
  }, [reports, loadReports])

  async function generate() {
    setError(''); setGenerating(true)
    try {
      const from = new Date(fromDate + 'T00:00:00').toISOString()
      const to = new Date(toDate + 'T23:59:59').toISOString()
      await axios.post(`/api/reports/${PROJECT_ID}/generate`,
        { fromUtc: from, toUtc: to, boards, theme })
      await loadReports()
    } catch (e: unknown) {
      // Safely extract error message — server may return ProblemDetails JSON or HTML.
      let msg: string
      if (axios.isAxiosError(e)) {
        const d = e.response?.data as unknown
        if (typeof d === 'string') msg = d.length > 500 ? `Request failed (${e.response?.status})` : d
        else if (d && typeof d === 'object' && 'title' in d) msg = String((d as { title: string }).title)
        else msg = e.message
      } else msg = String(e)
      setError(msg)
    } finally { setGenerating(false) }
  }

  return (
    <Box>
      <Typography variant="h5" gutterBottom>Reports</Typography>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>

        {/* Generate panel */}
        <Card variant="outlined" sx={{ minWidth: 320, maxWidth: 380 }}>
          <CardContent>
            <Typography variant="h6" gutterBottom>Generate report</Typography>
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
          </CardContent>
        </Card>

        {/* History */}
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Card variant="outlined">
            <CardContent>
              <Stack direction="row" sx={{ alignItems: 'center', mb: 1 }}>
                <Typography variant="h6" sx={{ flex: 1 }}>History ({reports.length})</Typography>
                <Tooltip title="Refresh">
                  <IconButton onClick={() => loadReports()}><RefreshIcon /></IconButton>
                </Tooltip>
              </Stack>
              <Stack spacing={1}>
                {reports.length === 0 && (
                  <Typography variant="body2" color="text.secondary">No reports yet.</Typography>
                )}
                {reports.map(r => (
                  <Card key={r.id} variant="outlined">
                    <CardContent sx={{ pb: '12px !important' }}>
                      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5 }}>
                        <Chip size="small" label={r.status}
                          color={r.status === 'Done' ? 'success' : r.status === 'Failed' ? 'error' : 'warning'} />
                        <Chip size="small" label={r.trigger} variant="outlined" />
                        {r.theme && <Chip size="small" label={r.theme} variant="outlined" color="primary" />}
                        <Typography variant="body2" sx={{ fontWeight: 500, flex: 1 }}>{r.title}</Typography>
                        {r.status === 'Generating' && <CircularProgress size={16} />}
                        <Typography variant="caption" color="text.secondary">
                          {new Date(r.createdAt).toLocaleString()}
                        </Typography>
                      </Stack>

                      <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'center', flexWrap: 'wrap' }}>
                        <Typography variant="caption" color="text.secondary">
                          {new Date(r.fromUtc).toLocaleDateString()} → {new Date(r.toUtc).toLocaleDateString()}
                        </Typography>
                        {r.boards && <Typography variant="caption" color="text.secondary">· {r.boards}</Typography>}
                        {r.status === 'Done' && (
                          <Typography variant="caption" color="text.secondary">· {r.ticketCount} ticket(s)</Typography>
                        )}
                        <Box sx={{ flex: 1 }} />
                        {r.status === 'Done' && (
                          <>
                            <Tooltip title="Open full-screen">
                              <IconButton size="small" onClick={() => setViewing(r)}><OpenInFullIcon fontSize="small" /></IconButton>
                            </Tooltip>
                            <Tooltip title="Download .html">
                              <IconButton size="small" component="a"
                                href={`/api/reports/download/${r.id}`}
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
                    </CardContent>
                  </Card>
                ))}
              </Stack>
            </CardContent>
          </Card>
        </Box>
      </Stack>

      {/* Full-screen viewer */}
      <Dialog open={!!viewing} onClose={() => setViewing(null)} fullScreen>
        <DialogTitle sx={{ display: 'flex', alignItems: 'center', gap: 1, p: 1 }}>
          <Typography variant="subtitle1" sx={{ flex: 1 }}>{viewing?.title}</Typography>
          {viewing && (
            <Button variant="outlined" size="small" startIcon={<DownloadIcon />}
              href={`/api/reports/download/${viewing.id}`} download>Download</Button>
          )}
          <IconButton onClick={() => setViewing(null)}><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent sx={{ p: 0 }}>
          {viewing && (
            <iframe
              src={`/api/reports/html/${viewing.id}`}
              title="report"
              style={{ width: '100%', height: 'calc(100vh - 64px)', border: 0 }}
            />
          )}
        </DialogContent>
      </Dialog>
    </Box>
  )
}
