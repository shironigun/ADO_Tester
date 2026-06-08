import { BrowserRouter, Routes, Route, NavLink } from 'react-router-dom'
import {
  AppBar, Box, CssBaseline, Drawer, List, ListItem, ListItemButton,
  ListItemText, Toolbar, Typography, ThemeProvider, createTheme,
} from '@mui/material'
import SettingsPage from './pages/SettingsPage'
import KnowledgebasePage from './pages/KnowledgebasePage'
import AuthorTestsPage from './pages/AuthorTestsPage'
import ReportsPage from './pages/ReportsPage'
import ProjectsPage from './pages/ProjectsPage'
import FindBugsPage from './pages/FindBugsPage'
import ProviderSwitcher from './components/ProviderSwitcher'

const DRAWER_WIDTH = 220

const theme = createTheme({
  palette: { mode: 'light', primary: { main: '#1565c0' } },
  typography: { fontFamily: '"Inter", "Roboto", "Helvetica", "Arial", sans-serif' },
})

const navItems = [
  { label: 'Projects',    path: '/projects' },
  { label: 'Knowledgebase', path: '/knowledgebase' },
  { label: 'Author Tests', path: '/author-tests' },
  { label: 'Find Bugs',   path: '/find-bugs' },
  { label: 'Reports',     path: '/reports' },
  { label: 'Settings',    path: '/settings' },
]

export default function App() {
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <BrowserRouter>
        <Box sx={{ display: 'flex' }}>
          <AppBar position="fixed" sx={{ zIndex: t => t.zIndex.drawer + 1 }}>
            <Toolbar sx={{ gap: 2 }}>
              <Typography variant="h6" noWrap sx={{ flexGrow: 1 }}>QA Automation — Your Project</Typography>
              <Typography variant="body2" sx={{ opacity: 0.85 }}>LLM:</Typography>
              <ProviderSwitcher />
            </Toolbar>
          </AppBar>

          <Drawer variant="permanent"
            sx={{ width: DRAWER_WIDTH, flexShrink: 0,
                  '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' } }}>
            <Toolbar />
            <List>
              {navItems.map(({ label, path }) => (
                <ListItem key={path} disablePadding>
                  <ListItemButton component={NavLink} to={path}
                    sx={{ '&.active': { bgcolor: 'action.selected', fontWeight: 700 } }}>
                    <ListItemText primary={label} />
                  </ListItemButton>
                </ListItem>
              ))}
            </List>
          </Drawer>

          <Box component="main" sx={{ flexGrow: 1, p: 3, mt: 8 }}>
            <Routes>
              <Route path="/" element={<SettingsPage />} />
              <Route path="/settings" element={<SettingsPage />} />
              {/* Back-compat: old /diagnostics URL still resolves */}
              <Route path="/diagnostics" element={<SettingsPage />} />
              <Route path="/projects" element={<ProjectsPage />} />
              <Route path="/knowledgebase" element={<KnowledgebasePage />} />
              <Route path="/author-tests" element={<AuthorTestsPage />} />
              <Route path="/find-bugs" element={<FindBugsPage />} />
              <Route path="/reports" element={<ReportsPage />} />
              <Route path="*" element={<Typography>Coming soon.</Typography>} />
            </Routes>
          </Box>
        </Box>
      </BrowserRouter>
    </ThemeProvider>
  )
}
