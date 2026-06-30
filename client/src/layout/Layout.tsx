import { AppBar, Box, Drawer, IconButton, Stack, Toolbar, Tooltip, Typography } from '@mui/material'
import { NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { AnimatePresence, motion } from 'framer-motion'
import LightModeIcon from '@mui/icons-material/LightModeOutlined'
import DarkModeIcon from '@mui/icons-material/DarkModeOutlined'
import DashboardIcon from '@mui/icons-material/SpaceDashboardOutlined'
import MenuBookIcon from '@mui/icons-material/MenuBookOutlined'
import RuleIcon from '@mui/icons-material/RuleOutlined'
import BugReportIcon from '@mui/icons-material/BugReportOutlined'
import AssessmentIcon from '@mui/icons-material/AssessmentOutlined'
import SettingsIcon from '@mui/icons-material/SettingsOutlined'
import ScienceIcon from '@mui/icons-material/ScienceOutlined'
import AutoAwesomeIcon from '@mui/icons-material/AutoAwesomeOutlined'
import PlayCircleIcon from '@mui/icons-material/PlayCircleOutlined'
import AccountTreeIcon from '@mui/icons-material/AccountTreeOutlined'
import type { ReactNode } from 'react'

import ProviderSwitcher from '../components/ProviderSwitcher'
import EmptyState from '../components/EmptyState'
import { PageTransition } from '../motion/primitives'
import { useColorMode } from '../theme/ColorModeContext'
import { paths } from '../routes/paths'

import SettingsPage from '../pages/SettingsPage'
import KnowledgebasePage from '../pages/KnowledgebasePage'
import AuthorTestsPage from '../pages/AuthorTestsPage'
import ReportsPage from '../pages/ReportsPage'
import ProjectsPage from '../pages/ProjectsPage'
import FindBugsPage from '../pages/FindBugsPage'
import QaLoopPage from '../pages/QaLoopPage'
import QaRunsPage from '../pages/QaRunsPage'
import QaPipelinesPage from '../pages/QaPipelinesPage'

const DRAWER_WIDTH = 244

interface NavItem { label: string; path: string; icon: ReactNode }
const navItems: NavItem[] = [
  { label: 'Dashboard', path: paths.dashboard, icon: <DashboardIcon /> },
  { label: 'Knowledgebase', path: paths.knowledgebase, icon: <MenuBookIcon /> },
  { label: 'QA Loop', path: paths.qaLoop, icon: <AutoAwesomeIcon /> },
  { label: 'QA Runs', path: paths.qaRuns, icon: <PlayCircleIcon /> },
  { label: 'QA Pipelines', path: paths.qaPipelines, icon: <AccountTreeIcon /> },
  { label: 'Author Tests', path: paths.authorTests, icon: <RuleIcon /> },
  { label: 'Find Bugs', path: paths.findBugs, icon: <BugReportIcon /> },
  { label: 'Reports', path: paths.reports, icon: <AssessmentIcon /> },
  { label: 'Settings', path: paths.settings, icon: <SettingsIcon /> },
]

function ColorModeToggle() {
  const { mode, toggle } = useColorMode()
  return (
    <Tooltip title={mode === 'light' ? 'Switch to dark mode' : 'Switch to light mode'}>
      <IconButton onClick={toggle} color="inherit" sx={{ ml: 0.5 }}>
        <Box component={motion.span} key={mode}
          initial={{ rotate: -90, opacity: 0 }} animate={{ rotate: 0, opacity: 1 }}
          transition={{ type: 'spring', stiffness: 300, damping: 20 }}
          sx={{ display: 'flex' }}>
          {mode === 'light' ? <DarkModeIcon /> : <LightModeIcon />}
        </Box>
      </IconButton>
    </Tooltip>
  )
}

function NavRow({ item }: { item: NavItem }) {
  return (
    <NavLink to={item.path} style={{ textDecoration: 'none' }}>
      {({ isActive }) => (
        <Box sx={{ position: 'relative', px: 1.5, py: 0.25 }}>
          {isActive && (
            <Box component={motion.div} layoutId="nav-active"
              transition={{ type: 'spring', stiffness: 380, damping: 32 }}
              sx={{
                position: 'absolute', inset: 0, mx: 1.5,
                borderRadius: 2, bgcolor: 'primary.lighter',
                border: theme => `1px solid ${theme.palette.primary.main}33`,
              }} />
          )}
          <Stack direction="row" spacing={1.5}
            sx={{
              position: 'relative', alignItems: 'center', px: 1.5, py: 1.1, borderRadius: 2,
              color: isActive ? 'primary.main' : 'text.secondary',
              transition: 'color 200ms',
              '&:hover': { color: isActive ? 'primary.main' : 'text.primary' },
              '& svg': { fontSize: 21 },
            }}>
            {item.icon}
            <Typography variant="body2" sx={{ fontWeight: isActive ? 700 : 500 }}>
              {item.label}
            </Typography>
          </Stack>
        </Box>
      )}
    </NavLink>
  )
}

export default function Layout() {
  const location = useLocation()

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="fixed" sx={{ zIndex: t => t.zIndex.drawer + 1 }}>
        <Toolbar sx={{ gap: 2 }}>
          <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', flexGrow: 1, minWidth: 0 }}>
            <Box sx={{
              width: 30, height: 30, borderRadius: 2, display: 'flex',
              alignItems: 'center', justifyContent: 'center',
              bgcolor: 'primary.main', color: 'primary.contrastText',
            }}>
              <ScienceIcon sx={{ fontSize: 19 }} />
            </Box>
            <Typography variant="h6" noWrap sx={{ fontFamily: theme => theme.typography.h6.fontFamily }}>
              QA Automation
              <Box component="span" sx={{ color: 'text.secondary', fontWeight: 500 }}> · Ideal Agile</Box>
            </Typography>
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ display: { xs: 'none', sm: 'block' } }}>
            LLM
          </Typography>
          <ProviderSwitcher />
          <ColorModeToggle />
        </Toolbar>
      </AppBar>

      <Drawer variant="permanent"
        sx={{ width: DRAWER_WIDTH, flexShrink: 0,
              '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' } }}>
        <Toolbar />
        <Box sx={{ py: 1.5, overflow: 'auto' }}>
          <Typography variant="overline" sx={{ px: 3, color: 'text.disabled', fontSize: '0.66rem' }}>
            Workspace
          </Typography>
          <Stack sx={{ mt: 0.5 }}>
            {navItems.map(item => <NavRow key={item.path} item={item} />)}
          </Stack>
        </Box>
      </Drawer>

      <Box component="main" sx={{ flexGrow: 1, minWidth: 0, p: { xs: 2, md: 3 }, mt: 8 }}>
        <AnimatePresence mode="wait">
          <PageTransition key={location.pathname}>
            <Routes location={location}>
              <Route path="/" element={<SettingsPage />} />
              <Route path={paths.settings} element={<SettingsPage />} />
              {/* Back-compat: old /diagnostics URL still resolves */}
              <Route path="/diagnostics" element={<SettingsPage />} />
              <Route path={paths.dashboard} element={<ProjectsPage />} />
              <Route path={paths.knowledgebase} element={<KnowledgebasePage />} />
              <Route path={paths.qaLoop} element={<QaLoopPage />} />
              <Route path={paths.qaRuns} element={<QaRunsPage />} />
              <Route path={paths.qaPipelines} element={<QaPipelinesPage />} />
              <Route path={paths.authorTests} element={<AuthorTestsPage />} />
              <Route path={paths.findBugs} element={<FindBugsPage />} />
              <Route path={paths.reports} element={<ReportsPage />} />
              <Route path="*" element={<EmptyState title="Coming soon" hint="This page isn't available yet." />} />
            </Routes>
          </PageTransition>
        </AnimatePresence>
      </Box>
    </Box>
  )
}
