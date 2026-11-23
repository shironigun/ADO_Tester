// JWT storage for the web-UI login gate. The token is issued by POST /api/auth/login and sent on
// every request — as an Authorization: Bearer header for axios calls, and (because EventSource /
// iframe / download requests can't set headers) as an ?access_token= query param that apiUrl() adds.
// localStorage persists it across reloads; there's no refresh token (re-login when it expires).
const TOKEN_KEY = 'ado_tester_token'

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY)
  } catch {
    return null
  }
}

export function setToken(token: string | null): void {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token)
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    /* storage unavailable (private mode) — token simply won't persist */
  }
}
