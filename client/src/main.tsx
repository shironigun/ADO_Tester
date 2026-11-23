import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Provider } from 'react-redux'
import { QueryClientProvider } from '@tanstack/react-query'
import { MotionConfig } from 'framer-motion'
import { store } from './app/store'
import { queryClient } from './app/queryClient'
import { ColorModeProvider } from './theme/ColorModeContext'
import App from './App.tsx'
import { ErrorBoundary } from './ErrorBoundary'
import { installGlobalErrorLogging } from './logger'
import './index.css'

// Capture uncaught errors, unhandled rejections, and failed API calls → POST /api/logs (ErrorLogs).
installGlobalErrorLogging()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* Redux + React Query providers wrap the app (F0). Slices/queries are populated in F2/F3. */}
    <Provider store={store}>
      <QueryClientProvider client={queryClient}>
        {/* reducedMotion="user" makes every motion component honour the OS
            prefers-reduced-motion setting globally. */}
        <MotionConfig reducedMotion="user">
          <ColorModeProvider>
            <ErrorBoundary>
              <App />
            </ErrorBoundary>
          </ColorModeProvider>
        </MotionConfig>
      </QueryClientProvider>
    </Provider>
  </StrictMode>,
)
