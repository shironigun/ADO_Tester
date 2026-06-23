import { useEffect, useMemo, useState } from 'react'
import { Box, Button, Chip, Stack, Typography, useTheme } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import RuleIcon from '@mui/icons-material/RuleOutlined'
import BugReportIcon from '@mui/icons-material/BugReportOutlined'
import AssessmentIcon from '@mui/icons-material/AssessmentOutlined'
import MenuBookIcon from '@mui/icons-material/MenuBookOutlined'
import HistoryIcon from '@mui/icons-material/HistoryOutlined'
import ArrowForwardIcon from '@mui/icons-material/ArrowForward'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import StatTile from '../components/StatTile'
import StatusChip from '../components/StatusChip'
import MiniDonut, { type DonutSegment } from '../components/MiniDonut'
import EmptyState from '../components/EmptyState'
import { Stagger, StaggerItem } from '../motion/primitives'
import { PROJECT_ID } from '../config/env'
import { listSessions as listAuthorSessions, type SessionSummary } from '../api/authorTests'
import { listSessions as listBugSessions } from '../api/findBugs'
import { listReports, type Report } from '../api/reports'
import { listDocs, type Doc } from '../api/knowledgebase'

interface ActivityRow { key: string; when: number; kind: string; status: string; text: string; to: string }

export default function ProjectsPage() {
  const theme = useTheme()
  const [authorSessions, setAuthorSessions] = useState<SessionSummary[]>([])
  const [bugSessions, setBugSessions] = useState<SessionSummary[]>([])
  const [reports, setReports] = useState<Report[]>([])
  const [docs, setDocs] = useState<Doc[]>([])

  useEffect(() => {
    listAuthorSessions(PROJECT_ID, 50).then(setAuthorSessions).catch(() => {})
    listBugSessions(PROJECT_ID, 50).then(setBugSessions).catch(() => {})
    listReports(PROJECT_ID).then(setReports).catch(() => {})
    listDocs(PROJECT_ID).then(setDocs).catch(() => {})
  }, [])

  // Test-session status breakdown for the donut.
  const donut: DonutSegment[] = useMemo(() => {
    const inFlight = ['Analyzing', 'Drafting', 'Pushing', 'AwaitingTester']
    let done = 0, failed = 0, running = 0, other = 0
    for (const s of authorSessions) {
      if (s.status === 'Done' || s.status === 'Drafted') done++
      else if (s.status === 'Failed') failed++
      else if (inFlight.includes(s.status)) running++
      else other++
    }
    return [
      { label: 'Done', value: done, color: theme.palette.success.main },
      { label: 'Failed', value: failed, color: theme.palette.error.main },
      { label: 'Running', value: running, color: theme.palette.warning.main },
      { label: 'Other', value: other, color: theme.palette.text.disabled },
    ].filter(s => s.value > 0)
  }, [authorSessions, theme])

  const activity: ActivityRow[] = useMemo(() => {
    const rows: ActivityRow[] = []
    for (const s of authorSessions) rows.push({
      key: `a${s.id}`, when: +new Date(s.createdAt), kind: 'Test session',
      status: s.status, text: `#${s.workItemId} — ${s.workItemTitle}`, to: '/author-tests',
    })
    for (const s of bugSessions) rows.push({
      key: `b${s.id}`, when: +new Date(s.createdAt), kind: 'Bug session',
      status: s.status, text: `#${s.workItemId} — ${s.workItemTitle}`, to: '/find-bugs',
    })
    for (const r of reports) rows.push({
      key: `r${r.id}`, when: +new Date(r.createdAt), kind: 'Report',
      status: r.status, text: r.title, to: '/reports',
    })
    return rows.sort((a, b) => b.when - a.when).slice(0, 7)
  }, [authorSessions, bugSessions, reports])

  const QUICK = [
    { label: 'Author Tests', to: '/author-tests', icon: <RuleIcon /> },
    { label: 'Find Bugs', to: '/find-bugs', icon: <BugReportIcon /> },
    { label: 'Reports', to: '/reports', icon: <AssessmentIcon /> },
    { label: 'Knowledgebase', to: '/knowledgebase', icon: <MenuBookIcon /> },
  ]

  return (
    <Box sx={{ pb: 4 }}>
      <PageHeader
        title="Dashboard"
        subtitle="Live snapshot of the Your Project workspace — sessions, bugs, reports, and recent activity."
      />

      {/* KPI tiles */}
      <Stagger>
        <Box sx={{
          display: 'grid', gap: 2, mb: 3,
          gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, 1fr)' },
        }}>
          <StaggerItem>
            <StatTile icon={<RuleIcon />} label="Test sessions" value={authorSessions.length}
              color="primary.main" sub="Analyst → Tester runs" />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<BugReportIcon />} label="Bug sessions" value={bugSessions.length}
              color="error.main" sub="Bug Writer runs" />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<AssessmentIcon />} label="Reports" value={reports.length}
              color="info.main" sub={`${reports.filter(r => r.status === 'Done').length} ready`} />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<MenuBookIcon />} label="KB documents" value={docs.length}
              color="secondary.main" sub="Domain context" />
          </StaggerItem>
        </Box>
      </Stagger>

      <Box sx={{
        display: 'grid', gap: 2,
        gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, mb: 3,
      }}>
        {/* Donut: test session outcomes */}
        <Section title="Test session outcomes">
          {donut.length === 0 ? (
            <EmptyState compact icon={<RuleIcon />} title="No sessions yet"
              hint="Author your first tests to see outcomes here." />
          ) : (
            <Box sx={{ pt: 1 }}>
              <MiniDonut segments={donut} centerLabel="sessions" />
            </Box>
          )}
        </Section>

        {/* Recent activity */}
        <Section title="Recent activity">
          {activity.length === 0 ? (
            <EmptyState compact icon={<HistoryIcon />} title="Nothing yet"
              hint="Sessions and reports will appear here." />
          ) : (
            <Stagger>
              <Stack spacing={0.5}>
                {activity.map(a => (
                  <StaggerItem key={a.key}>
                    <Box component={RouterLink} to={a.to} sx={{
                      display: 'block', textDecoration: 'none', color: 'inherit',
                      p: 1, borderRadius: 1.5,
                      transition: 'background-color 160ms',
                      '&:hover': { bgcolor: 'action.hover' },
                    }}>
                      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                        <StatusChip value={a.status} />
                        <Typography variant="caption" color="text.secondary" sx={{ minWidth: 78 }}>
                          {a.kind}
                        </Typography>
                        <Typography variant="body2" noWrap sx={{ flex: 1 }}>{a.text}</Typography>
                      </Stack>
                    </Box>
                  </StaggerItem>
                ))}
              </Stack>
            </Stagger>
          )}
        </Section>
      </Box>

      {/* Quick actions */}
      <Section title="Quick actions" sx={{ mb: 3 }}>
        <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1.5 }}>
          {QUICK.map(q => (
            <Button key={q.to} component={RouterLink} to={q.to}
              variant="outlined" startIcon={q.icon} endIcon={<ArrowForwardIcon sx={{ fontSize: 16 }} />}>
              {q.label}
            </Button>
          ))}
        </Stack>
      </Section>

      {/* Project card */}
      <Section
        title={
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <span>Your Project</span>
            <Chip size="small" label="Default" color="primary" />
          </Stack>
        }
        subtitle="v1 runs against a single seeded project. Multi-project support is on the roadmap."
        sx={{ maxWidth: 760 }}
      >
        <Typography variant="body2" color="text.secondary">
          ADO Organization: <strong>YourOrg</strong>
        </Typography>
        <Typography variant="body2" color="text.secondary">
          ADO Project: <strong>Your Project</strong>
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
          PAT loaded from <code>.env</code> (<code>ADOACCESS_PAT</code>). Configure connection details
          in <code>Settings → Azure DevOps</code>.
        </Typography>
      </Section>
    </Box>
  )
}
