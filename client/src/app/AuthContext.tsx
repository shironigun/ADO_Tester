import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { getSession, login as apiLogin, logout as apiLogout, type AuthSession } from '../api/auth'
import { getTokenExpiryMs, setToken } from '../api/authToken'

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

  // Don't wait for a failing API call: sign out the moment the token's `exp` passes (an idle page or an
  // SSE-only view never makes one). Hidden tabs throttle timers, so also re-check when the tab is shown.
  useEffect(() => {
    if (!session?.authRequired || !session.authenticated) return
    const exp = getTokenExpiryMs()
    if (exp === null) return
    const expire = () => {
      setToken(null)
      setSession({ authRequired: true, authenticated: false, username: null })
    }
    if (exp <= Date.now()) { expire(); return }
    // setTimeout overflows past ~24.8 days; a longer lifetime just re-arms on the next session change.
    const timer = window.setTimeout(expire, Math.min(exp - Date.now(), 2 ** 31 - 1))
    const onVisible = () => { if (document.visibilityState === 'visible' && Date.now() >= exp) expire() }
    document.addEventListener('visibilitychange', onVisible)
    return () => { window.clearTimeout(timer); document.removeEventListener('visibilitychange', onVisible) }
  }, [session])

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
