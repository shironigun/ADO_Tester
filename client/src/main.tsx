import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MotionConfig } from 'framer-motion'
import { ColorModeProvider } from './theme/ColorModeContext'
import App from './App.tsx'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* reducedMotion="user" makes every motion component honour the OS
        prefers-reduced-motion setting globally. */}
    <MotionConfig reducedMotion="user">
      <ColorModeProvider>
        <App />
      </ColorModeProvider>
    </MotionConfig>
  </StrictMode>,
)
