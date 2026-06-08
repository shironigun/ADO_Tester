import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ThemeProvider } from '@mui/material/styles'
import CssBaseline from '@mui/material/CssBaseline'
import { createAppTheme, type ColorMode } from './index'

const STORAGE_KEY = 'qa-color-mode'

interface ColorModeContextValue {
  mode: ColorMode
  toggle: () => void
  setMode: (m: ColorMode) => void
}

const ColorModeContext = createContext<ColorModeContextValue | null>(null)

function resolveInitialMode(): ColorMode {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'light' || saved === 'dark') return saved
  } catch { /* ignore */ }
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches) {
    return 'dark'
  }
  return 'light'
}

/**
 * Owns the colour mode (light/dark), persists it to localStorage, and provides
 * the MUI ThemeProvider + CssBaseline. A plain mode-state approach (not the
 * CSS-variables colorSchemes API) so every page's `sx`/`palette.mode` keeps
 * working unchanged.
 */
export function ColorModeProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<ColorMode>(resolveInitialMode)

  useEffect(() => {
    try { localStorage.setItem(STORAGE_KEY, mode) } catch { /* ignore */ }
  }, [mode])

  const theme = useMemo(() => createAppTheme(mode), [mode])

  const value = useMemo<ColorModeContextValue>(() => ({
    mode,
    toggle: () => setMode(m => (m === 'light' ? 'dark' : 'light')),
    setMode,
  }), [mode])

  return (
    <ColorModeContext.Provider value={value}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {children}
      </ThemeProvider>
    </ColorModeContext.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export function useColorMode(): ColorModeContextValue {
  const ctx = useContext(ColorModeContext)
  if (!ctx) throw new Error('useColorMode must be used within ColorModeProvider')
  return ctx
}
