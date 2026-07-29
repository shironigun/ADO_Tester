import { BrowserRouter } from 'react-router-dom'
import { Box, CircularProgress } from '@mui/material'
import AppRoutes from './routes/AppRoutes'
import { AuthProvider, useAuth } from './app/AuthContext'
import LoginPage from './pages/LoginPage'

// Login gate: block the app until the auth state is known and (if required) the user is signed in.
function AuthGate() {
  const { session, loading } = useAuth()

  if (loading || !session) {
    return (
      <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center' }}>
        <CircularProgress />
      </Box>
    )
  }

  if (session.authRequired && !session.authenticated) {
    return <LoginPage />
  }

  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  )
}

// App shell: auth gate wraps the router. The route table lives in routes/AppRoutes (F4).
export default function App() {
  return (
    <AuthProvider>
      <AuthGate />
    </AuthProvider>
  )
}
