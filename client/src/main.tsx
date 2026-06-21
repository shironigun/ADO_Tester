import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MotionConfig } from 'framer-motion'
import { ColorModeProvider } from './theme/ColorModeContext'
import App from './App.tsx'
import { ErrorBoundary } from './ErrorBoundary'
import { installGlobalErrorLogging } from './logger'
import './index.css'

// Capture uncaught errors, unhandled rejections, and failed API calls → POST /api/logs (ErrorLogs).
installGlobalErrorLogging()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* reducedMotion="user" makes every motion component honour the OS
        prefers-reduced-motion setting globally. */}
    <MotionConfig reducedMotion="user">
      <ColorModeProvider>
        <ErrorBoundary>
          <App />
        </ErrorBoundary>
      </ColorModeProvider>
    </MotionConfig>
  </StrictMode>,
)
