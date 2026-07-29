import { useState, type FormEvent } from 'react'
import { Alert, Box, Button, Card, CardContent, Stack, TextField, Typography } from '@mui/material'
import ScienceIcon from '@mui/icons-material/ScienceOutlined'
import { useAuth } from '../app/AuthContext'

// Full-screen login gate shown by App.tsx when the server requires auth and no valid token is held.
export default function LoginPage() {
  const { login } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login(username, password)
    } catch (err) {
      const status = (err as { response?: { status?: number } })?.response?.status
      setError(status === 401 ? 'Invalid username or password.' : 'Sign in failed. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', bgcolor: 'background.default', p: 2 }}>
      <Card sx={{ width: '100%', maxWidth: 400 }} elevation={3}>
        <CardContent sx={{ p: 4 }}>
          <Box component="form" onSubmit={onSubmit}>
            <Stack spacing={3}>
              <Stack spacing={1.25} sx={{ alignItems: 'center' }}>
                <Box sx={{
                  width: 44, height: 44, borderRadius: 2, display: 'flex',
                  alignItems: 'center', justifyContent: 'center',
                  bgcolor: 'primary.main', color: 'primary.contrastText',
                }}>
                  <ScienceIcon />
                </Box>
                <Typography variant="h6">Sign in to ADO_Tester</Typography>
              </Stack>

              {error && <Alert severity="error">{error}</Alert>}

              <TextField
                label="Username" value={username} onChange={e => setUsername(e.target.value)}
                autoFocus autoComplete="username" fullWidth required />
              <TextField
                label="Password" type="password" value={password} onChange={e => setPassword(e.target.value)}
                autoComplete="current-password" fullWidth required />

              <Button type="submit" variant="contained" size="large"
                disabled={submitting || !username || !password}>
                {submitting ? 'Signing in…' : 'Sign in'}
              </Button>
            </Stack>
          </Box>
        </CardContent>
      </Card>
    </Box>
  )
}
