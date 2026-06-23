import axios from 'axios'
import { API_BASE } from '../config/env'
import { logger } from '../logger'

// Centralized axios instance (F1). Every per-feature api/* module makes its calls through this client,
// so the base URL is configured in one place (config/env.ts → VITE_QA_API) and failed responses are
// logged consistently. baseURL is empty in dev (the Vite proxy forwards /api → :5250).
//
// During the F1 migration, call sites move from the global `axios` to this instance one feature at a
// time; the global-axios error interceptor in logger.ts stays until every caller is migrated, then is
// removed. (Instance interceptors don't fire for global-axios calls and vice-versa, so no double-log.)
export const api = axios.create({ baseURL: API_BASE })

api.interceptors.response.use(
  (r) => r,
  (error) => {
    const cfg = error?.config ?? {}
    const status: number | undefined = error?.response?.status
    const url = `${cfg.baseURL ?? ''}${cfg.url ?? ''}`
    const method = (cfg.method ?? '').toUpperCase()
    const msg = `API ${method} ${url} failed${status ? ` (${status})` : ''}: ${error?.message ?? ''}`
    const context = { status, data: error?.response?.data }
    if (status != null && status >= 400 && status < 500) {
      logger.warn(msg, { source: 'axios', context })
    } else {
      logger.error(msg, { source: 'axios', context, stack: error?.stack })
    }
    return Promise.reject(error)
  },
)
