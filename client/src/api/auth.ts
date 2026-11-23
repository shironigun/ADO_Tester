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
