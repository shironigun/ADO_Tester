import { api } from './client'

// ── Claude CLI subscription auth — /api/claude-auth ──────────────────────────
// Lets an app user sign the local `claude` CLI in/out from the browser (open the URL, paste the code)
// instead of needing `docker exec`. Login is two steps: start → get a URL + loginId, then complete → send
// the pasted code. Status mirrors `claude auth status --json`.

export interface ClaudeAuthStatus {
  available: boolean
  loggedIn: boolean
  authMethod?: string | null
  email?: string | null
  orgName?: string | null
  subscriptionType?: string | null
  error?: string | null
}

export interface ClaudeLoginStart {
  loginId: string
  url: string
}

export interface ClaudeLoginResult {
  success: boolean
  error?: string | null
  status?: ClaudeAuthStatus | null
}

export async function getClaudeAuthStatus(): Promise<ClaudeAuthStatus> {
  const { data } = await api.get<ClaudeAuthStatus>('/api/claude-auth/status')
  return data
}

export async function startClaudeLogin(): Promise<ClaudeLoginStart> {
  const { data } = await api.post<ClaudeLoginStart>('/api/claude-auth/login/start')
  return data
}

export async function completeClaudeLogin(loginId: string, code: string): Promise<ClaudeLoginResult> {
  const { data } = await api.post<ClaudeLoginResult>('/api/claude-auth/login/complete', { loginId, code })
  return data
}

export async function claudeLogout(): Promise<ClaudeAuthStatus> {
  const { data } = await api.post<ClaudeAuthStatus>('/api/claude-auth/logout')
  return data
}
