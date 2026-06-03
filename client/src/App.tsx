import { BrowserRouter, Routes, Route, NavLink } from 'react-router-dom'
import {
  AppBar, Box, CssBaseline, Drawer, List, ListItem, ListItemButton,
  ListItemText, Toolbar, Typography, ThemeProvider, createTheme,
} from '@mui/material'
import DiagnosticsPage from './pages/DiagnosticsPage'
import KnowledgebasePage from './pages/KnowledgebasePage'
import AuthorTestsPage from './pages/AuthorTestsPage'
import ProviderSwitcher from './components/ProviderSwitcher'

const DRAWER_WIDTH = 220

const theme = createTheme({
  palette: { mode: 'light', primary: { main: '#1565c0' } },
  typography: { fontFamily: '"Inter", "Roboto", "Helvetica", "Arial", sans-serif' },
})

const navItems = [
  { label: 'Diagnostics', path: '/diagnostics' },
  { label: 'Projects',    path: '/projects' },
  { label: 'Knowledgebase', path: '/knowledgebase' },
  { label: 'Author Tests', path: '/author-tests' },
  { label: 'Reports',     path: '/reports' },
]

export default function App() {
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <BrowserRouter>
        <Box sx={{ display: 'flex' }}>
          <AppBar position="fixed" sx={{ zIndex: t => t.zIndex.drawer + 1 }}>
            <Toolbar sx={{ gap: 2 }}>
              <Typography variant="h6" noWrap sx={{ flexGrow: 1 }}>QA Automation — Ideal Agile</Typography>
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
              <Route path="/" element={<DiagnosticsPage />} />
              <Route path="/diagnostics" element={<DiagnosticsPage />} />
              <Route path="/knowledgebase" element={<KnowledgebasePage />} />
              <Route path="/author-tests" element={<AuthorTestsPage />} />
              {/* S3 pages added per sprint */}
              <Route path="*" element={<Typography>Coming soon.</Typography>} />
            </Routes>
          </Box>
        </Box>
      </BrowserRouter>
    </ThemeProvider>
  )
}
