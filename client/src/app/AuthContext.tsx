import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { getSession, login as apiLogin, logout as apiLogout, type AuthSession } from '../api/auth'

// Auth state for the whole SPA. The login gate (App.tsx) reads `session` to decide whether to show
// the login screen; the AppBar account menu calls `logout`. Login issues a JWT that the api layer
// stores (localStorage) and sends on every request; a 401 clears it and drops back to the login screen.
interface AuthState {
  session: AuthSession | null
  loading: boolean
  login: (username: string, password: string) => Promise<void>
  logout: () => Promise<void>
  refresh: () => Promise<void>
}

const AuthContext = createContext<AuthState | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(null)
  const [loading, setLoading] = useState(true)

  const refresh = useCallback(async () => {
    try {
      setSession(await getSession())
    } catch {
      // Can't reach the server — assume login is required + we're signed out so the gate shows login.
      setSession({ authRequired: true, authenticated: false, username: null })
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])

  // A 401 from any protected call (cookie expired/cleared) is broadcast by the axios interceptor —
  // flip to signed-out so the gate returns to the login screen.
  useEffect(() => {
    const onUnauthorized = () =>
      setSession({ authRequired: true, authenticated: false, username: null })
    window.addEventListener('auth:unauthorized', onUnauthorized)
    return () => window.removeEventListener('auth:unauthorized', onUnauthorized)
  }, [])

  const login = useCallback(async (username: string, password: string) => {
    setSession(await apiLogin(username, password))
  }, [])

  const logout = useCallback(async () => {
    try {
      await apiLogout()
    } finally {
      setSession({ authRequired: true, authenticated: false, username: null })
    }
  }, [])

  return (
    <AuthContext.Provider value={{ session, loading, login, logout, refresh }}>
      {children}
    </AuthContext.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within <AuthProvider>')
  return ctx
}
