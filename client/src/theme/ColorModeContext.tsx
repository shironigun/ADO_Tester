import { useEffect, useMemo, type ReactNode } from 'react'
import { ThemeProvider } from '@mui/material/styles'
import CssBaseline from '@mui/material/CssBaseline'
import { createAppTheme, type ColorMode } from './index'
import { useAppDispatch, useAppSelector } from '../app/hooks'
import { toggleColorMode, setColorMode } from '../app/colorModeSlice'

const STORAGE_KEY = 'qa-color-mode'

/**
 * Owns the MUI ThemeProvider + CssBaseline and persists the colour mode (F3: the mode itself now
 * lives in the Redux colorMode slice — this component only reads it, builds the theme, and writes it
 * back to localStorage). Must be rendered inside the Redux <Provider> (it is, in main.tsx).
 *
 * A plain mode-state approach (not the CSS-variables colorSchemes API) so every page's
 * `sx`/`palette.mode` keeps working unchanged.
 */
export function ColorModeProvider({ children }: { children: ReactNode }) {
  const mode = useAppSelector(s => s.colorMode.mode)

  useEffect(() => {
    try { localStorage.setItem(STORAGE_KEY, mode) } catch { /* ignore */ }
  }, [mode])

  const theme = useMemo(() => createAppTheme(mode), [mode])

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      {children}
    </ThemeProvider>
  )
}

/** Read/toggle the colour mode. Backed by the Redux colorMode slice; public shape unchanged so
 *  consumers (Layout) keep working. */
// eslint-disable-next-line react-refresh/only-export-components
export function useColorMode(): { mode: ColorMode; toggle: () => void; setMode: (m: ColorMode) => void } {
  const dispatch = useAppDispatch()
  const mode = useAppSelector(s => s.colorMode.mode)
  return {
    mode,
    toggle: () => dispatch(toggleColorMode()),
    setMode: (m: ColorMode) => dispatch(setColorMode(m)),
  }
}
