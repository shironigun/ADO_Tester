import { api } from './client'
import { setToken } from './authToken'

// Web-UI login gate. Mirrors the backend AuthSession contract (GET /api/auth/session).
// `authRequired` = the server enforces login; `authenticated` = the current token is valid.
export interface AuthSession {
  authRequired: boolean
  authenticated: boolean
  username: string | null
  token?: string | null
  expiresAtUtc?: string | null
}

export async function getSession(): Promise<AuthSession> {
  const { data } = await api.get<AuthSession>('/api/auth/session')
  return data
}

/**
 * For callers outside the axios client (EventSource streams): a 401 there never reaches the response
 * interceptor, so ask the server whether we're still signed in and, if not, send the app to login.
 * /api/auth/session is anonymous, so an expired token answers 200 {authenticated:false}, not 401.
 */
export async function signOutIfSessionInvalid(): Promise<void> {
  try {
    const s = await getSession()
    if (s.authRequired && !s.authenticated) {
      setToken(null)
      window.dispatchEvent(new Event('auth:unauthorized'))
    }
  } catch { /* server unreachable: not a sign-out signal */ }
}

export async function login(username: string, password: string): Promise<AuthSession> {
  const { data } = await api.post<AuthSession>('/api/auth/login', { username, password })
  if (data.token) setToken(data.token)
  return data
}

export async function logout(): Promise<void> {
  try {
    await api.post('/api/auth/logout')
  } finally {
    setToken(null)
  }
}
